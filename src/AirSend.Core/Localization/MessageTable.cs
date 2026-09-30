using System.Globalization;
using System.Reflection;

namespace AirSend.Core.Localization;

/// <summary>
/// Looks a message up in a three-language table and substitutes the placeholders.
/// Every entry holds all three languages together, so a message cannot be added in
/// one language only.
/// </summary>
internal static class MessageTable
{
    public static string Format(
        string? language,
        IReadOnlyDictionary<string, (string Es, string En, string Zh)> table,
        string key,
        object? parameters)
    {
        if (!table.TryGetValue(key, out (string Es, string En, string Zh) entry))
        {
            // Returning the key keeps a missing entry visible instead of hiding the
            // message; the test suite fails before that can ship.
            return key;
        }

        string template = Normalize(language) switch
        {
            "zh" => entry.Zh,
            "es" => entry.Es,
            _ => entry.En,
        };

        if (parameters is null)
        {
            return template;
        }

        foreach (PropertyInfo property in parameters.GetType().GetProperties())
        {
            template = template.Replace(
                $"{{{property.Name}}}",
                Convert.ToString(property.GetValue(parameters), CultureInfo.InvariantCulture) ?? string.Empty,
                StringComparison.Ordinal);
        }

        return template;
    }

    private static string Normalize(string? language) => (language ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "zh" or "zh-cn" or "zh-hans" or "zh-sg" => "zh",
        "es" or "es-es" or "es-mx" => "es",
        _ => "en",
    };
}
