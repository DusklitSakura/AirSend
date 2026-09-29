namespace AirSend.Core.Updates;

/// <summary>How often the app looks for a new release.</summary>
public enum UpdateCheckInterval
{
    Never,
    Startup,
    Daily,
    Weekly,
}

public static class UpdatePolicy
{
    public static string ToSetting(UpdateCheckInterval interval) => interval switch
    {
        UpdateCheckInterval.Never => "never",
        UpdateCheckInterval.Daily => "daily",
        UpdateCheckInterval.Weekly => "weekly",
        _ => "startup",
    };

    public static UpdateCheckInterval Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "never" => UpdateCheckInterval.Never,
        "daily" => UpdateCheckInterval.Daily,
        "weekly" => UpdateCheckInterval.Weekly,
        _ => UpdateCheckInterval.Startup,
    };

    public static TimeSpan? PeriodFor(UpdateCheckInterval interval) => interval switch
    {
        UpdateCheckInterval.Daily => TimeSpan.FromDays(1),
        UpdateCheckInterval.Weekly => TimeSpan.FromDays(7),
        _ => null,
    };

    /// <summary>
    /// True when a check is due. <c>Startup</c> means once per session; the timed
    /// policies compare against the last successful check, so a machine that is
    /// only rebooted every few days still checks at most once per period.
    /// </summary>
    public static bool IsDue(
        UpdateCheckInterval interval,
        DateTimeOffset? lastCheckUtc,
        DateTimeOffset nowUtc,
        bool alreadyCheckedThisSession)
    {
        switch (interval)
        {
            case UpdateCheckInterval.Never:
                return false;
            case UpdateCheckInterval.Startup:
                return !alreadyCheckedThisSession;
            default:
                if (PeriodFor(interval) is not { } period)
                {
                    return false;
                }

                return lastCheckUtc is null || nowUtc - lastCheckUtc.Value >= period;
        }
    }
}
