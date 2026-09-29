namespace AirSend.Core.Discovery;

/// <summary>
/// Class of AirPlay receiver, derived from the mDNS <c>model</c> TXT entry.
/// Mirrors <c>DeviceKind</c> in <c>crates/airplay-core/src/discovery.rs</c>.
/// </summary>
public enum DeviceKind
{
    HomePod,
    AppleTv,
    AirportExpress,
    OtherAirPlay,
}

public static class DeviceKindExtensions
{
    public static DeviceKind FromModel(string? model)
    {
        if (string.IsNullOrEmpty(model))
        {
            return DeviceKind.OtherAirPlay;
        }

        if (model.StartsWith("AudioAccessory", StringComparison.OrdinalIgnoreCase))
        {
            return DeviceKind.HomePod;
        }

        if (model.StartsWith("AppleTV", StringComparison.OrdinalIgnoreCase))
        {
            return DeviceKind.AppleTv;
        }

        if (model.StartsWith("AirPort", StringComparison.OrdinalIgnoreCase))
        {
            return DeviceKind.AirportExpress;
        }

        return DeviceKind.OtherAirPlay;
    }

    /// <summary>Lower-case discriminator used by the UI (same spelling as the TypeScript port).</summary>
    public static string ToWireName(this DeviceKind kind) => kind switch
    {
        DeviceKind.HomePod => "homepod",
        DeviceKind.AppleTv => "appletv",
        DeviceKind.AirportExpress => "airportexpress",
        _ => "otherairplay",
    };

    /// <summary>Human readable label, matching <c>KIND_LABEL</c> in the Tauri frontend.</summary>
    public static string ToDisplayLabel(this DeviceKind kind) => kind switch
    {
        DeviceKind.HomePod => "HomePod",
        DeviceKind.AppleTv => "Apple TV",
        DeviceKind.AirportExpress => "AirPort Express",
        _ => "AirPlay",
    };
}
