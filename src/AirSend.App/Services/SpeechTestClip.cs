using System.Globalization;
using AirSend.Core.Logging;
using SpeechSynthesizer = System.Speech.Synthesis.SpeechSynthesizer;

namespace AirSend.Services;

/// <summary>
/// Renders a short spoken sentence with the Windows speech synthesizer and returns
/// raw 44.1 kHz / stereo / 16-bit PCM, ready to be streamed to a receiver.
/// </summary>
/// <remarks>
/// The voice is picked to match the interface language (Chinese UI → zh-CN voice,
/// Spanish → es-*, English → en-*), falling back to the first enabled voice when
/// the machine has no voice for that language. Nothing is played on the PC: the
/// synthesizer writes to a memory stream.
/// </remarks>
public static class SpeechTestClip
{
    // Synthesizing the first time loads SAPI and the voice (~8 s on a cold start),
    // so the clip is cached per language for the rest of the run.
    private static readonly Dictionary<string, (short[] Samples, int SampleRate, int Channels)?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static (short[] Samples, int SampleRate, int Channels)? TrySynthesize(string text, string languageTag)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string key = $"{languageTag}|{text}";
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var clip = Synthesize(text, languageTag);

        lock (Cache)
        {
            Cache[key] = clip;
        }

        return clip;
    }

    private static (short[] Samples, int SampleRate, int Channels)? Synthesize(string text, string languageTag)
    {
        try
        {
            using var synthesizer = new SpeechSynthesizer();
            SelectVoice(synthesizer, languageTag);

            using var buffer = new MemoryStream();
            // SetOutputToWaveStream writes a complete RIFF header with real chunk
            // sizes; the streaming variant leaves them at 0 and cannot be parsed.
            synthesizer.SetOutputToWaveStream(buffer);
            synthesizer.Speak(text);
            synthesizer.SetOutputToNull();

            short[]? samples = TryReadWavePcm(buffer.ToArray(), out int sampleRate, out int channels);
            if (samples is null)
            {
                AppLog.Warn("no pude interpretar el WAV sintetizado");
                return null;
            }

            AppLog.Info($"voz de prueba sintetizada: {samples.Length / Math.Max(1, channels)} frames @ {sampleRate} Hz");
            return (samples, sampleRate, channels);
        }
        catch (Exception ex)
        {
            // Robust on purpose: a missing SAPI runtime, a locked voice or a codec
            // problem must only downgrade the test to the 440 Hz tone.
            AppLog.Warn($"síntesis de voz no disponible ({ex.GetType().Name}): {ex.Message}");
            return null;
        }
    }

    private static void SelectVoice(SpeechSynthesizer synthesizer, string languageTag)
    {
        System.Speech.Synthesis.InstalledVoice? match = synthesizer
            .GetInstalledVoices()
            .FirstOrDefault(voice => voice.Enabled &&
                                     voice.VoiceInfo.Culture.TwoLetterISOLanguageName
                                         .Equals(languageTag, StringComparison.OrdinalIgnoreCase));

        match ??= synthesizer
            .GetInstalledVoices()
            .FirstOrDefault(voice => voice.Enabled &&
                                     voice.VoiceInfo.Culture.TwoLetterISOLanguageName
                                         .Equals("en", StringComparison.OrdinalIgnoreCase));

        match ??= synthesizer.GetInstalledVoices().FirstOrDefault(voice => voice.Enabled);

        if (match is not null)
        {
            synthesizer.SelectVoice(match.VoiceInfo.Name);
        }
        else
        {
            AppLog.Warn("no hay ninguna voz instalada; se usará la predeterminada del sistema");
        }
    }

    /// <summary>Minimal RIFF/WAVE reader: walks the chunks and returns the data chunk.</summary>
    private static short[]? TryReadWavePcm(byte[] wave, out int sampleRate, out int channels)
    {
        sampleRate = 44_100;
        channels = 2;

        if (wave.Length < 44 ||
            wave[0] != 'R' || wave[1] != 'I' || wave[2] != 'F' || wave[3] != 'F' ||
            wave[8] != 'W' || wave[9] != 'A' || wave[10] != 'V' || wave[11] != 'E')
        {
            return null;
        }

        int offset = 12;
        while (offset + 8 <= wave.Length)
        {
            string chunkId = System.Text.Encoding.ASCII.GetString(wave, offset, 4);
            int chunkSize = BitConverter.ToInt32(wave, offset + 4);
            int dataOffset = offset + 8;

            if (chunkSize < 0)
            {
                break;
            }

            // Streaming writers leave the size at 0; then the chunk runs to the end.
            if (chunkSize == 0 || dataOffset + chunkSize > wave.Length)
            {
                chunkSize = wave.Length - dataOffset;
            }

            if (chunkId == "fmt " && chunkSize >= 16)
            {
                channels = BitConverter.ToInt16(wave, dataOffset + 2);
                sampleRate = BitConverter.ToInt32(wave, dataOffset + 4);
                int bitsPerSample = BitConverter.ToInt16(wave, dataOffset + 14);
                if (bitsPerSample != 16)
                {
                    AppLog.Warn($"el WAV sintetizado no es de 16 bits ({bitsPerSample})");
                    return null;
                }
            }
            else if (chunkId == "data")
            {
                var samples = new short[chunkSize / 2];
                Buffer.BlockCopy(wave, dataOffset, samples, 0, samples.Length * 2);
                return samples;
            }

            offset = dataOffset + chunkSize + (chunkSize % 2);
        }

        return null;
    }
}
