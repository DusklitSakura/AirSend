using System.Text.Json;
using System.Text.Json.Serialization;
using AirSend.Core.Logging;
using AirSend.Core.Streaming;

namespace AirSend.Services;

public sealed class PersistedDevice
{
    [JsonPropertyName("ip")]
    public string Ip { get; set; } = string.Empty;

    [JsonPropertyName("port")]
    public ushort Port { get; set; } = 7000;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public sealed class Settings
{
    [JsonPropertyName("last_device")]
    public PersistedDevice? LastDevice { get; set; }

    [JsonPropertyName("volume")]
    public float? Volume { get; set; }

    [JsonPropertyName("latency")]
    public uint? Latency { get; set; }

    [JsonPropertyName("multi_device")]
    public bool MultiDevice { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    /// <summary>Ed25519 seed of the sender identity, base64 encoded.</summary>
    [JsonPropertyName("identity_seed")]
    public string? IdentitySeed { get; set; }

    /// <summary>Playback endpoint to capture; null means "follow the system default".</summary>
    [JsonPropertyName("capture_device_id")]
    public string? CaptureDeviceId { get; set; }

    /// <summary>Connect and start streaming automatically when the app launches.</summary>
    [JsonPropertyName("auto_connect")]
    public bool AutoConnect { get; set; }

    /// <summary>Device to use for auto-connect; null means "the last device used".</summary>
    [JsonPropertyName("auto_connect_device")]
    public PersistedDevice? AutoConnectDevice { get; set; }

    /// <summary>How often to look for a new release: "startup", "daily", "weekly", "never".</summary>
    [JsonPropertyName("update_check")]
    public string? UpdateCheck { get; set; }

    /// <summary>When the last successful update check happened, in UTC.</summary>
    [JsonPropertyName("update_last_check")]
    public DateTimeOffset? UpdateLastCheck { get; set; }

}

/// <summary>
/// JSON settings in <c>%APPDATA%\AirSend\settings.json</c>.
/// Counterpart of the <c>tauri-plugin-store</c> file the Rust app keeps in
/// <c>%APPDATA%/&lt;bundle-id&gt;/settings.json</c>, with the same keys.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private Settings _settings = new();

    public SettingsStore(string applicationName = "AirSend")
    {
        string directory = AppPaths.DataDirectory(applicationName);
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "settings.json");
        Load();
    }

    public string FilePath => _path;

    public PersistedDevice? LastDevice
    {
        get => _settings.LastDevice;
        set
        {
            _settings.LastDevice = value;
            Save();
        }
    }

    public float Volume
    {
        get => _settings.Volume ?? LatencyProfile.DefaultInitialVolume;
        set
        {
            _settings.Volume = Math.Clamp(value, 0f, 1f);
            Save();
        }
    }

    public uint Latency
    {
        get => _settings.Latency ?? LatencyProfile.DefaultLatencyMs;
        set
        {
            uint clamped = Math.Clamp(value, LatencyProfile.MinLatencyMs, LatencyProfile.MaxLatencyMs);
            _settings.Latency = clamped;
            Save();
        }
    }

    public bool MultiDevice
    {
        get => _settings.MultiDevice;
        set
        {
            _settings.MultiDevice = value;
            Save();
        }
    }

    public string? Language
    {
        get => _settings.Language;
        set
        {
            _settings.Language = value;
            Save();
        }
    }

    public byte[]? IdentitySeed
    {
        get => _settings.IdentitySeed is null ? null : Convert.FromBase64String(_settings.IdentitySeed);
        set
        {
            _settings.IdentitySeed = value is null ? null : Convert.ToBase64String(value);
            Save();
        }
    }

    public string? CaptureDeviceId
    {
        get => _settings.CaptureDeviceId;
        set
        {
            _settings.CaptureDeviceId = string.IsNullOrWhiteSpace(value) ? null : value;
            Save();
        }
    }

    public bool AutoConnect
    {
        get => _settings.AutoConnect;
        set
        {
            _settings.AutoConnect = value;
            Save();
        }
    }

    public PersistedDevice? AutoConnectDevice
    {
        get => _settings.AutoConnectDevice;
        set
        {
            _settings.AutoConnectDevice = value;
            Save();
        }
    }

    /// <summary>Update check interval as stored, e.g. "startup".</summary>
    public string? UpdateCheckPolicy
    {
        get => _settings.UpdateCheck;
        set
        {
            _settings.UpdateCheck = string.IsNullOrWhiteSpace(value) ? null : value;
            Save();
        }
    }

    public DateTimeOffset? UpdateLastCheck
    {
        get => _settings.UpdateLastCheck;
        set
        {
            _settings.UpdateLastCheck = value;
            Save();
        }
    }

    public void ClearLastDevice()
    {
        _settings.LastDevice = null;
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                _settings = new Settings();
                return;
            }

            string json = File.ReadAllText(_path);
            _settings = JsonSerializer.Deserialize<Settings>(json, Options) ?? new Settings();

            if (_settings.Latency is { } latency && !LatencyProfile.IsValid(latency))
            {
                _settings.Latency = null;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            AppLog.Warn($"no pude leer {_path}: {ex.Message}");
            _settings = new Settings();
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_settings, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"no pude guardar {_path}: {ex.Message}");
        }
    }
}
