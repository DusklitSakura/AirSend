using AirSend.Core;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using AirSend.Core.Logging;
using AirSend.Core.Updates;

namespace AirSend.Services;

public enum UpdatePhase
{
    Downloading,
    Extracting,
    Staging,
}

public sealed record UpdateProgress(UpdatePhase Phase, double Fraction, string? PackageName = null);

/// <summary>What the updater reported after an update attempt.</summary>
public sealed record UpdateOutcome(bool Succeeded, string? Version, int FilesCopied, string? Error);

/// <summary>
/// Checks GitHub for a newer release and, when the user agrees, downloads it,
/// unpacks it next to the running copy and hands over to a small helper script
/// that replaces the files once this process has exited.
/// </summary>
/// <remarks>
/// The app is distributed as a zip that the user unzips wherever they like, so
/// there is no installer to call and no package identity to update. Replacing the
/// files of a running process is impossible on Windows (the executable and the
/// loaded DLLs are locked), hence the detach-and-swap dance: the helper waits for
/// our PID to disappear, robocopies the new files over the old ones and restarts
/// the app.
/// </remarks>
public sealed class UpdateService : IDisposable
{
    private const string StagingPrefix = "AirSend-update-";

    /// <summary>Report the helper script leaves next to the executable.</summary>
    private const string ReportFileName = UpdateApplier.ReportFileName;

    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan StagingLifetime = TimeSpan.FromDays(1);

    private readonly SettingsStore _settings;
    private readonly UpdateClient _client = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _checkGate = new(1, 1);

    private Task? _loop;
    private bool _checkedThisSession;

    public UpdateService(SettingsStore settings)
    {
        _settings = settings;
        CleanUpStagingDirectories();
    }

    /// <summary>Raised on a background thread after every check, manual or not.</summary>
    public event Action<UpdateCheckResult>? CheckCompleted;

    public UpdateVersion CurrentVersion { get; } = CurrentAssemblyVersion();

    /// <summary>
    /// What this installation is (architecture, flavour, version). The updater uses
    /// it to pick the one package that may replace it.
    /// </summary>
    public InstalledBuild Build { get; } = InstalledBuild.Detect(AppContext.BaseDirectory);

    public UpdateCheckInterval Policy
    {
        get => UpdatePolicy.Parse(_settings.UpdateCheckPolicy);
        set => _settings.UpdateCheckPolicy = UpdatePolicy.ToSetting(value);
    }

    /// <summary>Starts the periodic check. The first look happens shortly after launch.</summary>
    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken token)
    {
        try
        {
            // Let the window come up and the startup prompts settle first.
            await Task.Delay(StartupDelay, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(PollInterval);

        while (!token.IsCancellationRequested)
        {
            if (UpdatePolicy.IsDue(Policy, _settings.UpdateLastCheck, DateTimeOffset.UtcNow, _checkedThisSession))
            {
                _checkedThisSession = true;
                await CheckAsync(manual: false, token).ConfigureAwait(false);
            }

            try
            {
                await timer.WaitForNextTickAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Runs one check and reports it through <see cref="CheckCompleted"/>. Manual
    /// checks ignore the configured interval; automatic ones are only started by
    /// <see cref="LoopAsync"/> when the interval says they are due.
    /// </summary>
    public async Task CheckAsync(bool manual, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);

        await _checkGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            UpdateRelease? release = await _client
                .GetNewerReleaseAsync(CurrentVersion, linked.Token)
                .ConfigureAwait(false);

            // Only a completed round trip counts: a failed check retries on the next tick.
            _settings.UpdateLastCheck = DateTimeOffset.UtcNow;

            if (release is null)
            {
                AppLog.Info("log.update.up_to_date", new { current = CurrentVersion });
            }
            else
            {
                AppLog.Info("log.update.available", new { version = release.Version, current = CurrentVersion });
            }

            CheckCompleted?.Invoke(new UpdateCheckResult(release, null, manual));
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // Shutting down, or the caller cancelled: not an error worth reporting.
        }
        catch (Exception ex)
        {
            AppLog.Warn("log.update.check_failed", new { err = AirSendError.Describe(ex) });
            CheckCompleted?.Invoke(new UpdateCheckResult(null, AirSendError.Describe(ex), manual));
        }
        finally
        {
            _checkGate.Release();
        }
    }

    /// <summary>
    /// Downloads the package that matches this build, unpacks it and writes the
    /// helper script. Returns the script path; the caller runs it via
    /// <see cref="ApplyUpdate"/> after the user has been told the app will restart.
    /// </summary>
    public async Task<string> DownloadAndStageAsync(
        UpdateRelease release,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        UpdateAsset asset = UpdateAssets.Select(release.Assets, Build, release.Version)
            ?? throw new UpdateException(
                UpdateFailure.NoPackageForThisBuild,
                "error.update.no_package",
                new { version = release.Version, build = Build.Describe() });

        AppLog.Info("log.update.package_chosen", new { package = asset.Name, build = Build.Describe() });

        string stagingDirectory = Path.Combine(
            Path.GetTempPath(),
            $"{StagingPrefix}{release.Version}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);

        string zipPath = Path.Combine(stagingDirectory, asset.Name);
        progress?.Report(new UpdateProgress(UpdatePhase.Downloading, 0, asset.Name));

        IProgress<double>? downloadProgress = progress is null
            ? null
            : new Progress<double>(fraction =>
                progress.Report(new UpdateProgress(UpdatePhase.Downloading, fraction, asset.Name)));

        await _client.DownloadAsync(asset, zipPath, downloadProgress, cancellationToken).ConfigureAwait(false);

        progress?.Report(new UpdateProgress(UpdatePhase.Extracting, 0, asset.Name));
        string extracted = Path.Combine(stagingDirectory, "files");
        await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, extracted, overwriteFiles: true), cancellationToken)
            .ConfigureAwait(false);

        // Nothing is allowed to replace the installation before it has been checked
        // to be the same architecture, the same flavour and the same version.
        UpdateAssets.EnsurePackageMatches(extracted, Build, release.Version);

        progress?.Report(new UpdateProgress(UpdatePhase.Staging, 1, asset.Name));

        AppLog.Info("log.update.staged", new { version = release.Version, path = stagingDirectory });
        return Path.Combine(extracted, "AirSend.exe");
    }

    /// <summary>
    /// Hands over to the freshly staged copy of AirSend, which waits for this process
    /// to exit, copies the files over the installation, reports the result and starts
    /// the app again. Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>
    /// so paths are escaped by the runtime rather than by string concatenation.
    /// </summary>
    public void ApplyUpdate(string stagedExecutable)
    {
        string stagingDirectory = Path.GetDirectoryName(stagedExecutable) ?? Path.GetTempPath();

        var startInfo = new ProcessStartInfo(stagedExecutable)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = stagingDirectory,
        };

        startInfo.ArgumentList.Add("--apply-update");
        startInfo.ArgumentList.Add("--source");
        startInfo.ArgumentList.Add(stagingDirectory);
        startInfo.ArgumentList.Add("--target");
        startInfo.ArgumentList.Add(AppContext.BaseDirectory);
        startInfo.ArgumentList.Add("--wait-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

        Process.Start(startInfo);
        AppLog.Info("log.update.applying");
    }

    /// <summary>
    /// Reads (and removes) the report the helper script leaves behind, so the next
    /// start can say whether the files were actually replaced. The script runs
    /// detached while the app is gone, which is exactly when a failure would
    /// otherwise be invisible.
    /// </summary>
    public UpdateOutcome? ConsumeOutcomeReport()
    {
        string path = Path.Combine(AppContext.BaseDirectory, ReportFileName);

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            Dictionary<string, string> values = File
                .ReadAllLines(path)
                .Select(line => line.Split('=', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);

            File.Delete(path);

            bool succeeded = values.TryGetValue("result", out string? result)
                             && result.Equals("ok", StringComparison.OrdinalIgnoreCase);
            int filesCopied = values.TryGetValue("files", out string? files) && int.TryParse(files, out int parsed)
                ? parsed
                : 0;

            return new UpdateOutcome(
                succeeded,
                values.TryGetValue("version", out string? version) ? version : null,
                filesCopied,
                values.TryGetValue("error", out string? error) ? error : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException
                                       or ArgumentException or InvalidOperationException)
        {
            AppLog.Warn("log.update.report_failed", new { err = AirSendError.Describe(ex) });
            return null;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _client.Dispose();
        _checkGate.Dispose();
        _cts.Dispose();
    }

    private static UpdateVersion CurrentAssemblyVersion()
    {
        Version version = typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);
        return new UpdateVersion(version.Major, version.Minor, Math.Max(version.Build, 0));
    }

    /// <summary>
    /// Removes leftovers from earlier updates. Only old directories are touched:
    /// the one belonging to an update that is still being applied has just been
    /// created, and the helper script lives inside it.
    /// </summary>
    private static void CleanUpStagingDirectories()
    {
        try
        {
            DateTimeOffset cutoff = DateTimeOffset.UtcNow - StagingLifetime;

            foreach (string directory in Directory.EnumerateDirectories(Path.GetTempPath(), StagingPrefix + "*"))
            {
                if (Directory.GetCreationTimeUtc(directory) > cutoff)
                {
                    continue;
                }

                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Still in use; it will be cleaned up after the next update.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temp directory we cannot enumerate is not worth failing startup over.
        }
    }
}
