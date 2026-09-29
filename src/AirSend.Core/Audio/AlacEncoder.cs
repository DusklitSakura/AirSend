using System.Buffers.Binary;

namespace AirSend.Core.Audio;

/// <summary>
/// ALAC magic cookie (the <c>asc</c> stream description AirPlay sends in SETUP).
/// </summary>
/// <remarks>
/// Layout, shared with the ALAC decoder in the receiver:
/// <code>
/// u32 frameLength | u8 compatibleVersion | u8 bitDepth | u8 pb | u8 mb | u8 kb
/// u8 channels | u16 maxRun | u32 maxFrameBytes | u32 avgBitRate | u32 sampleRate
/// </code>
/// </remarks>
public static class AlacMagicCookie
{
    public const int StreamInfoSize = 24;

    public static byte[] BuildStreamInfo(
        int frameLength,
        int bitDepth,
        int channels,
        int sampleRate)
    {
        var cookie = new byte[StreamInfoSize];
        BinaryPrimitives.WriteUInt32BigEndian(cookie.AsSpan(0), (uint)frameLength);
        cookie[4] = 0;                       // compatible version
        cookie[5] = (byte)bitDepth;
        cookie[6] = 40;                      // pb
        cookie[7] = 10;                      // mb
        cookie[8] = 14;                      // kb
        cookie[9] = (byte)channels;
        BinaryPrimitives.WriteUInt16BigEndian(cookie.AsSpan(10), 255);   // maxRun
        BinaryPrimitives.WriteUInt32BigEndian(cookie.AsSpan(12), 0);     // maxFrameBytes
        BinaryPrimitives.WriteUInt32BigEndian(cookie.AsSpan(16), 0);     // avgBitRate
        BinaryPrimitives.WriteUInt32BigEndian(cookie.AsSpan(20), (uint)sampleRate);
        return cookie;
    }

    /// <summary>Wraps the stream info in an <c>alac</c> atom, as some receivers expect.</summary>
    public static byte[] BuildAtom(int frameLength, int bitDepth, int channels, int sampleRate)
    {
        byte[] streamInfo = BuildStreamInfo(frameLength, bitDepth, channels, sampleRate);
        var atom = new byte[4 + 4 + streamInfo.Length];
        BinaryPrimitives.WriteUInt32BigEndian(atom.AsSpan(0), (uint)(atom.Length - 4));
        "alac"u8.CopyTo(atom.AsSpan(4));
        streamInfo.CopyTo(atom, 8);
        return atom;
    }

    public static (int FrameLength, int BitDepth, int Channels, int SampleRate) Parse(ReadOnlySpan<byte> cookie)
    {
        // Tolerate the 4 byte size + 'alac' prefix.
        if (cookie.Length >= StreamInfoSize + 8 &&
            cookie[4] == (byte)'a' && cookie[5] == (byte)'l' && cookie[6] == (byte)'a' && cookie[7] == (byte)'c')
        {
            cookie = cookie[8..];
        }

        if (cookie.Length < StreamInfoSize)
        {
            throw new FormatException("ALAC cookie too short");
        }

        return (
            (int)BinaryPrimitives.ReadUInt32BigEndian(cookie),
            cookie[5],
            cookie[9],
            (int)BinaryPrimitives.ReadUInt32BigEndian(cookie[20..]));
    }
}

/// <summary>
/// ALAC encoder.
/// </summary>
/// <remarks>
/// <para>
/// This port implements the <em>uncompressed</em> ("verbatim") frame mode of the
/// ALAC bitstream: the frame header declares <c>isNotCompressed = 1</c> and the
/// samples are stored as raw big-endian PCM. Apple's reference decoder supports
/// that mode explicitly (it is what the reference encoder emits for incompressible
/// audio), and it is what <c>crates/audio-encode</c> in the upstream Rust project
/// will fall back to as well.
/// </para>
/// <para>
/// The adaptive predictor path (the compressed mode that gives ALAC its typical
/// 40-50 % saving) is not implemented yet: it needs the residual/Rice coding part
/// of the reference encoder, and it cannot be validated without a receiver.
/// See PORTING.md for the verification status.
/// </para>
/// </remarks>
public sealed class AlacEncoder
{
    private readonly int _channels;
    private readonly int _bitDepth;
    private readonly int _framesPerPacket;
    private readonly byte[] _cookie;

    public AlacEncoder(AudioFormatDescription format)
    {
        if (format.Codec != AudioCodec.Alac)
        {
            throw new NotSupportedException($"codec {format.Codec} is not supported");
        }

        _channels = format.Channels;
        _bitDepth = format.BitDepth;
        _framesPerPacket = format.FramesPerPacket;
        _cookie = AlacMagicCookie.BuildStreamInfo(_framesPerPacket, _bitDepth, _channels, format.SampleRate);
    }

    public byte[] MagicCookie => _cookie;

    public int FramesPerPacket => _framesPerPacket;

    /// <summary>Encodes one frame of interleaved 16 bit PCM samples.</summary>
    /// <remarks>
    /// <para>
    /// A frame is a sequence of channel elements terminated by an end tag, exactly
    /// as Apple's decoder (and ffmpeg's port, <c>libavcodec/alac.c</c>) reads it:
    /// </para>
    /// <code>
    /// per element:
    ///   3 bits   element type        (0 = SCE mono, 1 = CPE stereo)
    ///   4 bits   element instance tag
    ///   12 bits  unused
    ///   1 bit    has_size            (only partial frames carry their length)
    ///   2 bits   extra_bits &gt;&gt; 3   (0 for 16 bit samples)
    ///   1 bit    is_not_compressed   (1 = uncompressed)
    ///   32 bits  sample count        (only when has_size)
    ///   N*ch     16 bit samples, interleaved, big endian
    /// 3 bits   end tag               (TYPE_END = 7)
    /// </code>
    /// <para>
    /// The 3 bit element tag is easy to forget when writing an uncompressed frame,
    /// and without it the receiver's bit reader is misaligned from the very first
    /// bit: the session is accepted but only silence comes out.
    /// </para>
    /// </remarks>
    public byte[] EncodeFrame(ReadOnlySpan<short> interleavedSamples)
    {
        int sampleCount = interleavedSamples.Length / _channels;
        bool partialFrame = sampleCount != _framesPerPacket;
        const uint ElementCpe = 1;
        const uint ElementEnd = 7;

        var writer = new BitWriter(interleavedSamples.Length * 2 + 16);

        writer.WriteBits(_channels == 2 ? ElementCpe : 0, 3); // channel pair / single channel
        writer.WriteBits(0, 4);      // element instance tag
        writer.WriteBits(0, 12);     // unused header bits
        writer.WriteBits(partialFrame ? 1u : 0u, 1);  // has_size
        writer.WriteBits(0, 2);      // extra_bits = 0 → sample size stays 16 bit
        writer.WriteBits(1, 1);      // is_not_compressed = 1 → uncompressed frame

        if (partialFrame)
        {
            writer.WriteBits((uint)sampleCount, 32);
        }

        // Interleaved, sample by sample: the decoder reads for each sample every channel.
        foreach (short sample in interleavedSamples)
        {
            writer.WriteBits((ushort)sample, _bitDepth);
        }

        writer.WriteBits(ElementEnd, 3); // frame end tag
        return writer.ToArray();
    }

    /// <summary>Encodes a whole buffer, splitting it into <c>framesPerPacket</c> sized frames.</summary>
    public IReadOnlyList<byte[]> Encode(ReadOnlySpan<short> interleavedSamples)
    {
        int samplesPerFrame = _framesPerPacket * _channels;
        var frames = new List<byte[]>();

        for (int offset = 0; offset < interleavedSamples.Length; offset += samplesPerFrame)
        {
            int length = Math.Min(samplesPerFrame, interleavedSamples.Length - offset);
            frames.Add(EncodeFrame(interleavedSamples.Slice(offset, length)));
        }

        return frames;
    }

    private sealed class BitWriter(int capacity)
    {
        private readonly List<byte> _bytes = new(capacity);
        private ulong _accumulator;
        private int _bits;

        public void WriteBits(uint value, int count)
        {
            if (count <= 0 || count > 32)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            // A ulong accumulator keeps the 32 bit "samples in frame" field plus
            // the up-to-7 pending bits without losing the high byte.
            ulong masked = count == 32 ? value : value & ((1u << count) - 1);
            _accumulator = (_accumulator << count) | masked;
            _bits += count;

            while (_bits >= 8)
            {
                _bits -= 8;
                _bytes.Add((byte)((_accumulator >> _bits) & 0xFF));
            }

            _accumulator &= _bits == 0 ? 0UL : (1UL << _bits) - 1;
        }

        public byte[] ToArray()
        {
            if (_bits > 0)
            {
                _bytes.Add((byte)(_accumulator << (8 - _bits)));
                _bits = 0;
            }

            return _bytes.ToArray();
        }
    }
}
