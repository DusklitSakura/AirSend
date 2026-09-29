using Microsoft.Win32;

namespace AirSend.Services;

/// <summary>
/// "Start with Windows" for an unpackaged app: a value in the per-user Run key.
/// </summary>
/// <remarks>
/// The registered command passes <c>--minimized</c>, so an autostart launch goes
/// straight to the tray: combined with "connect automatically" the app can be
/// streaming before the user looks at the screen.
/// </remarks>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AirSend";

    /// <summary>Command line written to the Run key.</summary>
    public static string Command => $"\"{Environment.ProcessPath}\" --minimized";

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>True when the stored command points at this very executable.</summary>
    public static bool IsCurrentExecutableRegistered()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value &&
                   value.Contains(Environment.ProcessPath ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("no se pudo abrir la clave Run del registro");

        if (enabled)
        {
            key.SetValue(ValueName, Command, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
