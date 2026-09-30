using System.Diagnostics;
using AirSend.Core.Localization;
using AirSend.Core.Logging;
using AirSend.Core.Updates;
using AirSend.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace AirSend;

/// <summary>
/// Entry point. Besides starting the window it carries one headless mode: the updater.
/// </summary>
/// <remarks>
/// The updater used to be a generated batch file, which turned out to be fragile in
/// exactly the spot that matters (the destination directory ends with a backslash, so
/// the quoted path swallowed its closing quote and the copy never happened). Having
/// the freshly staged copy of AirSend do the work itself removes the shell from the
/// equation: no quoting, no batch parsing, real error messages, and it runs on the
/// runtime flavour the package was built for.
/// </remarks>
internal static class Program
{
    private const string ApplyUpdateSwitch = "--apply-update";

    [STAThread]
    private static void Main(string[] args)
    {
        if (TryRunUpdater(args))
        {
            return;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }

    private static bool TryRunUpdater(string[] args)
    {
        if (!args.Any(argument => argument.Equals(ApplyUpdateSwitch, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        string? source = Value(args, "--source");
        string? target = Value(args, "--target");
        // This process *is* the new version, so it knows it better than the caller does.
        string version = typeof(Program).Assembly.GetName().Version?.ToString(3)
                         ?? Value(args, "--version")
                         ?? "?";
        int? waitForProcessId = int.TryParse(Value(args, "--wait-pid"), out int pid) ? pid : null;

        // Keep the updater's own lines in the same log, in the user's language.
        AppLog.Initialize();
        try
        {
            AppText.Language = new SettingsStore().Language ?? AppText.Language;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the settings file the log simply stays in the default language.
        }

        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
        {
            AppLog.Error("log.update.apply_bad_arguments", new { source = source ?? "?", target = target ?? "?" });
            return true;
        }

        AppLog.Info("log.update.apply_started", new { version, source, target });

        UpdateApplyResult result = UpdateApplier.Apply(
            new UpdateApplyRequest(source, target, version, waitForProcessId),
            log: detail => AppLog.Info("log.update.apply_detail", new { detail }));

        if (!result.Succeeded)
        {
            AppLog.Error("log.update.apply_failed", new { err = result.Error ?? "?" });
            return true;
        }

        AppLog.Info("log.update.applied", new { version });

        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(target, "AirSend.exe"))
            {
                WorkingDirectory = target,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            AppLog.Error("log.update.relaunch_failed", ex);
        }

        return true;
    }

    private static string? Value(string[] args, string name)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }
}
