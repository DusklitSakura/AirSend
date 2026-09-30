using AirSend.Core.Logging;

namespace AirSend.Core.Localization;

/// <summary>
/// The language the app is showing, plus the catalog lookup for everything Core has
/// to say: log lines and the messages of <c>AirSendException</c>.
/// </summary>
/// <remarks>
/// The app sets <see cref="Language"/> from the interface language once at startup
/// (and again when the user switches language). Both the log and the exception
/// messages are rendered when they are used, so switching language changes what new
/// log lines and new error dialogs look like.
/// </remarks>
public static class AppText
{
    /// <summary>Language tag: "zh", "en" or "es". Defaults to English.</summary>
    public static string Language { get; set; } = "en";

    /// <summary>Resolves a catalog key in the current language.</summary>
    public static string Get(string key, object? parameters = null) => key switch
    {
        _ when key.StartsWith("log.", StringComparison.Ordinal) => LogMessages.Format(Language, key, parameters),
        _ when key.StartsWith("name.", StringComparison.Ordinal) => NameMessages.Format(Language, key, parameters),
        _ => ErrorMessages.Format(Language, key, parameters),
    };

    /// <summary>Every key the catalog knows: log lines, error messages and names.</summary>
    public static IReadOnlyCollection<string> Keys { get; } =
        [.. LogMessages.Keys, .. ErrorMessages.Keys, .. NameMessages.Keys];
}
