namespace AirSend.Core.Streaming;

/// <summary>
/// The latency slider only sets the <em>upper bound</em> AirPlay is allowed to
/// buffer; receivers may add their own delay on top.
/// Port of the constants and helpers in <c>crates/airplay-core/src/streaming.rs</c>.
/// </summary>
public static class LatencyProfile
{
    public const uint DefaultLatencyMs = 3000;

    /// <summary>
    /// Receivers stay silent below this: with no buffer at all a HomePod accepts the
    /// session and never renders, so 100 ms is the floor the UI offers.
    /// </summary>
    public const uint MinLatencyMs = 100;

    public const uint MaxLatencyMs = 3000;
    public const uint LatencyStepMs = 100;

    /// <summary>0.20 ≈ -25 dB: gentle default so the first samples are not loud.</summary>
    public const float DefaultInitialVolume = 0.20f;

    public const int SampleRate = 44_100;
    public const int Channels = 2;
    public const int FramesPerPacket = 352;

    public static bool IsValid(uint latencyMs) =>
        latencyMs is >= MinLatencyMs and <= MaxLatencyMs && latencyMs % LatencyStepMs == 0;

    public static void Validate(uint latencyMs)
    {
        if (!IsValid(latencyMs))
        {
            throw new ArgumentOutOfRangeException(
                nameof(latencyMs),
                latencyMs,
                "latency must be 0–3000 ms in 100 ms steps");
        }
    }

    /// <summary>
    /// Converts milliseconds into the AirPlay <c>latencyMinFrames</c>/<c>latencyMaxFrames</c>
    /// pair at the current sample rate.
    /// </summary>
    public static (uint MinFrames, uint MaxFrames) ToFrames(uint latencyMs)
    {
        Validate(latencyMs);
        if (latencyMs == 0)
        {
            return (0, 0);
        }

        uint minMs = Math.Clamp(latencyMs / 4, 100, 500);
        return (minMs * SampleRate / 1000, latencyMs * SampleRate / 1000);
    }

    /// <summary>
    /// Decodes legacy settings ("music"/"video"/"gaming") that predate the slider,
    /// so upgrades keep the user's choice.
    /// </summary>
    public static uint? DecodeLegacySetting(string? value) => value switch
    {
        "music" => 3000,
        "video" => 2000,
        "gaming" => 1000,
        _ => null,
    };

    public static uint? DecodeLegacySetting(long? numeric) =>
        numeric is not null && IsValid((uint)numeric.Value) ? (uint)numeric.Value : null;

    /// <summary>Cooldown between two accepted latency changes (10 s, as in the UI).</summary>
    public static readonly TimeSpan ChangeCooldown = TimeSpan.FromSeconds(10);

    public static TimeSpan CooldownRemaining(DateTimeOffset? lastChange, DateTimeOffset now)
    {
        if (lastChange is null)
        {
            return TimeSpan.Zero;
        }

        TimeSpan elapsed = now - lastChange.Value;
        return elapsed >= ChangeCooldown ? TimeSpan.Zero : ChangeCooldown - elapsed;
    }
}
