namespace AirSend.Core.Audio;

public sealed record DecodedFrame(bool IsUncompressed, short[] Samples, int SampleCount)
{
    public int Channels => SampleCount == 0 ? 0 : Samples.Length / SampleCount;
}

/// <summary>
/// Decoder for the ALAC frame layout, structured exactly like ffmpeg's
/// implementation (<c>libavcodec/alac.c</c>: <c>alac_decode_frame</c> plus
/// <c>decode_element</c>) so the C# encoder is checked against an independent
/// reader instead of against itself.
/// </summary>
/// <remarks>
/// Only the uncompressed path that <see cref="AlacEncoder"/> emits is implemented;
/// compressed frames come back with <c>IsUncompressed == false</c>.
/// </remarks>
public static class AlacVerbatimDecoder
{
    private const uint ElementSce = 0;
    private const uint ElementCpe = 1;
    private const uint ElementEnd = 7;

    public static DecodedFrame Decode(
        ReadOnlySpan<byte> frame,
        int sampleSizeBits = 16,
        int maxSamplesPerFrame = 352)
    {
        var reader = new BitReader(frame);
        var samples = new List<short>();
        int sampleCount = 0;
        bool uncompressed = true;
        bool sawEnd = false;

        // Element loop, mirroring alac_decode_frame().
        while (reader.BitsLeft >= 3)
        {
            uint element = reader.Read(3);
            if (element == ElementEnd)
            {
                sawEnd = true;
                break;
            }

            int channels = element == ElementCpe ? 2 : element == ElementSce ? 1 : 0;
            if (channels == 0)
            {
                throw new FormatException($"unsupported ALAC element {element}");
            }

            // ref: BitReader is a ref struct, so passing it by value would leave the
            // element loop reading the same offset for every element.
            DecodedFrame decoded = DecodeElement(ref reader, channels, sampleSizeBits, maxSamplesPerFrame);
            samples.AddRange(decoded.Samples);
            sampleCount = decoded.SampleCount;
            uncompressed &= decoded.IsUncompressed;
        }

        if (!sawEnd)
        {
            throw new FormatException("no end tag found: incomplete ALAC packet");
        }

        return new DecodedFrame(uncompressed, samples.ToArray(), sampleCount);
    }

    private static DecodedFrame DecodeElement(
        ref BitReader reader,
        int channels,
        int sampleSizeBits,
        int maxSamplesPerFrame)
    {
        reader.Skip(4);    // element instance tag
        reader.Skip(12);   // unused header bits
        bool hasSize = reader.Read(1) != 0;
        int extraBits = (int)reader.Read(2) << 3;
        bool isCompressed = reader.Read(1) == 0;

        int sampleSize = sampleSizeBits - extraBits;
        if (sampleSize is < 1 or > 32)
        {
            throw new FormatException($"invalid sample size {sampleSize}");
        }

        int sampleCount = hasSize ? (int)reader.Read(32) : maxSamplesPerFrame;
        if (sampleCount <= 0 || sampleCount > maxSamplesPerFrame)
        {
            throw new FormatException($"invalid samples per frame: {sampleCount}");
        }

        if (isCompressed)
        {
            return new DecodedFrame(false, [], sampleCount);
        }

        var samples = new short[sampleCount * channels];
        for (int i = 0; i < sampleCount; i++)
        {
            for (int channel = 0; channel < channels; channel++)
            {
                samples[i * channels + channel] = unchecked((short)reader.ReadSigned(sampleSize));
            }
        }

        return new DecodedFrame(true, samples, sampleCount);
    }

    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _bitOffset;

        public int BitsLeft => (_data.Length * 8) - _bitOffset;

        public void Skip(int count) => _bitOffset += count;

        public uint Read(int count)
        {
            uint value = 0;
            for (int i = 0; i < count; i++)
            {
                int byteIndex = _bitOffset >> 3;
                int bitIndex = 7 - (_bitOffset & 7);
                uint bit = byteIndex < _data.Length ? (uint)((_data[byteIndex] >> bitIndex) & 1) : 0u;
                value = (value << 1) | bit;
                _bitOffset++;
            }

            return value;
        }

        /// <summary>Reads a two's complement value and sign extends it, like <c>get_sbits_long</c>.</summary>
        public int ReadSigned(int count)
        {
            uint raw = Read(count);
            int shift = 32 - count;
            return (int)(raw << shift) >> shift;
        }
    }
}
