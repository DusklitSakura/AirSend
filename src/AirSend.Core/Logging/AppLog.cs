using System.Text;
using AirSend.Core.Localization;

namespace AirSend.Core.Logging;

public enum AppLogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

public sealed record AppLogEntry(DateTimeOffset Timestamp, AppLogLevel Level, string Message)
{
    public override string ToString() =>
        $"{Timestamp:HH:mm:ss.fff}  {Level.ToString().ToUpperInvariant(),-5} {Message}";
}

/// <summary>
/// Daily rotated file log plus an in-memory tail for the UI.
/// Counterpart of <c>init_tracing()</c> in <c>src-tauri/src/lib.rs</c>, which writes
/// <c>%APPDATA%/ConexionAirPlay/logs/app.log.YYYY-MM-DD</c> through
/// <c>tracing_appender::rolling::daily</c>.
/// </summary>
public static class AppLog
{
    private const int MaxBufferedEntries = 2000;

    private static readonly object Sync = new();
    private static readonly Queue<AppLogEntry> Buffer = new();
    private static string? _logDirectory;
    private static AppLogLevel _minimumLevel = AppLogLevel.Info;
    private static bool _initialized;

    /// <summary>Raised for every accepted entry; the UI subscribes to mirror the log panel.</summary>
    public static event Action<AppLogEntry>? EntryWritten;

    public static string? LogDirectory => _logDirectory;

    /// <summary>
    /// Language of the messages ("zh", "en", "es"). The app sets it from the interface
    /// language, so the log reads like the rest of the UI; the Spanish column of the
    /// catalog stays the reference text ported from the upstream Rust build.
    /// </summary>
    public static string Language
    {
        get => AppText.Language;
        set => AppText.Language = value;
    }

    public static void Initialize(string applicationName = "AirSend")
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        // Preference order matches the Rust app (%APPDATA%\AirSend\logs), but the
        // port also works where that directory is not writable (locked down
        // machines, sandboxes): it falls back next to the executable, then to temp.
        foreach (string candidate in EnumerateLogDirectories(applicationName))
        {
            try
            {
                Directory.CreateDirectory(candidate);
                _logDirectory = candidate;
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"→ no pude crear dir de logs {candidate}: {ex.Message}");
            }
        }

        Console.Error.WriteLine("→ los logs quedan solo en memoria (no hay directorio escribible)");
    }

    private static IEnumerable<string> EnumerateLogDirectories(string applicationName)
    {
        yield return AppPaths.LogDirectory(applicationName);
        yield return Path.Combine(AppContext.BaseDirectory, "logs");
        yield return Path.Combine(Path.GetTempPath(), applicationName, "logs");
    }

    public static void SetMinimumLevel(AppLogLevel level) => _minimumLevel = level;

    public static void Debug(string key, object? parameters = null) =>
        Write(AppLogLevel.Debug, LogMessages.Format(Language, key, parameters));

    public static void Info(string key, object? parameters = null) =>
        Write(AppLogLevel.Info, LogMessages.Format(Language, key, parameters));

    public static void Warn(string key, object? parameters = null) =>
        Write(AppLogLevel.Warn, LogMessages.Format(Language, key, parameters));

    public static void Error(string key, object? parameters = null) =>
        Write(AppLogLevel.Error, LogMessages.Format(Language, key, parameters));

    public static void Error(string key, Exception exception, object? parameters = null) =>
        Write(
            AppLogLevel.Error,
            $"{LogMessages.Format(Language, key, parameters)}: " +
            $"{exception.GetType().Name}: {AirSendError.Describe(exception)}");

    /// <summary>
    /// Resolves a catalog entry without logging it, for the few places that need a
    /// translated fragment inside a message (for example "no default device").
    /// </summary>
    public static string Text(string key, object? parameters = null) =>
        AppText.Get(key, parameters);

    public static IReadOnlyList<AppLogEntry> Snapshot()
    {
        lock (Sync)
        {
            return Buffer.ToArray();
        }
    }

    private static void Write(AppLogLevel level, string message)
    {
        if (level < _minimumLevel)
        {
            return;
        }

        var entry = new AppLogEntry(DateTimeOffset.Now, level, message);

        lock (Sync)
        {
            Buffer.Enqueue(entry);
            while (Buffer.Count > MaxBufferedEntries)
            {
                Buffer.Dequeue();
            }

            AppendToFile(entry);
        }

        EntryWritten?.Invoke(entry);
    }

    private static void AppendToFile(AppLogEntry entry)
    {
        if (_logDirectory is null)
        {
            return;
        }

        try
        {
            string file = Path.Combine(_logDirectory, $"app.{entry.Timestamp:yyyy-MM-dd}.log");
            var line = new StringBuilder()
                .Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                .Append(' ')
                .Append(entry.Level.ToString().ToUpperInvariant())
                .Append(' ')
                .AppendLine(entry.Message)
                .ToString();
            File.AppendAllText(file, line, Encoding.UTF8);
        }
        catch (IOException)
        {
            // Logging must never take the app down.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}
