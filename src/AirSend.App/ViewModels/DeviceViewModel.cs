using AirSend.Core.Discovery;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AirSend.ViewModels;

/// <summary>One row of the device list. Mirrors the <c>render()</c> loop in ui/src/main.ts.</summary>
public partial class DeviceViewModel : ObservableObject
{
    public DeviceViewModel(AirPlayDevice device, string language)
    {
        Device = device;
        UpdateLanguage(language);
    }

    public AirPlayDevice Device { get; }

    public string Id => Device.Id;

    public string GroupKey => DeviceGrouping.GroupKey(Device);

    public DeviceKind Kind => Device.Kind;

    /// <summary>Segoe Fluent glyph shown in the device card avatar.</summary>
    public string KindGlyph => Device.Kind switch
    {
        DeviceKind.HomePod => "\uE767",        // Volume (speaker)
        DeviceKind.AppleTv => "\uE7F4",        // TVMonitor
        DeviceKind.AirportExpress => "\uE701", // WiFi
        _ => "\uE774",                         // Globe
    };

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Meta { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ButtonText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsConnected { get; set; }

    [ObservableProperty]
    public partial bool IsConnecting { get; set; }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;

    /// <summary>IPv4 address the Tauri frontend would use for the RTSP route.</summary>
    public string PreferredAddress => Device.PreferredAddress?.ToString() ?? Device.Host;

    public void UpdateLanguage(string language)
    {
        Name = Device.Name;
        Meta = BuildMeta();
        _ = language;
    }

    private string BuildMeta()
    {
        string address = Device.PreferredAddress?.ToString() ?? Device.Host;
        string kind = Device.Kind.ToDisplayLabel();
        string suffix = Device.SupportsAirPlay2 ? " · AirPlay 2" : string.Empty;
        return $"{kind} · {address}:{Device.Port}{suffix}";
    }
}
