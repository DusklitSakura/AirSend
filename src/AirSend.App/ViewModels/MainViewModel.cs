using System.Collections.ObjectModel;
using System.Net;
using AirSend.Core.Discovery;
using AirSend.Core.Capture;
using AirSend.Core.Logging;
using AirSend.Core.Probe;
using AirSend.Core.Streaming;
using AirSend.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;

namespace AirSend.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private const int ReconnectTimeoutMs = 6000;

    /// <summary>Volume (in percent) above which the hearing-damage warning appears.</summary>
    public const int LoudVolumeThreshold = 35;

    /// <summary>Volume (in percent) at or below which the "barely audible" hint appears.</summary>
    public const int QuietVolumeThreshold = 5;

    private static readonly TimeSpan QuietHintDuration = TimeSpan.FromSeconds(5);

    private readonly AirPlayCoordinator _coordinator;
    private readonly Localization _localization;
    private readonly DispatcherQueue _dispatcher;
    private readonly Dictionary<string, DeviceViewModel> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _activeRouteKeys = new(StringComparer.OrdinalIgnoreCase);

    private string? _connectedGroupKey;
    private string? _connectingGroupKey;
    private DeviceViewModel? _playerDevice;
    private AirPlayDevice? _connectedRoute;
    private bool _applyingLatency;
    private int _volumeDebounceToken;
    private int _latencyTicker;
    private bool _loadingCaptureSources;
    private List<AudioRenderDevice> _captureDevices = [];
    private List<AirPlayDevice> _autoConnectDevices = [];
    private bool _loadingAutoConnect;
    private bool _loadingLanguage;
    private bool _loadingStartup;
    private bool _loudVolumeSuppressed;
    private bool _suppressVolumeHandling;
    private double _appliedVolumePercent;
    private int _quietHintToken;

    public MainViewModel(AirPlayCoordinator coordinator, Localization localization)
    {
        _coordinator = coordinator;
        _localization = localization;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        // Initialise the slider without going through the debounce/confirm path:
        // restoring a saved loud volume at startup must not pop a dialog.
        _appliedVolumePercent = Math.Clamp(coordinator.Volume * 100, 0, 100);
        _suppressVolumeHandling = true;
        VolumePercent = _appliedVolumePercent;
        VolumeText = $"{Math.Round(_appliedVolumePercent)}%";
        _suppressVolumeHandling = false;
        LatencyMs = coordinator.Latency;
        ConfirmedLatencyMs = coordinator.Latency;
        MultiDevice = coordinator.MultiDevice;

        _coordinator.DeviceDiscovered += OnDeviceDiscovered;
        _coordinator.AsyncError += OnAsyncError;
        _localization.LanguageChanged += OnLanguageChanged;
        AppLog.EntryWritten += OnLogEntry;

        foreach (AppLogEntry entry in AppLog.Snapshot())
        {
            LogEntries.Add(entry.ToString());
        }

        ApplyLanguage();
        UpdateStatusText();
        RefreshLatencyUi();
    }

    /// <summary>Set by the page: shows a modal confirmation (ContentDialog requires the XamlRoot).</summary>
    /// <summary>
    /// Set by the page: (title, message, acceptLabel, cancelLabel) → confirmed.
    /// ContentDialog needs the XamlRoot, so the page owns the dialog itself.
    /// </summary>
    /// <summary>
    /// Set by the page: (title, message, acceptLabel, cancelLabel, dontAskLabel?) →
    /// result. ContentDialog needs the XamlRoot, so the page owns the dialog.
    /// <paramref name="dontAskLabel"/> adds a checkbox when it is not null.
    /// </summary>
    public Func<string, string, string, string, string?, Task<ConfirmationResult>>? ConfirmAsync { get; set; }

    /// <summary>
    /// Shows a confirmation on the UI thread. These prompts are reached from
    /// continuations that may already be running on a thread-pool thread, and a
    /// ContentDialog can only be created and shown on the UI thread.
    /// </summary>
    private Task<bool> ConfirmOnUiAsync(string title, string message, string acceptLabel, string? cancelLabel = null)
        => ConfirmAndForgetAsync(title, message, acceptLabel, cancelLabel);

    private async Task<bool> ConfirmAndForgetAsync(
        string title,
        string message,
        string acceptLabel,
        string? cancelLabel = null)
    {
        ConfirmationResult result = await RequestConfirmationAsync(title, message, acceptLabel, cancelLabel)
            .ConfigureAwait(false);
        return result.Confirmed;
    }

    private Task<ConfirmationResult> RequestConfirmationAsync(
        string title,
        string message,
        string acceptLabel,
        string? cancelLabel = null,
        string? dontAskLabel = null)
    {
        if (ConfirmAsync is null)
        {
            return Task.FromResult(new ConfirmationResult(true, false));
        }

        var completion = new TaskCompletionSource<ConfirmationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUi(async () =>
        {
            try
            {
                completion.SetResult(await ConfirmAsync(
                    title,
                    message,
                    acceptLabel,
                    cancelLabel ?? _localization.T("cancel"),
                    dontAskLabel));
            }
            catch (Exception ex)
            {
                // A dialog that cannot be shown must not block playback.
                AppLog.Warn($"no pude mostrar el diálogo: {ex.Message}");
                completion.SetResult(new ConfirmationResult(true, false));
            }
        });

        return completion.Task;
    }

    public ObservableCollection<DeviceViewModel> Devices { get; } = new();

    public ObservableCollection<string> LogEntries { get; } = new();

    public ObservableCollection<string> CaptureSourceOptions { get; } = new();

    public ObservableCollection<string> AutoConnectOptions { get; } = new();

    public ObservableCollection<string> LanguageOptions { get; } = new();

    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ScanButtonText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LanguageButtonText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LanguageButtonTooltip { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MultiDeviceLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MultiDeviceHint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool MultiDevice { get; set; }

    [ObservableProperty]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    public partial bool IsPlayerVisible { get; set; }

    [ObservableProperty]
    public partial string PlayButtonText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PlayerStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double VolumePercent { get; set; }

    [ObservableProperty]
    public partial string VolumeText { get; set; } = "20%";

    [ObservableProperty]
    public partial double LatencyMs { get; set; }

    [ObservableProperty]
    public partial string LatencyText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LatencyStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool CanConfirmLatency { get; set; }

    [ObservableProperty]
    public partial string TestToneText { get; set; } = string.Empty;

    /// <summary>Segoe Fluent glyph for the play/stop button (no text emoji).</summary>
    [ObservableProperty]
    public partial string PlayButtonGlyph { get; set; } = "\uE768";

    [ObservableProperty]
    public partial bool CanPlayTestTone { get; set; }

    [ObservableProperty]
    public partial bool CanTogglePlay { get; set; }

    [ObservableProperty]
    public partial bool IsLowVolumeTipOpen { get; set; }

    [ObservableProperty]
    public partial string LowVolumeTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LowVolumeMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AutoConnectLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AutoConnectHint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool AutoConnectEnabled { get; set; }

    [ObservableProperty]
    public partial bool CanPickAutoConnect { get; set; }

    [ObservableProperty]
    public partial int SelectedAutoConnectIndex { get; set; }

    [ObservableProperty]
    public partial string LanguageLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int SelectedLanguageIndex { get; set; }

    [ObservableProperty]
    public partial string LogsHint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LogToggleText { get; set; } = string.Empty;

    /// <summary>The log list stays collapsed until the user asks for it.</summary>
    [ObservableProperty]
    public partial bool IsLogVisible { get; set; }

    [ObservableProperty]
    public partial string OpenLogsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OpenDataText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AboutLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AboutText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string VersionText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NoDeviceHint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DevicesTabLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PlaybackTabLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SettingsTabLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DevicesTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DevicesDescription { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DevicesEmptyTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DevicesEmptyDescription { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasDevices { get; set; }

    [ObservableProperty]
    public partial string PlaybackTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PlaybackDescription { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CurrentDeviceLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CurrentDeviceName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CurrentDeviceMeta { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PlaybackNoDeviceDescription { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GoToDevicesText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TestToneTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TestToneDescription { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TestToneHint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsTestToneHintVisible { get; set; }


    [ObservableProperty]
    public partial string GeneralGroupLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AudioGroupLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LogsGroupLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LanguageDescription { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AutoConnectDeviceLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StartWithWindowsLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StartWithWindowsDescription { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    [ObservableProperty]
    public partial string VolumeLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LatencyLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LatencyLowerLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LatencySaferLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LatencyHint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LatencyConfirmText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ManualSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ManualHint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ManualEndpointPlaceholder { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ManualNamePlaceholder { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ManualAddText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ManualIp { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ManualName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ManualStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsManualBusy { get; set; }

    [ObservableProperty]
    public partial string ToastMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsToastVisible { get; set; }

    [ObservableProperty]
    public partial string LogSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CaptureSourceLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CaptureSourceHint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int SelectedCaptureIndex { get; set; }

    public uint ConfirmedLatencyMs { get; private set; }

    public event Action? TrayLabelsChanged;

    public void Attach()
    {
        _coordinator.StartDiscovery();
        LoadStartupState();
    }

    /// <summary>
    /// Reads the Run key. If the entry points at another copy of the app (the user
    /// unzipped a new version somewhere else), it is refreshed to this executable.
    /// </summary>
    private void LoadStartupState()
    {
        _loadingStartup = true;
        try
        {
            StartWithWindows = StartupRegistration.IsEnabled();
            if (StartWithWindows && !StartupRegistration.IsCurrentExecutableRegistered())
            {
                StartupRegistration.SetEnabled(true);
                AppLog.Info("ruta de inicio automático actualizada a la copia actual");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException
                                       or IOException or InvalidOperationException)
        {
            AppLog.Warn($"no pude leer el inicio automático: {ex.Message}");
            StartWithWindows = false;
        }
        finally
        {
            _loadingStartup = false;
        }
    }

    /// <summary>
    /// Fills the capture-source picker. Index 0 always means "follow the system
    /// default", which is what most users want; the other entries let the audio be
    /// taken from a specific endpoint (for example a virtual cable).
    /// </summary>
    public void LoadCaptureSources()
    {
        _ = Task.Run(() =>
        {
            List<AudioRenderDevice> devices = [.. WasapiDevices.EnumerateRenderDevices()];
            RunOnUi(() =>
            {
                _loadingCaptureSources = true;
                _captureDevices = devices;

                CaptureSourceOptions.Clear();
                CaptureSourceOptions.Add(_localization.T("capture_follow_default"));
                foreach (AudioRenderDevice device in devices)
                {
                    CaptureSourceOptions.Add(device.DisplayName);
                }

                string? saved = _coordinator.CaptureDeviceId;
                ApplyCaptureSourceSelection(saved);
                _loadingCaptureSources = false;
            });
        });
    }

    /// <summary>
    /// Re-selects the capture source. Rebuilding the option list (or replacing the
    /// first entry when the language changes) resets a ComboBox to -1, and assigning
    /// the value it already holds raises no notification, so the control would stay
    /// visually empty. Bouncing through -1 forces the change through.
    /// </summary>
    private void ApplyCaptureSourceSelection(string? savedDeviceId)
    {
        int index = savedDeviceId is null
            ? 0
            : _captureDevices.FindIndex(d => d.Id == savedDeviceId) + 1;

        SelectedCaptureIndex = -1;
        SelectedCaptureIndex = index <= 0 || index >= CaptureSourceOptions.Count ? 0 : index;
    }

    /// <summary>
    /// Startup path: browse, then reconnect to the last HomePod (mDNS first, manual
    /// probe as fallback) and start playing, exactly like <c>bootstrap()</c>.
    /// </summary>
    public async Task BootstrapAsync()
    {
        ScanCommand.Execute(null);

        // Auto-connect is opt-in: it only runs when the user enabled it, and it
        // targets either the device they picked or the last one they used.
        if (!_coordinator.AutoConnect)
        {
            return;
        }

        PersistedDevice? last = _coordinator.AutoConnectDevice ?? _coordinator.LastDevice;

        if (last is null)
        {
            return;
        }

        StatusText = _localization.T("reconnecting", new { name = last.Name });

        DeviceViewModel? found = null;
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMilliseconds(ReconnectTimeoutMs);
        while (found is null && DateTimeOffset.UtcNow < deadline)
        {
            found = Devices.FirstOrDefault(d => d.Device.Addresses.Any(a => a.ToString() == last.Ip));
            if (found is null)
            {
                await Task.Delay(250).ConfigureAwait(false);
            }
        }

        if (found is null)
        {
            try
            {
                AirPlayDevice device = await _coordinator
                    .AddManualDeviceAsync(last.Ip, last.Port, last.Name)
                    .ConfigureAwait(false);
                RunOnUi(() =>
                {
                    RefreshDevices();
                    found = Devices.FirstOrDefault(d => d.Device.Addresses.Any(a => a.ToString() == last.Ip));
                });
            }
            catch (Exception ex) when (ex is AirPlayProbeException or ManualEndpointException or FormatException)
            {
                RunOnUi(() => ShowToast(_localization.T("cant_find", new { name = last.Name, err = ex.Message })));
                return;
            }
        }

        RunOnUi(() =>
        {
            if (found is null)
            {
                return;
            }

            _connectedGroupKey = found.GroupKey;
            _playerDevice = found;
            UpdateDeviceButtons();
        });

        await TogglePlayAsync().ConfigureAwait(false);
    }

    [RelayCommand]
    private void Scan()
    {
        IsScanning = true;
        StatusText = _localization.T("scan_searching");
        _coordinator.StartDiscovery();
        IsScanning = false;
        UpdateStatusText();
    }

    [RelayCommand]
    private async Task ToggleDeviceAsync(DeviceViewModel? device)
    {
        if (device is null || IsBusy)
        {
            return;
        }

        bool isConnected = IsPlaying
            ? _activeRouteKeys.Contains(device.GroupKey)
            : string.Equals(_connectedGroupKey, device.GroupKey, StringComparison.OrdinalIgnoreCase);

        if (isConnected)
        {
            await DisconnectAsync(device).ConfigureAwait(false);
        }
        else
        {
            await ConnectAsync(device).ConfigureAwait(false);
        }
    }

    private async Task ConnectAsync(DeviceViewModel device)
    {
        if (IsPlaying && !MultiDevice)
        {
            string current = _playerDevice?.Name ?? _localization.T("player_playing");
            bool confirmed = await ConfirmOnUiAsync(
                _localization.T("switch_title"),
                _localization.T("switch_message", new { from = current, to = device.Name }),
                _localization.T("switch_confirm", new { name = device.Name }));
            if (!confirmed)
            {
                return;
            }
        }

        RunOnUi(() =>
        {
            IsBusy = true;
            _connectingGroupKey = device.GroupKey;
            UpdateDeviceButtons();
        });

        try
        {
            if (IsPlaying)
            {
                StreamingInfo info = await StreamToAsync(device, add: MultiDevice).ConfigureAwait(false);
                RunOnUi(() =>
                {
                    if (!MultiDevice)
                    {
                        _activeRouteKeys.Clear();
                        _connectedGroupKey = device.GroupKey;
                        _playerDevice = device;
                    }

                    _activeRouteKeys.Add(device.GroupKey);
                    PlayerStatusText = _localization.T("player_playing");
                    UpdateDeviceButtons();
                });

                _ = info;
                return;
            }

            AirPlayProbeException? lastError = null;
            foreach (AirPlayDevice route in _coordinator.RoutesFor(device.Device))
            {
                string ip = route.PreferredAddress?.ToString() ?? route.Host;
                try
                {
                    await _coordinator.ConnectDeviceAsync(ip, route.Port, device.Name).ConfigureAwait(false);
                    _connectedRoute = route;
                    RunOnUi(() =>
                    {
                        _connectedGroupKey = device.GroupKey;
                        _playerDevice = device;
                        IsBusy = false;
                        UpdatePlayerUi();
                        UpdateDeviceButtons();
                    });

                    // Connecting only opens the session; ask before pushing audio.
                    bool startNow = await ConfirmOnUiAsync(
                        _localization.T("connect_ready_title", new { name = device.Name }),
                        _localization.T("connect_ready_message", new { name = device.Name }),
                        _localization.T("connect_ready_accept"),
                        _localization.T("later"));
                    if (startNow)
                    {
                        await TogglePlayAsync().ConfigureAwait(false);
                    }

                    return;
                }
                catch (AirPlayProbeException ex)
                {
                    lastError = ex;
                }
            }

            if (lastError is not null)
            {
                throw lastError;
            }
        }
        catch (Exception ex)
        {
            RunOnUi(() => StatusText = _localization.T("error_prefix", new { err = ex.Message }));
        }
        finally
        {
            RunOnUi(() =>
            {
                IsBusy = false;
                _connectingGroupKey = null;
                // The player card is driven by UpdatePlayerUi: without this the card
                // only appeared after the next discovery event (or a new scan).
                UpdatePlayerUi();
                UpdateDeviceButtons();
            });
        }
    }

    private async Task DisconnectAsync(DeviceViewModel device)
    {
        RunOnUi(() => IsBusy = true);

        try
        {
            if (IsPlaying && _activeRouteKeys.Count > 1)
            {
                AirPlayDevice? route = _coordinator.RoutesFor(device.Device).FirstOrDefault();
                if (route?.PreferredAddress is { } address)
                {
                    await _coordinator.RemoveStreamingAsync(address.ToString(), route.Port).ConfigureAwait(false);
                }

                RunOnUi(() =>
                {
                    _activeRouteKeys.Remove(device.GroupKey);
                    if (string.Equals(_connectedGroupKey, device.GroupKey, StringComparison.OrdinalIgnoreCase))
                    {
                        string? next = _activeRouteKeys.FirstOrDefault();
                        _connectedGroupKey = next;
                        _playerDevice = next is null ? null : Devices.FirstOrDefault(d => d.GroupKey == next);
                    }

                    UpdatePlayerUi();
                });
                return;
            }

            if (IsPlaying)
            {
                await _coordinator.StopStreamingAsync().ConfigureAwait(false);
            }

            _coordinator.DisconnectDevice();
            RunOnUi(() =>
            {
                IsPlaying = false;
                _activeRouteKeys.Clear();
                _connectedGroupKey = null;
                // Without clearing the player device the "send PC audio" card stayed
                // on screen after disconnecting.
                _playerDevice = null;
                PlayerStatusText = string.Empty;
                UpdatePlayerUi();
            });
        }
        catch (Exception ex)
        {
            RunOnUi(() => StatusText = _localization.T("error_prefix", new { err = ex.Message }));
        }
        finally
        {
            RunOnUi(() =>
            {
                IsBusy = false;
                UpdateDeviceButtons();
            });
        }
    }

    [RelayCommand]
    private async Task TogglePlayAsync()
    {
        if (IsBusy || _applyingLatency)
        {
            return;
        }

        DeviceViewModel? device = _playerDevice;
        if (device is null)
        {
            return;
        }

        RunOnUi(() => IsBusy = true);

        try
        {
            if (IsPlaying)
            {
                await _coordinator.StopStreamingAsync().ConfigureAwait(false);
                RunOnUi(() =>
                {
                    IsPlaying = false;
                    _activeRouteKeys.Clear();
                    PlayerStatusText = string.Empty;
                });
                return;
            }

            RunOnUi(() => PlayerStatusText = _localization.T("player_starting"));
            StreamingInfo info = await StreamToAsync(device, add: false).ConfigureAwait(false);
            await _coordinator.SetVolumeAsync((float)(VolumePercent / 100.0)).ConfigureAwait(false);

            RunOnUi(() =>
            {
                IsPlaying = true;
                _activeRouteKeys.Clear();
                _activeRouteKeys.Add(device.GroupKey);
                PlayerStatusText = _localization.T("player_playing");
                AppLog.Info($"reproduciendo en {info.Name} ({info.Ip}:{info.Port})");

                // The receiver may already have its own volume (set from the HomePod
                // or another sender): mirror that instead of forcing our own.
                if (info.Volume > 0)
                {
                    _appliedVolumePercent = Math.Round(info.Volume * 100);
                    SetVolumePercentInternal(_appliedVolumePercent);
                }
            });
        }
        catch (Exception ex)
        {
            RunOnUi(() => PlayerStatusText = _localization.T("error_prefix", new { err = ex.Message }));
        }
        finally
        {
            RunOnUi(() =>
            {
                IsBusy = false;
                UpdatePlayerUi();
                UpdateDeviceButtons();
            });
        }
    }

    private async Task<StreamingInfo> StreamToAsync(DeviceViewModel device, bool add)
    {
        Exception? lastError = null;

        foreach (AirPlayDevice route in _coordinator.RoutesFor(device.Device, _connectedRoute))
        {
            IPAddress? address = route.PreferredAddress;
            if (address is null)
            {
                continue;
            }

            var target = new StreamingTarget(
                address.ToString(),
                route.Port,
                device.Name,
                (float)(VolumePercent / 100.0),
                ConfirmedLatencyMs);

            try
            {
                return add
                    ? await _coordinator.AddStreamingAsync(target).ConfigureAwait(false)
                    : await _coordinator.StartStreamingAsync(target).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (ex.Message.StartsWith("stream:", StringComparison.Ordinal))
                {
                    break;
                }
            }
        }

        throw lastError ?? new StreamingException("dispositivo sin dirección IP");
    }

    [RelayCommand]
    private async Task ConfirmLatencyAsync()
    {
        if (IsBusy || _applyingLatency)
        {
            return;
        }

        uint requested = (uint)Math.Round(LatencyMs);
        if (requested == ConfirmedLatencyMs)
        {
            return;
        }

        RunOnUi(() =>
        {
            _applyingLatency = true;
            UpdatePlayerUi();
            RefreshLatencyUi();
        });

        try
        {
            bool restarted = await _coordinator.ConfirmLatencyAsync(requested).ConfigureAwait(false);
            ConfirmedLatencyMs = requested;
            RunOnUi(() =>
            {
                if (restarted)
                {
                    IsPlaying = true;
                    PlayerStatusText = _localization.T("player_playing");
                }
            });
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("latency_cooldown:", StringComparison.Ordinal))
        {
            // The cooldown countdown keeps refreshing the button state.
        }
        catch (Exception ex)
        {
            RunOnUi(() => ShowToast(_localization.T("latency_error", new { err = ex.Message })));
        }
        finally
        {
            RunOnUi(() =>
            {
                _applyingLatency = false;
                UpdatePlayerUi();
                RefreshLatencyUi();
                UpdateDeviceButtons();
            });
        }
    }

    [RelayCommand]
    private async Task AddManualDeviceAsync()
    {
        string ip = ManualIp.Trim();
        if (ip.Length == 0)
        {
            ManualStatus = _localization.T("manual_need_ip");
            return;
        }

        IsManualBusy = true;
        ManualStatus = _localization.T("manual_checking");

        try
        {
            AirPlayDevice device = await _coordinator
                .AddManualDeviceAsync(ip, null, ManualName.Trim())
                .ConfigureAwait(false);
            RunOnUi(() =>
            {
                ManualStatus = _localization.T("manual_ok", new { name = device.Name });
                ManualIp = string.Empty;
                ManualName = string.Empty;
            });
        }
        catch (Exception ex)
        {
            RunOnUi(() => ManualStatus = ex.Message);
        }
        finally
        {
            RunOnUi(() => IsManualBusy = false);
        }
    }

    [RelayCommand]
    private void SetLanguage(string? tag) => _localization.SetLanguage(tag);

    [RelayCommand]
    private void ToggleLogVisibility()
    {
        IsLogVisible = !IsLogVisible;
        LogToggleText = _localization.T(IsLogVisible ? "log_hide" : "log_show");
    }

    /// <summary>Raised when a view wants the shell to switch to another section.</summary>
    public event Action<string>? SectionRequested;

    [RelayCommand]
    private void GoToDevices() => SectionRequested?.Invoke("devices");

    [RelayCommand]
    private void GoToPlayback() => SectionRequested?.Invoke("playback");

    /// <summary>
    /// Plays a short tone through the active stream. Handy to confirm the audio
    /// path works before debugging anything else (the Rust build has the same
    /// helper as a CLI example).
    /// </summary>
    [RelayCommand]
    private async Task PlayTestToneAsync()
    {
        if (IsBusy || _playerDevice is null)
        {
            return;
        }

        // Synthesize first: the very first call loads the speech engine, and doing it
        // before playback starts keeps that latency out of the "sending" state.
        RunOnUi(() =>
        {
            IsBusy = true;
            PlayerStatusText = _localization.T("tone_playing");
            UpdatePlayerUi();
            UpdateDeviceButtons();
        });

        string sentence = _localization.T("test_speech_text");
        (short[] Samples, int SampleRate, int Channels)? clip =
            await Task.Run(() => SpeechTestClip.TrySynthesize(sentence, _localization.Tag)).ConfigureAwait(false);

        RunOnUi(() =>
        {
            IsBusy = false;
            UpdatePlayerUi();
            UpdateDeviceButtons();
        });

        // The tone travels through the live stream, so start playback first when the
        // user has only connected the device. TogglePlayAsync manages IsBusy itself,
        // so it must not be called while this method already holds the busy flag.
        if (!IsPlaying)
        {
            await TogglePlayAsync().ConfigureAwait(false);
            if (!IsPlaying)
            {
                return;
            }
        }

        RunOnUi(() =>
        {
            IsBusy = true;
            PlayerStatusText = _localization.T("tone_playing");
            UpdatePlayerUi();
            UpdateDeviceButtons();
        });

        try
        {
            AirPlayStreamSession? session = _coordinator.ActiveSessions.FirstOrDefault();
            if (session is null)
            {
                RunOnUi(() => ShowToast(_localization.T("error_prefix", new { err = _localization.T("player_starting") })));
                return;
            }

            // Speak a short sentence in the interface language; if the machine has no
            // usable voice, fall back to the 440 Hz tone.
            if (clip is { } speech)
            {
                AppLog.Info($"enviando prueba de voz: \"{sentence}\"");
                await session.PlayPcmAsync(speech.Samples, speech.SampleRate, speech.Channels).ConfigureAwait(false);
            }
            else
            {
                AppLog.Warn("sin voz disponible: se envía el tono de 440 Hz");
                await session.PlayTestToneAsync(440, 2000, 0.3f).ConfigureAwait(false);
            }

            RunOnUi(() => PlayerStatusText = _localization.T("player_playing"));
        }
        catch (Exception ex)
        {
            AppLog.Error("prueba de conexión falló", ex);
            RunOnUi(() => ShowToast(_localization.T("error_prefix", new { err = ex.Message })));
        }
        finally
        {
            RunOnUi(() =>
            {
                IsBusy = false;
                UpdatePlayerUi();
                UpdateDeviceButtons();
            });
        }
    }

    public void RequestShowHideWindow() => WindowToggleRequested?.Invoke();

    public event Action? WindowToggleRequested;

    partial void OnVolumePercentChanged(double value)
    {
        VolumeText = $"{Math.Round(value)}%";

        if (_suppressVolumeHandling)
        {
            return;
        }

        int token = ++_volumeDebounceToken;
        _ = Task.Run(async () =>
        {
            await Task.Delay(120).ConfigureAwait(false);
            if (token != _volumeDebounceToken)
            {
                return;
            }

            double requested = value;

            // Hearing-health guard: ask every time the volume crosses the threshold
            // upwards, unless the user ticked "don't remind me again" in this run.
            bool wasAbove = _appliedVolumePercent > LoudVolumeThreshold;
            bool isAbove = requested > LoudVolumeThreshold;
            if (isAbove && !wasAbove && !_loudVolumeSuppressed)
            {
                // The "keep" button restores the volume the user actually had, so
                // its label shows that value instead of the threshold.
                double previousVolume = Math.Round(_appliedVolumePercent);

                ConfirmationResult result = await RequestConfirmationAsync(
                    _localization.T("loud_title"),
                    _localization.T("loud_message", new { threshold = LoudVolumeThreshold }),
                    _localization.T("loud_accept"),
                    _localization.T("loud_keep", new { volume = previousVolume }),
                    _localization.T("loud_dont_ask")).ConfigureAwait(false);

                if (!result.Confirmed)
                {
                    RunOnUi(() => SetVolumePercentInternal(_appliedVolumePercent));
                    return;
                }

                _loudVolumeSuppressed = result.DontAskAgain;
            }

            try
            {
                await _coordinator.SetVolumeAsync((float)(requested / 100.0)).ConfigureAwait(false);
                _appliedVolumePercent = requested;
            }
            catch (Exception ex)
            {
                RunOnUi(() => PlayerStatusText = _localization.T("vol_error_prefix", new { err = ex.Message }));
            }

            // Dropping into the very quiet range only warns: it does not block.
            if (requested <= QuietVolumeThreshold)
            {
                ShowQuietHint();
            }
        });
    }

    /// <summary>Sets the slider value without re-entering the debounce/confirm logic.</summary>
    private void SetVolumePercentInternal(double percent)
    {
        _suppressVolumeHandling = true;
        VolumePercent = percent;
        VolumeText = $"{Math.Round(percent)}%";
        _suppressVolumeHandling = false;
    }

    private void ShowQuietHint()
    {
        int token = ++_quietHintToken;
        RunOnUi(() => IsLowVolumeTipOpen = true);

        _ = Task.Run(async () =>
        {
            await Task.Delay(QuietHintDuration).ConfigureAwait(false);
            if (token != _quietHintToken)
            {
                return;
            }

            RunOnUi(() => IsLowVolumeTipOpen = false);
        });
    }

    partial void OnLatencyMsChanged(double value) => RefreshLatencyUi();

    partial void OnSelectedCaptureIndexChanged(int value)
    {
        if (_loadingCaptureSources)
        {
            return;
        }

        _coordinator.CaptureDeviceId = value <= 0 || value - 1 >= _captureDevices.Count
            ? null
            : _captureDevices[value - 1].Id;
    }

    partial void OnAutoConnectEnabledChanged(bool value)
    {
        CanPickAutoConnect = value;
        if (!_loadingAutoConnect)
        {
            _coordinator.AutoConnect = value;
        }
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loadingStartup)
        {
            return;
        }

        try
        {
            StartupRegistration.SetEnabled(value);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException
                                       or IOException or InvalidOperationException)
        {
            RunOnUi(() =>
            {
                _loadingStartup = true;
                StartWithWindows = !value;
                _loadingStartup = false;
                ShowToast(_localization.T("settings_startup_failed", new { err = ex.Message }));
            });
        }
    }

    partial void OnSelectedAutoConnectIndexChanged(int value)
    {
        if (_loadingAutoConnect)
        {
            return;
        }

        // Index 0 means "whatever I used last", the rest are concrete devices.
        _coordinator.AutoConnectDevice = value <= 0 || value - 1 >= _autoConnectDevices.Count
            ? null
            : ToPersisted(_autoConnectDevices[value - 1]);
    }

    partial void OnSelectedLanguageIndexChanged(int value)
    {
        if (_loadingLanguage || value < 0 || value >= Localization.Options.Count)
        {
            return;
        }

        _localization.SetLanguage(Localization.Options[value].Tag);
    }

    private void RefreshAutoConnectOptions()
    {
        _loadingAutoConnect = true;

        AirPlayDevice[] devices = _coordinator.GroupedDevices().Values
            .OrderBy(d => d.Kind == DeviceKind.HomePod ? 0 : 1)
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        _autoConnectDevices = [.. devices];

        string? currentId = _coordinator.AutoConnectDevice?.Ip;

        AutoConnectOptions.Clear();
        AutoConnectOptions.Add(_localization.T("auto_connect_last"));
        foreach (AirPlayDevice device in devices)
        {
            AutoConnectOptions.Add($"{device.Name} ({device.PreferredAddress?.ToString() ?? device.Host})");
        }

        int index = 0;
        if (currentId is not null)
        {
            int found = devices.ToList().FindIndex(
                d => string.Equals(d.PreferredAddress?.ToString(), currentId, StringComparison.OrdinalIgnoreCase));
            index = found >= 0 ? found + 1 : 0;
        }

        SelectedAutoConnectIndex = -1;
        SelectedAutoConnectIndex = index;
        AutoConnectEnabled = _coordinator.AutoConnect;
        CanPickAutoConnect = AutoConnectEnabled;
        _loadingAutoConnect = false;
    }

    private static PersistedDevice ToPersisted(AirPlayDevice device) => new()
    {
        Ip = device.PreferredAddress?.ToString() ?? device.Host,
        Port = device.Port,
        Name = device.Name,
    };

    partial void OnMultiDeviceChanged(bool value)
    {
        if (value || !IsPlaying || _activeRouteKeys.Count <= 1)
        {
            _coordinator.SaveMultiDevice(value);
            return;
        }

        _ = Task.Run(async () =>
        {
            string keep = _playerDevice?.Name ?? _localization.T("player_playing");
            bool confirmed = await ConfirmOnUiAsync(
                _localization.T("multi_off_title"),
                _localization.T("multi_off_message", new { name = keep }),
                _localization.T("multi_off_confirm", new { name = keep }));

            if (!confirmed)
            {
                RunOnUi(() => MultiDevice = true);
                return;
            }

            string[] keepKeys = _playerDevice is null ? [] : [_playerDevice.GroupKey];
            foreach (string key in _activeRouteKeys.Where(k => !keepKeys.Contains(k)).ToArray())
            {
                DeviceViewModel? device = Devices.FirstOrDefault(d => d.GroupKey == key);
                if (device?.Device.PreferredAddress is { } address)
                {
                    await _coordinator.RemoveStreamingAsync(address.ToString(), device.Device.Port).ConfigureAwait(false);
                }

                RunOnUi(() => _activeRouteKeys.Remove(key));
            }

            _coordinator.SaveMultiDevice(false);
        });
    }

    private void OnDeviceDiscovered(AirPlayDevice device) =>
        RunOnUi(() =>
        {
            RefreshDevices();
            _ = device;
        });

    private void OnAsyncError(string message)
    {
        RunOnUi(() =>
        {
            string text = message == "capture_interrupted"
                ? _localization.T("capture_interrupted")
                : message;
            ShowToast(text);

            if (IsPlaying)
            {
                IsPlaying = false;
                _activeRouteKeys.Clear();
                _ = _coordinator.StopStreamingAsync();
                PlayerStatusText = _localization.T("error_prefix", new { err = text });
                UpdatePlayerUi();
                UpdateDeviceButtons();
            }
        });
    }

    private void OnLanguageChanged() => RunOnUi(() =>
    {
        ApplyLanguage();
        RefreshDevices();
        UpdatePlayerUi();
        RefreshLatencyUi();
    });

    private void OnLogEntry(AppLogEntry entry) =>
        RunOnUi(() =>
        {
            LogEntries.Add(entry.ToString());
            while (LogEntries.Count > 400)
            {
                LogEntries.RemoveAt(0);
            }
        });

    private void ApplyLanguage()
    {
        Subtitle = _localization.T("subtitle");
        ScanButtonText = _localization.T("scan");
        LanguageButtonText = _localization.Tag switch
        {
            "zh" => "中文",
            "es" => "ES",
            _ => "EN",
        };
        LanguageButtonTooltip = _localization.T("lang_toggle_title");
        MultiDeviceLabel = _localization.T("multi_device");
        MultiDeviceHint = _localization.T("multi_device_hint");
        VolumeLabel = _localization.T("volume");
        LatencyLabel = _localization.T("latency");
        LatencyLowerLabel = _localization.T("latency_lower");
        LatencySaferLabel = _localization.T("latency_safer");
        LatencyHint = _localization.T("latency_hint");
        LatencyConfirmText = _localization.T("latency_confirm");
        ManualSummary = _localization.T("manual_summary");
        ManualHint = _localization.T("manual_hint");
        ManualEndpointPlaceholder = _localization.T("manual_endpoint_placeholder");
        ManualNamePlaceholder = _localization.T("manual_name_placeholder");
        ManualAddText = _localization.T("manual_add");
        LogSummary = _localization.T("log_summary");
        CaptureSourceLabel = _localization.T("capture_source");
        CaptureSourceHint = _localization.T("capture_source_hint");
        TestToneText = _localization.T("playback_tone_action");
        AutoConnectLabel = _localization.T("auto_connect");
        AutoConnectHint = _localization.T("auto_connect_hint");
        LanguageLabel = _localization.T("settings_language");
        LogsHint = _localization.T("settings_logs_hint");
        LogToggleText = _localization.T(IsLogVisible ? "log_hide" : "log_show");
        OpenLogsText = _localization.T("settings_open_logs");
        OpenDataText = _localization.T("settings_open_settings");
        AboutLabel = _localization.T("settings_about");
        AboutText = _localization.T("settings_about_text");
        VersionText = _localization.T(
            "settings_version",
            new { version = typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0" });
        NoDeviceHint = _localization.T("no_device_hint");
        LowVolumeTitle = _localization.T("quiet_title");
        LowVolumeMessage = _localization.T("quiet_message", new { threshold = QuietVolumeThreshold });
        GeneralGroupLabel = _localization.T("settings_group_general");
        DevicesTitle = _localization.T("devices_title");
        DevicesDescription = _localization.T("devices_description");
        DevicesEmptyTitle = _localization.T("devices_empty_title");
        DevicesEmptyDescription = _localization.T("devices_empty_description");
        PlaybackTitle = _localization.T("playback_title");
        PlaybackDescription = _localization.T("playback_description");
        CurrentDeviceLabel = _localization.T("playback_current_device");
        PlaybackNoDeviceDescription = _localization.T("playback_no_device_description");
        GoToDevicesText = _localization.T("playback_go_to_devices");
        TestToneTitle = _localization.T("playback_tone");
        TestToneDescription = _localization.T("playback_tone_description");
        TestToneHint = _localization.T("playback_tone_needs_playback");
        AudioGroupLabel = _localization.T("settings_group_audio");
        LogsGroupLabel = _localization.T("settings_group_logs");
        LanguageDescription = _localization.T("settings_language_desc");
        AutoConnectDeviceLabel = _localization.T("settings_auto_connect_device");
        StartWithWindowsLabel = _localization.T("settings_start_with_windows");
        StartWithWindowsDescription = _localization.T("settings_start_with_windows_desc");
        DevicesTabLabel = _localization.T("tab_devices");
        PlaybackTabLabel = _localization.T("tab_playback");
        SettingsTabLabel = _localization.T("tab_settings");

        _loadingLanguage = true;
        LanguageOptions.Clear();
        foreach ((string _, string nativeName) in Localization.Options)
        {
            LanguageOptions.Add(nativeName);
        }

        SelectedLanguageIndex = -1;
        SelectedLanguageIndex = Math.Max(0, Localization.Options
            .ToList()
            .FindIndex(option => option.Tag == _localization.Tag));
        _loadingLanguage = false;

        if (CaptureSourceOptions.Count > 0)
        {
            _loadingCaptureSources = true;
            CaptureSourceOptions[0] = _localization.T("capture_follow_default");
            ApplyCaptureSourceSelection(_coordinator.CaptureDeviceId);
            _loadingCaptureSources = false;
        }

        RefreshAutoConnectOptions();

        UpdateStatusText();
        UpdatePlayerUi();
        TrayLabelsChanged?.Invoke();
    }

    private void RefreshDevices()
    {
        IReadOnlyDictionary<string, AirPlayDevice> grouped = _coordinator.GroupedDevices();

        var ordered = grouped.Values
            .OrderBy(d => d.Kind == DeviceKind.HomePod ? 0 : 1)
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var wanted = new HashSet<string>(ordered.Select(d => DeviceGrouping.GroupKey(d)), StringComparer.OrdinalIgnoreCase);

        foreach (string stale in _known.Keys.Where(k => !wanted.Contains(k)).ToArray())
        {
            DeviceViewModel? viewModel = _known[stale];
            _known.Remove(stale);
            Devices.Remove(viewModel);
        }

        for (int index = 0; index < ordered.Count; index++)
        {
            string key = DeviceGrouping.GroupKey(ordered[index]);
            if (_known.TryGetValue(key, out DeviceViewModel? existing))
            {
                int currentIndex = Devices.IndexOf(existing);
                if (currentIndex != index)
                {
                    Devices.Move(currentIndex, index);
                }
            }
            else
            {
                var viewModel = new DeviceViewModel(ordered[index], _localization.Language.ToString());
                _known[key] = viewModel;
                Devices.Insert(Math.Min(index, Devices.Count), viewModel);
            }
        }

        UpdateDeviceButtons();
        UpdateStatusText();
        UpdatePlayerUi();
        RefreshAutoConnectOptions();
        HasDevices = _known.Count > 0;
    }

    private void UpdateDeviceButtons()
    {
        foreach (DeviceViewModel device in Devices)
        {
            bool connected = IsPlaying
                ? _activeRouteKeys.Contains(device.GroupKey)
                : string.Equals(_connectedGroupKey, device.GroupKey, StringComparison.OrdinalIgnoreCase);

            device.IsConnecting = string.Equals(_connectingGroupKey, device.GroupKey, StringComparison.OrdinalIgnoreCase);
            device.IsConnected = connected;
            device.IsEnabled = !IsBusy && !_applyingLatency;
            device.ButtonText = device.IsConnecting
                ? _localization.T("connecting")
                : connected
                    ? _localization.T("disconnect")
                    : _localization.T("connect");
        }
    }

    private void UpdatePlayerUi()
    {
        IsPlayerVisible = _playerDevice is not null;

        if (_playerDevice is null)
        {
            // No device picked yet: keep the button readable instead of leaving it
            // as an empty bar.
            CurrentDeviceName = _localization.T("playback_no_device");
            CurrentDeviceMeta = _localization.T("playback_no_device_description");
            PlayButtonText = _localization.T("play_generic");
            PlayButtonGlyph = "\uE768"; // Play
            CanPlayTestTone = false;
            CanTogglePlay = false;
            return;
        }

        DeviceViewModel device = _playerDevice;
        CurrentDeviceName = device.Name;
        CurrentDeviceMeta = device.Meta;
        // The tone now starts the stream itself, so it only needs a connected device.
        CanPlayTestTone = !IsBusy && _playerDevice is not null;
        CanTogglePlay = IsPlaying || !IsBusy;
        IsTestToneHintVisible = !IsPlaying;
        PlayButtonText = IsPlaying
            ? _localization.T("stop")
            : _localization.T("play_to", new { name = device.Name });
        PlayButtonGlyph = IsPlaying ? "\uE71A" : "\uE768"; // Stop : Play
    }

    private void UpdateStatusText()
    {
        int count = _known.Count;
        StatusText = count == 1
            ? _localization.T("devices_count_one")
            : _localization.T("devices_count_other", new { n = count });
    }

    private void RefreshLatencyUi()
    {
        LatencyText = $"{Math.Round(LatencyMs)} ms";
        TimeSpan cooldown = _coordinator.LatencyCooldownRemaining;

        if (_applyingLatency)
        {
            LatencyStatusText = _localization.T("latency_applying");
        }
        else if (cooldown > TimeSpan.Zero)
        {
            LatencyStatusText = _localization.T("latency_cooldown", new { seconds = (int)Math.Ceiling(cooldown.TotalSeconds) });
        }
        else
        {
            LatencyStatusText = _localization.T(
                Math.Abs(LatencyMs - ConfirmedLatencyMs) < 1 ? "latency_current" : "latency_pending");
        }

        CanConfirmLatency = !IsBusy && !_applyingLatency && cooldown <= TimeSpan.Zero &&
                            Math.Abs(LatencyMs - ConfirmedLatencyMs) >= 1;

        if (cooldown > TimeSpan.Zero && Interlocked.CompareExchange(ref _latencyTicker, 1, 0) == 0)
        {
            _ = Task.Run(async () =>
            {
                while (_coordinator.LatencyCooldownRemaining > TimeSpan.Zero)
                {
                    await Task.Delay(250).ConfigureAwait(false);
                    RunOnUi(RefreshLatencyUi);
                }

                Interlocked.Exchange(ref _latencyTicker, 0);
                RunOnUi(RefreshLatencyUi);
            });
        }
    }

    private void ShowToast(string message)
    {
        ToastMessage = message;
        IsToastVisible = true;

        _ = Task.Run(async () =>
        {
            await Task.Delay(6000).ConfigureAwait(false);
            RunOnUi(() =>
            {
                if (ToastMessage == message)
                {
                    IsToastVisible = false;
                }
            });
        });
    }

    private void RunOnUi(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
        }
        else
        {
            _dispatcher.TryEnqueue(() => action());
        }
    }
}

