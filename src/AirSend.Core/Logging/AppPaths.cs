namespace AirSend.Core.Logging;

/// <summary>
/// Where AirSend keeps its settings and logs.
/// </summary>
/// <remarks>
/// Default is <c>%APPDATA%\AirSend</c> (same place the Rust build uses through
/// <c>tauri-plugin-store</c>). Setting the <c>AIRSEND_DATA_DIR</c> environment
/// variable redirects everything, which is handy for portable installs, support
/// diagnostics, and automated tests.
/// </remarks>
public static class AppPaths
{
    public const string DataDirectoryVariable = "AIRSEND_DATA_DIR";

    public static string DataDirectory(string applicationName = "AirSend")
    {
        string? custom = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        return !string.IsNullOrWhiteSpace(custom)
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), applicationName);
    }

    public static string LogDirectory(string applicationName = "AirSend") =>
        Path.Combine(DataDirectory(applicationName), "logs");
}
