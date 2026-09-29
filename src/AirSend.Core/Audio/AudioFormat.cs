namespace AirSend.Core.Audio;

/// <summary>PCM format requested from the capture backend.</summary>
public readonly record struct CaptureFormat(int SampleRate, int Channels)
{
    /// <summary>AirPlay wants 44.1 kHz / 16 bit / stereo.</summary>
    public static CaptureFormat AirPlayDefault => new(44_100, 2);

    public override string ToString() => $"{SampleRate} Hz / {Channels} ch";
}

/// <summary>Interleaved 16-bit PCM frame straight out of the capture backend.</summary>
public sealed class CapturedFrame(short[] samples, int channels, int sampleRate)
{
    public short[] Samples { get; } = samples;

    public int Channels { get; } = channels;

    public int SampleRate { get; } = sampleRate;

    public int FrameCount => Channels == 0 ? 0 : Samples.Length / Channels;

    public byte[] ToLittleEndianBytes()
    {
        var bytes = new byte[Samples.Length * 2];
        for (int i = 0; i < Samples.Length; i++)
        {
            bytes[i * 2] = (byte)(Samples[i] & 0xFF);
            bytes[i * 2 + 1] = (byte)((Samples[i] >> 8) & 0xFF);
        }

        return bytes;
    }
}

public enum AudioCodec
{
    /// <summary>ALAC, the only codec HomePod accepts on the AirPlay 2 audio stream.</summary>
    Alac,
}

public readonly record struct AudioFormatDescription(
    AudioCodec Codec,
    int SampleRate,
    int BitDepth,
    int Channels,
    int FramesPerPacket)
{
    public static AudioFormatDescription AirPlayDefault => new(
        AudioCodec.Alac,
        Streaming.LatencyProfile.SampleRate,
        16,
        Streaming.LatencyProfile.Channels,
        Streaming.LatencyProfile.FramesPerPacket);
}
