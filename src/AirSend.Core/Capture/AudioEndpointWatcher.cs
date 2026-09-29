using AirSend.Core.Logging;

namespace AirSend.Core.Capture;

/// <summary>
/// Watches which playback endpoint Windows currently uses as the default for the
/// console role, so a capture that follows the system default can be rebuilt on
/// the endpoint the user just switched to.
/// </summary>
/// <remarks>
/// This is a one second poll of <c>IMMDeviceEnumerator::GetDefaultAudioEndpoint</c>
/// rather than an <c>IMMNotificationClient</c> callback. The notification interface
/// would report the change immediately, but it requires a managed COM-callable
/// class whose vtable is called from a thread owned by the audio service: a bad
/// signature or an exception there takes the whole process down, and it cannot be
/// covered by the test suite. A single COM read per second costs nothing and one
/// second of latency is imperceptible when the user switches output devices.
/// </remarks>
public sealed class AudioEndpointWatcher : IDisposable
{
    public const int DefaultPollIntervalMs = 1000;

    private readonly Func<string?> _readDefaultDeviceId;
    private readonly int _pollIntervalMs;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();

    private Task? _poll;
    private string? _lastDefaultDeviceId;
    private bool _initialized;
    private bool _started;
    private bool _disposed;

    public AudioEndpointWatcher()
        : this(WasapiDevices.GetDefaultRenderDeviceId, DefaultPollIntervalMs)
    {
    }

    /// <summary>Test seam: lets the polling logic run without any audio device.</summary>
    internal AudioEndpointWatcher(Func<string?> readDefaultDeviceId, int pollIntervalMs)
    {
        _readDefaultDeviceId = readDefaultDeviceId;
        _pollIntervalMs = Math.Max(10, pollIntervalMs);
    }

    /// <summary>
    /// Raised after the default playback device changed. The argument is the new
    /// endpoint id, or null when Windows has no default playback device at all.
    /// </summary>
    public event Action<string?>? DefaultOutputChanged;

    /// <summary>Endpoint id observed by the last poll, or null before the first one.</summary>
    public string? ObservedDefaultDeviceId
    {
        get
        {
            lock (_gate)
            {
                return _lastDefaultDeviceId;
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed)
            {
                return;
            }

            _started = true;
        }

        _poll = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Reads the current default device once. Returns true when that is a change
    /// from the previously observed value (the very first read only seeds the
    /// baseline and reports false).
    /// </summary>
    public bool Refresh() => Publish(ReadCurrent());

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _cts.Cancel();

        try
        {
            _poll?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
        {
            // Expected on shutdown.
        }

        _cts.Dispose();
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        Refresh();

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_pollIntervalMs));
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                Refresh();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private string? ReadCurrent()
    {
        try
        {
            return _readDefaultDeviceId();
        }
        catch (Exception ex)
        {
            // A device that disappears between two registry reads must not look like
            // a switch to "no output": keep the last value and try again next tick.
            AppLog.Warn($"no pude leer la salida predeterminada: {ex.Message}");
            return ObservedDefaultDeviceId;
        }
    }

    private bool Publish(string? current)
    {
        bool changed;
        lock (_gate)
        {
            changed = _initialized
                && !string.Equals(_lastDefaultDeviceId, current, StringComparison.OrdinalIgnoreCase);
            _initialized = true;
            _lastDefaultDeviceId = current;
        }

        if (!changed)
        {
            return false;
        }

        AppLog.Info($"la salida de audio predeterminada del sistema cambió a {current ?? "(ninguna)"}");
        DefaultOutputChanged?.Invoke(current);
        return true;
    }
}
