namespace AirSend.Core.Discovery;

/// <summary>
/// The same device de-duplication the Tauri frontend applies in
/// <c>ui/src/devices.ts</c>: a HomePod announces itself over both
/// <c>_airplay._tcp</c> and <c>_raop._tcp</c>, and the UI must show one row.
/// </summary>
public static class DeviceGrouping
{
    public static bool IsRaop(AirPlayDevice device) =>
        device.Id.Contains("._raop._tcp.", StringComparison.OrdinalIgnoreCase);

    public static bool IsAirPlay(AirPlayDevice device) =>
        device.Id.Contains("._airplay._tcp.", StringComparison.OrdinalIgnoreCase);

    public static string GroupKey(AirPlayDevice device)
    {
        if (!IsRaop(device) && !IsAirPlay(device))
        {
            return device.Id;
        }

        if (!string.IsNullOrEmpty(device.HardwareId))
        {
            return $"hardware:{device.HardwareId}";
        }

        return $"host:{device.Host.ToLowerInvariant()}|{device.Name.ToLowerInvariant()}";
    }

    /// <summary>Route preference: prefer <c>_airplay._tcp</c> for HomePods, <c>_raop._tcp</c> otherwise.</summary>
    public static IReadOnlyList<AirPlayDevice> RoutesFor(
        IEnumerable<AirPlayDevice> discovered,
        string groupKey,
        AirPlayDevice? preferred = null)
    {
        List<AirPlayDevice> routes = discovered
            .Where(candidate => string.Equals(GroupKey(candidate), groupKey, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (routes.Count == 0)
        {
            return Array.Empty<AirPlayDevice>();
        }

        bool preferAirPlay = routes[0].Kind == DeviceKind.HomePod;
        routes.Sort((a, b) =>
        {
            int aScore = preferAirPlay ? (IsAirPlay(a) ? 1 : 0) : (IsRaop(a) ? 1 : 0);
            int bScore = preferAirPlay ? (IsAirPlay(b) ? 1 : 0) : (IsRaop(b) ? 1 : 0);
            return bScore - aScore;
        });

        if (preferred is not null)
        {
            routes.Sort((a, b) =>
                string.Equals(b.Id, preferred.Id, StringComparison.OrdinalIgnoreCase) ? 1 :
                string.Equals(a.Id, preferred.Id, StringComparison.OrdinalIgnoreCase) ? -1 : 0);
        }

        return routes;
    }

    /// <summary>Collapses every discovered device into the single row the UI displays.</summary>
    public static IReadOnlyDictionary<string, AirPlayDevice> Group(IEnumerable<AirPlayDevice> discovered)
    {
        var result = new Dictionary<string, AirPlayDevice>(StringComparer.OrdinalIgnoreCase);

        foreach (IGrouping<string, AirPlayDevice> group in discovered.GroupBy(GroupKey, StringComparer.OrdinalIgnoreCase))
        {
            AirPlayDevice[] members = group.ToArray();
            AirPlayDevice presentation = members.FirstOrDefault(IsAirPlay) ?? members[0];
            AirPlayDevice transport = presentation.Kind == DeviceKind.HomePod
                ? members.FirstOrDefault(IsAirPlay) ?? presentation
                : members.FirstOrDefault(IsRaop) ?? presentation;

            result[group.Key] = new AirPlayDevice
            {
                Id = group.Key,
                HardwareId = transport.HardwareId ?? presentation.HardwareId,
                Name = presentation.Name,
                Host = transport.Host,
                Addresses = transport.Addresses.Count > 0 ? transport.Addresses : presentation.Addresses,
                Port = transport.Port,
                Kind = presentation.Kind,
                Model = presentation.Model ?? transport.Model,
                // A HomePod announces the feature bitmask on the RAOP record while
                // the AirPlay record carries the display name, so take the first
                // member that actually has it.
                Features = transport.Features
                    ?? presentation.Features
                    ?? members.Select(m => m.Features).FirstOrDefault(f => !string.IsNullOrEmpty(f)),
                SupportsAirPlay2 = members.Any(m => m.SupportsAirPlay2),
            };
        }

        return result;
    }
}
