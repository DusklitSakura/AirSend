using System.Diagnostics;
using AirSend.Core.Logging;

namespace AirSend.Core.Updates;

/// <summary>What to copy where, and which process has to be gone first.</summary>
public sealed record UpdateApplyRequest(
    string SourceDirectory,
    string TargetDirectory,
    string Version,
    int? WaitForProcessId = null,
    TimeSpan? WaitTimeout = null,
    TimeSpan? RetryDelay = null);

/// <summary>Outcome of applying an update.</summary>
public sealed record UpdateApplyResult(bool Succeeded, int FilesCopied, string? Error);

/// <summary>
/// Replaces the files of an installation with an unpacked package.
/// </summary>
/// <remarks>
/// This runs inside AirSend itself (the freshly staged copy, launched with
/// <c>--apply-update</c>), not in a generated batch file. The batch approach looked
/// simpler but was fragile exactly where it mattered: <c>AppContext.BaseDirectory</c>
/// ends with a backslash, so the quoted path <c>"D:\Software\AirSend\"</c> swallowed
/// its closing quote, robocopy reported a malformed path and the helper exited
/// without touching anything — a silent failure the user only noticed as a window
/// that flashed. Doing it in-process means no shell quoting, no line-ending rules,
/// proper error messages and a report file the next start can show.
/// </remarks>
public static class UpdateApplier
{
    /// <summary>Report the updater leaves next to the executable.</summary>
    public const string ReportFileName = "update-report.txt";

    private const int CopyAttempts = 4;

    public static UpdateApplyResult Apply(UpdateApplyRequest request, Action<string>? log = null)
    {
        log ??= _ => { };

        string source = Path.GetFullPath(request.SourceDirectory);
        string target = Path.GetFullPath(request.TargetDirectory);

        if (!Directory.Exists(source))
        {
            return Fail(target, request.Version, 0, $"no encuentro el paquete en {source}", log);
        }

        if (request.WaitForProcessId is { } processId)
        {
            var wait = request.WaitTimeout ?? TimeSpan.FromSeconds(60);
            log($"esperando a que termine el proceso {processId} (máximo {wait.TotalSeconds:0} s)");
            WaitForExit(processId, wait);
        }

        string[] files = [.. Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)];
        var failures = new List<string>();
        TimeSpan retryDelay = request.RetryDelay ?? TimeSpan.FromSeconds(1);
        int copied = 0;

        for (int attempt = 1; attempt <= CopyAttempts; attempt++)
        {
            failures.Clear();
            copied = 0;

            foreach (string file in files)
            {
                string relative = Path.GetRelativePath(source, file);
                string destination = Path.Combine(target, relative);

                try
                {
                    string? directory = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    File.Copy(file, destination, overwrite: true);
                    copied++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A file can still be held for a moment after the app exits, so a
                    // failed pass is retried instead of given up on.
                    failures.Add($"{relative}: {ex.Message}");
                }
            }

            if (failures.Count == 0)
            {
                log($"copiados {copied} archivos a {target}");
                WriteReport(target, succeeded: true, request.Version, copied, error: null);
                return new UpdateApplyResult(true, copied, null);
            }

            log($"intento {attempt}: {failures.Count} de {files.Length} archivos no se pudieron copiar");

            if (attempt < CopyAttempts)
            {
                Thread.Sleep(retryDelay * attempt);
            }
        }

        return Fail(target, request.Version, copied, failures[0], log);
    }

    private static UpdateApplyResult Fail(
        string target,
        string version,
        int copied,
        string error,
        Action<string> log)
    {
        log($"falló la actualización: {error}");
        WriteReport(target, succeeded: false, version, copied, error);
        return new UpdateApplyResult(false, copied, error);
    }

    private static void WaitForExit(int processId, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < timeout && IsRunning(processId))
        {
            Thread.Sleep(200);
        }
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            // No such process any more: exactly what we are waiting for.
            return false;
        }
    }

    private static void WriteReport(string targetDirectory, bool succeeded, string version, int files, string? error)
    {
        try
        {
            var lines = new List<string>
            {
                $"result={(succeeded ? "ok" : "failed")}",
                $"version={version}",
                $"files={files}",
            };

            if (!string.IsNullOrEmpty(error))
            {
                lines.Add($"error={error}");
            }

            File.WriteAllLines(Path.Combine(targetDirectory, ReportFileName), lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("log.update.report_failed", new { err = AirSendError.Describe(ex) });
        }
    }
}
