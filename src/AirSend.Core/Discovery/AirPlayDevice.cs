using System.Net;

namespace AirSend.Core.Discovery;

/// <summary>
/// A discovered (or manually added) AirPlay receiver.
/// C# counterpart of <c>Device</c> in <c>crates/airplay-core/src/discovery.rs</c>.
/// </summary>
public sealed class AirPlayDevice
{
    /// <summary>Service fullname for mDNS devices, <c>manual://ip:port</c> for manual ones.</summary>
    public required string Id { get; init; }

    /// <summary>Normalised 12 hex digit device id announced over mDNS, when known.</summary>
    public string? HardwareId { get; init; }

    public required string Name { get; init; }

    public required string Host { get; init; }

    public IReadOnlyList<IPAddress> Addresses { get; init; } = Array.Empty<IPAddress>();

    public ushort Port { get; init; } = 7000;

    public DeviceKind Kind { get; init; } = DeviceKind.OtherAirPlay;

    public string? Model { get; init; }

    public string? Features { get; init; }

    public bool SupportsAirPlay2 { get; init; }

    /// <summary>
    /// First IPv4 address, falling back to the first address of any family.
    /// The Tauri frontend uses the same preference when it has to pick a route.
    /// </summary>
    public IPAddress? PreferredAddress =>
        Addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        ?? Addresses.FirstOrDefault();

    public AirPlayDevice With(IReadOnlyList<IPAddress> addresses) => new()
    {
        Id = Id,
        HardwareId = HardwareId,
        Name = Name,
        Host = Host,
        Addresses = addresses,
        Port = Port,
        Kind = Kind,
        Model = Model,
        Features = Features,
        SupportsAirPlay2 = SupportsAirPlay2,
    };

    public override string ToString() => $"{Name} [{Kind}] {PreferredAddress}:{Port}";
}
