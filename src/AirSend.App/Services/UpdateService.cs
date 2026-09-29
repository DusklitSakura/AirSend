using System.Diagnostics;
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

            AppLog.Info(release is null
                ? $"actualizaciones: {CurrentVersion} sigue siendo la última versión"
                : $"actualizaciones: disponible {release.Version} (instalada {CurrentVersion})");

            CheckCompleted?.Invoke(new UpdateCheckResult(release, null, manual));
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // Shutting down, or the caller cancelled: not an error worth reporting.
        }
        catch (Exception ex)
        {
            AppLog.Warn($"no pude comprobar si hay actualizaciones: {ex.Message}");
            CheckCompleted?.Invoke(new UpdateCheckResult(null, ex.Message, manual));
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
                $"la versión {release.Version} no trae un paquete para esta instalación ({Build.Describe()})");

        AppLog.Info($"actualización: paquete elegido {asset.Name} para {Build.Describe()}");

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

        string scriptPath = Path.Combine(stagingDirectory, "apply-update.cmd");
        await File
            .WriteAllTextAsync(
                scriptPath,
                BuildApplyScript(extracted, AppContext.BaseDirectory, Environment.ProcessId),
                cancellationToken)
            .ConfigureAwait(false);

        AppLog.Info($"actualización {release.Version} descargada y preparada en {stagingDirectory}");
        return scriptPath;
    }

    /// <summary>Launches the helper that swaps the files once this process exits.</summary>
    public void ApplyUpdate(string scriptPath)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c \"\"{scriptPath}\"\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? Path.GetTempPath(),
        };

        Process.Start(startInfo);
        AppLog.Info("actualización en curso: AirSend se cierra para reemplazar los archivos");
    }

    public void Dispose()
    {
        _cts.Cancel();
        _client.Dispose();
        _checkGate.Dispose();
        _cts.Dispose();
    }

    private static string BuildApplyScript(string sourceDirectory, string targetDirectory, int processId) => $"""
        @echo off
        rem Written by AirSend: waits until the running copy has exited, copies the
        rem new files over the current installation and starts the app again.
        setlocal
        set "AIRSEND_PID={processId}"
        set "AIRSEND_SRC={sourceDirectory}"
        set "AIRSEND_DST={targetDirectory}"

        :wait
        tasklist /FI "PID eq %AIRSEND_PID%" 2>NUL | findstr /C:"%AIRSEND_PID%" >NUL
        if not errorlevel 1 (
            ping -n 2 127.0.0.1 >NUL
            goto wait
        )

        robocopy "%AIRSEND_SRC%" "%AIRSEND_DST%" /E /R:2 /W:1 /NFL /NDL /NJH /NJS /NP >NUL
        if errorlevel 8 exit /b 1

        cd /d "%AIRSEND_DST%"
        start "" "%AIRSEND_DST%\AirSend.exe"
        exit /b 0
        """;

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
