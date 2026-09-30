namespace AirSend.Core.Localization;

/// <summary>
/// Text Core invents itself (not an error, not a log line) — for example the name a
/// manually added receiver gets when the user does not type one.
/// </summary>
internal static class NameMessages
{
    private static readonly Dictionary<string, (string Es, string En, string Zh)> Table =
        new(StringComparer.Ordinal)
        {
            ["name.manual_device"] = (
                "Dispositivo manual {address}",
                "Manual device {address}",
                "手动添加的设备 {address}"),
        };

    public static string Format(string? language, string key, object? parameters) =>
        MessageTable.Format(language, Table, key, parameters);

    internal static IReadOnlyCollection<string> Keys => Table.Keys;
}
