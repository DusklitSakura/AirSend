using System.Collections.Concurrent;
using System.Net;
using AirSend.Core.Logging;

namespace AirSend.Core.Discovery;

/// <summary>
/// Long lived discovery service: browses <c>_airplay._tcp</c> and <c>_raop._tcp</c>,
/// normalises every mDNS answer into an <see cref="AirPlayDevice"/> and replays its
/// cache on request.
/// </summary>
/// <remarks>
/// Port of <c>DiscoveryState</c> + <c>start_discovery_stream</c> in
/// <c>src-tauri/src/lib.rs</c>: the daemon is created once (creating one per scan
/// leaked a socket storm) and the cached devices are re-emitted every time the UI
/// asks for a new scan, because mDNS announcements are not repeated on demand.
/// </remarks>
public sealed class AirPlayDiscovery : IAsyncDisposable
{
    public const string AirPlayService = "_airplay._tcp.local";
    public const string RaopService = "_raop._tcp.local";

    private readonly MdnsBrowser _browser = new();
    private readonly ConcurrentDictionary<string, AirPlayDevice> _cache = new(StringComparer.OrdinalIgnoreCase);

    public event Action<AirPlayDevice>? DeviceDiscovered;

    /// <summary>Forwards raw mDNS datagrams (diagnostics only).</summary>
    public event Action<byte[]>? RawPacketReceived
    {
        add => _browser.RawPacketReceived += value;
        remove => _browser.RawPacketReceived -= value;
    }

    public IReadOnlyCollection<AirPlayDevice> CachedDevices => _cache.Values.ToArray();

    public bool IsBrowsing => _browser.IsRunning;

    public AirPlayDiscovery()
    {
        _browser.ServiceResolved += OnServiceResolved;
    }

    /// <summary>
    /// Starts browsing (idempotent) and replays every cached device so the caller's
    /// list is populated immediately, exactly like the Tauri command does.
    /// </summary>
    public void StartBrowsing()
    {
        foreach (AirPlayDevice device in _cache.Values)
        {
            DeviceDiscovered?.Invoke(device);
        }

        if (_browser.IsRunning)
        {
            AppLog.Debug($"discovery ya en curso, replayé {_cache.Count} cacheados");
            return;
        }

        _browser.Start([AirPlayService, RaopService]);
        AppLog.Info("discovery: browsing _airplay._tcp y _raop._tcp");
    }

    public void StopBrowsing()
    {
        _browser.Stop();
        AppLog.Info("discovery: detenido");
    }

    /// <summary>Adds a device that was not found over mDNS to the cache (manual IP entry).</summary>
    public void Remember(AirPlayDevice device)
    {
        _cache[device.Id] = device;
        DeviceDiscovered?.Invoke(device);
    }

    /// <summary>One-shot browse used by tests and by the first UI fetch.</summary>
    public static async Task<IReadOnlyList<AirPlayDevice>> BrowseOnceAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        await using var discovery = new AirPlayDiscovery();
        var seen = new ConcurrentDictionary<string, AirPlayDevice>(StringComparer.OrdinalIgnoreCase);
        discovery.DeviceDiscovered += device => seen[device.Id] = device;
        discovery.StartBrowsing();
        await Task.Delay(timeout, cancellationToken).ConfigureAwait(false);
        discovery.StopBrowsing();
        return seen.Values.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        _browser.ServiceResolved -= OnServiceResolved;
        await _browser.DisposeAsync().ConfigureAwait(false);
    }

    private void OnServiceResolved(MdnsService service)
    {
        AirPlayDevice device = ToDevice(service);
        AppLog.Info($"discovered {service.ServiceType}: {device}");
        _cache[device.Id] = device;
        DeviceDiscovered?.Invoke(device);
    }

    internal static AirPlayDevice ToDevice(MdnsService service)
    {
        string? hardwareId = NormalizeHardwareId(service.DeviceId);
        if (hardwareId is null &&
            string.Equals(service.ServiceType, RaopService, StringComparison.OrdinalIgnoreCase))
        {
            string prefix = service.InstanceName.Split('@')[0];
            hardwareId = NormalizeHardwareId(prefix);
        }

        string displayName = string.Equals(service.ServiceType, RaopService, StringComparison.OrdinalIgnoreCase)
            ? StripRaopPrefix(service.InstanceName)
            : service.InstanceName;

        string? features = service.Features;
        bool supportsAirPlay2 =
            (service.SourceVersion?.StartsWith('3') ?? false) ||
            (features?.Contains("0x", StringComparison.OrdinalIgnoreCase) ?? false);

        return new AirPlayDevice
        {
            Id = service.FullName,
            HardwareId = hardwareId,
            Name = displayName,
            Host = service.HostName,
            Addresses = service.Addresses,
            Port = service.Port,
            Kind = DeviceKindExtensions.FromModel(service.Model),
            Model = service.Model,
            Features = features,
            SupportsAirPlay2 = supportsAirPlay2,
        };
    }

    /// <summary>RAOP instances are announced as <c>AA:BB:CC:DD:EE:FF@Living Room</c>.</summary>
    internal static string StripRaopPrefix(string instanceName)
    {
        int separator = instanceName.IndexOf('@');
        if (separator <= 0)
        {
            return instanceName;
        }

        string prefix = instanceName[..separator];
        bool looksLikeHardwareId = prefix.Length is >= 12 and <= 17 &&
            prefix.All(c => Uri.IsHexDigit(c) || c is ':' or '-');
        return looksLikeHardwareId ? instanceName[(separator + 1)..] : instanceName;
    }

    /// <summary>Keeps only 12 hex digits, lower-cased (upstream <c>normalize_hardware_id</c>).</summary>
    internal static string? NormalizeHardwareId(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        string hex = new(id.Where(c => c is not (':' or '-')).ToArray());
        return hex.Length == 12 && hex.All(Uri.IsHexDigit) ? hex.ToLowerInvariant() : null;
    }
}
