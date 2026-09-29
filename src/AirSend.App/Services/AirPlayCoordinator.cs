using System.Collections.Concurrent;
using System.Net;
using AirSend.Core.Discovery;
using AirSend.Core.Logging;
using AirSend.Core.Pairing;
using AirSend.Core.Probe;
using AirSend.Core.Streaming;

namespace AirSend.Services;

public sealed record StreamingTarget(
    string Ip,
    ushort Port,
    string Name,
    float Volume,
    uint LatencyMs,
    string? CaptureDeviceId = null);

/// <summary>
/// Application level orchestration: discovery, manual endpoints, connections and
/// the set of live streams.
/// </summary>
/// <remarks>
/// This is the C# counterpart of the <c>#[tauri::command]</c> surface in
/// <c>src-tauri/src/lib.rs</c>: every command the React frontend could invoke has
/// a method here, with the same validation and the same failure messages.
/// </remarks>
public sealed class AirPlayCoordinator : IAsyncDisposable
{
    private readonly AirPlayDiscovery _discovery = new();
    private readonly SettingsStore _settings;
    private readonly ConcurrentDictionary<string, AirPlayStreamSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AirPlayDevice> _discovered = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _actionLock = new();

    private PairingIdentity? _identity;
    private DateTimeOffset? _lastLatencyChange;
    private bool _actionBusy;

    public AirPlayCoordinator(SettingsStore settings)
    {
        _settings = settings;
        _discovery.DeviceDiscovered += OnDeviceDiscovered;
        Latency = settings.Latency;
        Volume = settings.Volume;
        MultiDevice = settings.MultiDevice;
    }

    /// <summary>Raised on the thread pool whenever a device appears or changes.</summary>
    public event Action<AirPlayDevice>? DeviceDiscovered;

    /// <summary>Raised for asynchronous failures the UI must surface as a toast.</summary>
    public event Action<string>? AsyncError;

    /// <summary>Raised when a stream stops on its own (capture failure, receiver gone).</summary>
    public event Action? StreamingStopped;

    public bool IsPlaying => !_sessions.IsEmpty;

    public bool IsBusy
    {
        get => _actionBusy;
        private set => _actionBusy = value;
    }

    public float Volume { get; private set; }

    public uint Latency { get; private set; }

    public bool MultiDevice { get; private set; }

    public PersistedDevice? LastDevice
    {
        get => _settings.LastDevice;
        set => _settings.LastDevice = value;
    }

    /// <summary>Playback endpoint to capture; null follows the Windows default.</summary>
    public string? CaptureDeviceId
    {
        get => _settings.CaptureDeviceId;
        set => _settings.CaptureDeviceId = value;
    }

    /// <summary>Auto-connect on startup, and the device it targets (null = last used).</summary>
    public bool AutoConnect
    {
        get => _settings.AutoConnect;
        set => _settings.AutoConnect = value;
    }

    public PersistedDevice? AutoConnectDevice
    {
        get => _settings.AutoConnectDevice;
        set => _settings.AutoConnectDevice = value;
    }

    public string? ConnectedDeviceId { get; private set; }

    public IReadOnlyCollection<string> ActiveRouteKeys => _sessions.Keys.ToArray();

    /// <summary>Live sessions, used by the UI for the test tone and diagnostics.</summary>
    public IReadOnlyCollection<AirPlayStreamSession> ActiveSessions => _sessions.Values.ToArray();

    public IReadOnlyCollection<AirPlayDevice> DiscoveredDevices => _discovered.Values.ToArray();

    public PairingIdentity Identity => _identity ??= LoadOrCreateIdentity();

    /// <summary>Starts browsing and replays the cached devices (upstream behaviour).</summary>
    public void StartDiscovery() => _discovery.StartBrowsing();

    public void StopDiscovery() => _discovery.StopBrowsing();

    public IReadOnlyDictionary<string, AirPlayDevice> GroupedDevices() => DeviceGrouping.Group(_discovered.Values);

    public IReadOnlyList<AirPlayDevice> RoutesFor(AirPlayDevice device, AirPlayDevice? preferred = null) =>
        DeviceGrouping.RoutesFor(_discovered.Values, DeviceGrouping.GroupKey(device), preferred);

    /// <summary>Adds a device typed by the user; verifies it really is AirPlay first.</summary>
    public async Task<AirPlayDevice> AddManualDeviceAsync(string ip, ushort? port, string? name, CancellationToken cancellationToken = default)
    {
        (IPAddress address, ushort resolvedPort) = ManualEndpoint.Parse(ip, port);
        ProbeResult probe = await AirPlayProbe.ProbeAsync(address, resolvedPort, cancellationToken).ConfigureAwait(false);

        AirPlayDevice device = AirPlayProbe.BuildManualDevice(address, resolvedPort, name);
        if (probe.ServerHeader is { } server)
        {
            device = new AirPlayDevice
            {
                Id = device.Id,
                Name = device.Name,
                Host = device.Host,
                Addresses = device.Addresses,
                Port = device.Port,
                Kind = device.Kind,
                Features = server,
                SupportsAirPlay2 = probe.LooksLikeAirPlay2,
            };
        }

        _discovered[device.Id] = device;
        _discovery.Remember(device);
        return device;
    }

    /// <summary>Ping style check used when the user presses "Connect".</summary>
    public async Task ConnectDeviceAsync(string ip, ushort port, string name, CancellationToken cancellationToken = default)
    {
        if (!IPAddress.TryParse(ip, out IPAddress? address))
        {
            throw new AirPlayProbeException($"IP inválida: {ip}");
        }

        await AirPlayProbe.ProbeAsync(address, port, cancellationToken).ConfigureAwait(false);
        ConnectedDeviceId = $"manual://{ip}:{port}";
        AppLog.Info($"connect_device: {name} ({ip}:{port}) respondió a OPTIONS");
    }

    public void DisconnectDevice()
    {
        ConnectedDeviceId = null;
    }

    /// <summary>Starts streaming to a single receiver, replacing any current stream.</summary>
    public async Task<StreamingInfo> StartStreamingAsync(StreamingTarget target, CancellationToken cancellationToken = default)
    {
        await StopStreamingAsync().ConfigureAwait(false);
        return await AddStreamingAsync(target, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Adds a receiver without interrupting the ones already playing.</summary>
    public async Task<StreamingInfo> AddStreamingAsync(StreamingTarget target, CancellationToken cancellationToken = default)
    {
        string key = $"{target.Ip}:{target.Port}";
        if (_sessions.TryGetValue(key, out AirPlayStreamSession? existing))
        {
            return existing.Info;
        }

        AirPlayDevice device = _discovered.Values.FirstOrDefault(
                d => d.Addresses.Any(a => a.ToString() == target.Ip) && d.Port == target.Port)
            ?? new AirPlayDevice
            {
                Id = key,
                Name = target.Name,
                Host = target.Ip,
                Addresses = [IPAddress.Parse(target.Ip)],
                Port = target.Port,
            };

        AirPlayStreamSession session = await AirPlayStreamSession
            .OpenAsync(
                device,
                Identity,
                target.Volume,
                target.LatencyMs,
                captureDeviceId: target.CaptureDeviceId ?? CaptureDeviceId,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        session.ErrorOccurred += message =>
        {
            AppLog.Error($"stream error: {message}");
            AsyncError?.Invoke(message);
        };

        if (!_sessions.TryAdd(key, session))
        {
            await session.DisposeAsync().ConfigureAwait(false);
            return _sessions[key].Info;
        }

        _settings.LastDevice = new PersistedDevice
        {
            Ip = target.Ip,
            Port = target.Port,
            Name = target.Name,
        };

        return session.Info;
    }

    public async Task RemoveStreamingAsync(string ip, ushort port, CancellationToken cancellationToken = default)
    {
        string key = $"{ip}:{port}";
        if (_sessions.TryRemove(key, out AirPlayStreamSession? session))
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task StopStreamingAsync()
    {
        foreach (KeyValuePair<string, AirPlayStreamSession> entry in _sessions.ToArray())
        {
            if (_sessions.TryRemove(entry.Key, out AirPlayStreamSession? session))
            {
                try
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    AppLog.Warn($"error cerrando el stream {entry.Key}: {ex.Message}");
                }
            }
        }

        StreamingStopped?.Invoke();
    }

    public async Task SetVolumeAsync(float volume, CancellationToken cancellationToken = default)
    {
        float clamped = Math.Clamp(volume, 0f, 1f);
        Volume = clamped;
        _settings.Volume = clamped;

        foreach (AirPlayStreamSession session in _sessions.Values)
        {
            try
            {
                await session.SetVolumeAsync(clamped, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                AppLog.Warn($"no pude ajustar el volumen: {ex.Message}");
            }
        }
    }

    public TimeSpan LatencyCooldownRemaining =>
        LatencyProfile.CooldownRemaining(_lastLatencyChange, DateTimeOffset.UtcNow);

    /// <summary>
    /// Applies a new latency setting. A live stream is restarted because AirPlay
    /// negotiates the buffer during SETUP, exactly like the Rust implementation.
    /// </summary>
    public async Task<bool> ConfirmLatencyAsync(uint latencyMs, CancellationToken cancellationToken = default)
    {
        LatencyProfile.Validate(latencyMs);

        TimeSpan remaining = LatencyCooldownRemaining;
        if (remaining > TimeSpan.Zero)
        {
            throw new InvalidOperationException($"latency_cooldown:{(int)remaining.TotalMilliseconds}");
        }

        uint previous = Latency;
        if (previous == latencyMs)
        {
            return false;
        }

        var targets = new List<StreamingTarget>();
        foreach (KeyValuePair<string, AirPlayStreamSession> entry in _sessions.ToArray())
        {
            StreamingInfo info = entry.Value.Info;
            targets.Add(new StreamingTarget(info.Ip, info.Port, info.Name, info.Volume, previous));
        }

        _settings.Latency = latencyMs;
        Latency = latencyMs;

        if (targets.Count == 0)
        {
            _lastLatencyChange = DateTimeOffset.UtcNow;
            return false;
        }

        await StopStreamingAsync().ConfigureAwait(false);

        try
        {
            foreach (StreamingTarget target in targets)
            {
                await AddStreamingAsync(target with { LatencyMs = latencyMs }, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            _settings.Latency = previous;
            Latency = previous;
            throw;
        }
        finally
        {
            _lastLatencyChange = DateTimeOffset.UtcNow;
        }

        return true;
    }

    public void SaveMultiDevice(bool enabled)
    {
        MultiDevice = enabled;
        _settings.MultiDevice = enabled;
    }

    /// <summary>Serialises the UI initiated actions, mirroring the <c>actionBusy</c> guard.</summary>
    public async Task<T> RunExclusiveAsync<T>(Func<Task<T>> action)
    {
        lock (_actionLock)
        {
            if (_actionBusy)
            {
                throw new InvalidOperationException("busy");
            }

            _actionBusy = true;
        }

        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _actionBusy = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _discovery.DeviceDiscovered -= OnDeviceDiscovered;
        await StopStreamingAsync().ConfigureAwait(false);
        await _discovery.DisposeAsync().ConfigureAwait(false);
    }

    private void OnDeviceDiscovered(AirPlayDevice device)
    {
        _discovered[device.Id] = device;
        DeviceDiscovered?.Invoke(device);
    }

    private PairingIdentity LoadOrCreateIdentity()
    {
        if (_settings.IdentitySeed is { Length: 32 } seed)
        {
            return PairingIdentity.Import(seed);
        }

        PairingIdentity identity = PairingIdentity.Generate();
        _settings.IdentitySeed = identity.Seed;
        AppLog.Info($"identidad de emisor creada: {identity.Identifier}");
        return identity;
    }
}
