using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using AirSend.Core.Audio;
using AirSend.Core.Capture;
using AirSend.Core.Crypto;
using AirSend.Core.Discovery;
using AirSend.Core.Logging;
using AirSend.Core.Pairing;
using AirSend.Core.Rtsp;

namespace AirSend.Core.Streaming;

public sealed record StreamingInfo(
    string Ip,
    ushort Port,
    string Name,
    int SampleRate,
    int Channels,
    float Volume);

public sealed class StreamingException(string message) : Exception(message);

/// <summary>
/// One live AirPlay 2 stream: RTSP session, pairing, RTP audio pump and the
/// feedback heartbeat that keeps the receiver from dropping the session.
/// Port of <c>open_live_stream</c>/<c>StreamHandle</c> from
/// <c>crates/airplay-core/src/streaming.rs</c> plus the connection sequence in
/// <c>airplay-client/src/connection.rs</c>.
/// </summary>
public sealed class AirPlayStreamSession : IAsyncDisposable
{
    /// <summary>The HomePod drops a session that has been silent for ~10 s; the
    /// upstream uses 2 s and so do we.</summary>
    private static readonly TimeSpan FeedbackInterval = TimeSpan.FromSeconds(2);

    private const int ControlQueueCapacity = 64;

    private readonly AirPlayDevice _device;
    private readonly PairingIdentity _identity;
    private readonly RtspClient _client;
    private readonly AlacEncoder _encoder;
    private readonly NtpTimingServer _timingServer;
    private readonly BlockingCollection<short[]> _audioQueue = new(ControlQueueCapacity);
    private readonly CancellationTokenSource _cts = new();
    private readonly AudioCipher _audioCipher;
    private readonly byte[] _sessionKey;
    private readonly string _sessionId;

    private RtpAudioSender? _sender;
    private System.Net.Sockets.UdpClient? _controlSocket;
    private Task? _controlLoop;
    private WasapiLoopbackCapture? _capture;
    private readonly object _captureLock = new();
    private string? _captureDeviceId;
    private string? _appliedCaptureDeviceId;
    private bool _captureRequested;
    private bool _captureClosed;
    private int _captureRestartPending;
    private Task? _pump;
    private Task? _heartbeat;
    private long _framesSent;
    private bool _teardownSent;
    private long _controlPacketsReceived;
    private long _retransmitRequestsAnswered;
    private long _syncPacketsSent;
    private uint _lastSyncRtpTimestamp;
    private IPEndPoint? _receiverControlEndPoint;

    private AirPlayStreamSession(
        AirPlayDevice device,
        PairingIdentity identity,
        RtspClient client,
        AlacEncoder encoder,
        NtpTimingServer timingServer,
        AudioCipher audioCipher,
        byte[] sessionKey,
        string sessionId,
        float volume)
    {
        _device = device;
        _identity = identity;
        _client = client;
        _encoder = encoder;
        _timingServer = timingServer;
        _audioCipher = audioCipher;
        _sessionKey = sessionKey;
        _sessionId = sessionId;
        Volume = volume;
    }

    public float Volume { get; private set; }

    public bool IsRunning => _pump is { IsCompleted: false };

    public long FramesSent => Interlocked.Read(ref _framesSent);

    public long FramesDropped => _audioQueue.Count;

    /// <summary>RTCP packets the receiver sent us (retransmit requests and sync).</summary>
    public long ControlPacketsReceived => Interlocked.Read(ref _controlPacketsReceived);

    public long RetransmitRequestsAnswered => Interlocked.Read(ref _retransmitRequestsAnswered);

    /// <summary>Sync packets (payload type 84) that tell the receiver how RTP maps to NTP time.</summary>
    public long SyncPacketsSent => Interlocked.Read(ref _syncPacketsSent);

    /// <summary>Timing requests the receiver sent to our NTP server.</summary>
    public long TimingRequestsReceived => _timingServer.RequestsReceived;

    public StreamingInfo Info => new(
        _device.PreferredAddress?.ToString() ?? _device.Host,
        _device.Port,
        _device.Name,
        LatencyProfile.SampleRate,
        LatencyProfile.Channels,
        Volume);

    /// <summary>Raised for asynchronous failures (capture interrupted, heartbeat dead).</summary>
    public event Action<string>? ErrorOccurred;

    public static async Task<AirPlayStreamSession> OpenAsync(
        AirPlayDevice device,
        PairingIdentity identity,
        float initialVolume,
        uint latencyMs,
        string pin = AirPlayPairing.TransientPin,
        bool captureAudio = true,
        string? captureDeviceId = null,
        CancellationToken cancellationToken = default)
    {
        LatencyProfile.Validate(latencyMs);

        IPAddress address = device.PreferredAddress
            ?? throw new StreamingException($"device {device.Name} has no IP address");

        var client = new RtspClient(address, device.Port);
        NtpTimingServer? timingServer = null;

        try
        {
            await client.ConnectAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            AppLog.Info($"RTSP conectado a {address}:{device.Port}");

            string clientInstance = identity.CompactDeviceId;
            client.AddSessionHeader("User-Agent", "AirPlay/745.83");
            client.AddSessionHeader("X-Apple-Client-Name", "AirSend");
            client.AddSessionHeader("X-Apple-Device-ID", identity.DeviceId);
            client.AddSessionHeader("DACP-ID", clientInstance);
            client.AddSessionHeader("Client-Instance", clientInstance);
            client.AddSessionHeader("Active-Remote", "1234567890");

            // GET /info is required before pairing on AirPlay 2 receivers.
            RtspResponse infoResponse = await client.SendAsync("GET", "/info", cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            LogDeviceInfo(infoResponse);

            SessionKeys keys = await AirPlayPairing
                .PairSetupTransientAsync(client, pin, cancellationToken)
                .ConfigureAwait(false);

            client.SetCipher(keys.CreateCipher());

            // From this point on every request is encrypted; the receiver answers
            // with an encrypted OPTIONS so we know the channel is live.
            RtspResponse options = await client.SendAsync("OPTIONS", "*", cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AppLog.Debug($"OPTIONS cifrado → {options.StatusCode} ({options.Header("Public") ?? "sin Public"})");

            timingServer = new NtpTimingServer();

            byte[] setupPhase1 = BuildSetupPhase1(timingServer.Port, identity.DeviceId);
            RtspResponse phase1 = await client
                .SendAsync("SETUP", client.BaseUri, setupPhase1, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!phase1.IsSuccess)
            {
                throw new StreamingException($"SETUP phase 1 returned {phase1.StatusCode}");
            }

            Dictionary<string, object?>? phase1Body = ParsePlist(phase1);
            DumpPlist("SETUP phase 1 response", phase1Body);
            string sessionId = phase1.Header("Session") ?? "1";
            client.SetSessionId(sessionId);

            int eventPort = (int)(BinaryPlist.GetInteger(phase1Body, "eventPort") ?? 0);
            if (eventPort > 0)
            {
                // Reverse connection to the events port; some receivers answer 500
                // to SETUP phase 2 when this is missing.
                try
                {
                    using var eventsClient = new System.Net.Sockets.TcpClient();
                    await eventsClient.ConnectAsync(address, eventPort, cancellationToken).ConfigureAwait(false);
                    AppLog.Debug($"conexión de eventos establecida con el puerto {eventPort}");
                }
                catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException)
                {
                    AppLog.Warn($"no pude conectar al puerto de eventos {eventPort}: {ex.Message}");
                }
            }

            byte[] audioKey = RandomNumberGenerator.GetBytes(32);

            // Real listening socket: the receiver sends RTCP (retransmit requests,
            // sync packets) to this port and expects answers from the same port.
            var controlSocket = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Any, 0));
            int controlPort = ((IPEndPoint)controlSocket.Client.LocalEndPoint!).Port;

            byte[] setupPhase2 = BuildSetupPhase2(
                encoder: new AlacEncoder(AudioFormatDescription.AirPlayDefault),
                sessionKey: audioKey,
                controlPort: controlPort,
                latencyMs: latencyMs);

            RtspResponse phase2 = await client
                .SendAsync("SETUP", client.BaseUri, setupPhase2, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!phase2.IsSuccess)
            {
                // Receivers answer 400 when the previous session is still being
                // released; one short retry covers the common case of switching
                // devices or restarting playback quickly.
                AppLog.Warn($"SETUP phase 2 → {phase2.StatusCode}, reintentando una vez");
                await Task.Delay(750, cancellationToken).ConfigureAwait(false);
                phase2 = await client
                    .SendAsync("SETUP", client.BaseUri, setupPhase2, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!phase2.IsSuccess)
            {
                throw new StreamingException($"SETUP phase 2 returned {phase2.StatusCode}");
            }

            (int? dataPortValue, int? controlPortValue) = ExtractStreamPorts(phase2);
            DumpPlist("SETUP phase 2 response", ParsePlist(phase2));
            int dataPort = dataPortValue ?? 6000;
            int receiverControlPort = controlPortValue ?? 0;
            AppLog.Info(
                $"SETUP completado: audio → {address}:{dataPort}, control ← {controlPort} " +
                $"(control del receptor {receiverControlPort})");

            RtspResponse record = await client
                .SendAsync("RECORD", client.BaseUri, Array.Empty<byte>(), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AppLog.Info($"RECORD → {record.StatusCode} {record.Reason}");

            var session = new AirPlayStreamSession(
                device,
                identity,
                client,
                new AlacEncoder(AudioFormatDescription.AirPlayDefault),
                timingServer,
                new AudioCipher(audioKey),
                audioKey,
                sessionId,
                Math.Clamp(initialVolume, 0f, 1f))
            {
                _controlSocket = controlSocket,
            };

            // SSRC 0 and an RTP clock that starts at zero: that is what the working
            // Rust sender uses, and what the FLUSH below announces to the receiver.
            session._sender = new RtpAudioSender(address, dataPort, session._audioCipher, ssrc: 0);
            session._sender.SetInitialTimestamp(0);
            session._receiverControlEndPoint = receiverControlPort > 0
                ? new IPEndPoint(address, receiverControlPort)
                : null;

            // FLUSH + RTP-Info tells the receiver where the stream starts; without
            // it the HomePod accepts the session (the LED lights up) but keeps
            // waiting for a position it never sees, so nothing is audible.
            var flush = new RtspRequest("FLUSH", client.BaseUri)
                .WithHeader(
                    "RTP-Info",
                    $"seq={session._sender.NextSequence};rtptime={session._sender.NextTimestamp}");
            RtspResponse flushResponse = await client.SendAsync(flush, cancellationToken).ConfigureAwait(false);
            AppLog.Info($"FLUSH → {flushResponse.StatusCode} (rtptime 0)");

            session.StartControlLoop();

            // Honour whatever volume the receiver already has: only push our own
            // preference when the receiver does not report one.
            float? receiverVolume = await session.TryGetReceiverVolumeAsync(cancellationToken).ConfigureAwait(false);
            if (receiverVolume is { } reported)
            {
                session.Volume = reported;
                AppLog.Info($"se mantiene el volumen del receptor ({reported:0.###})");
            }
            else
            {
                await session.SetVolumeAsync(initialVolume, cancellationToken).ConfigureAwait(false);
            }

            // The pump always runs: with capture disabled it still forwards frames
            // pushed by the caller (test tone, protocol tests).
            session.StartPump(address, controlPort, captureDeviceId, captureAudio);

            return session;
        }
        catch
        {
            timingServer?.Dispose();
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Enqueues a captured PCM frame; drops it when the queue is full.</summary>
    public void PushPcm(CapturedFrame frame)
    {
        if (!_audioQueue.IsAddingCompleted)
        {
            _audioQueue.TryAdd(frame.Samples);
        }
    }

    /// <summary>
    /// Queues a synthetic sine tone through the same ALAC + RTP + encryption path
    /// the capture uses. Same idea as <c>play_test_tone</c> in the Rust build: it
    /// confirms the audio path on real hardware without needing any playing audio
    /// on the machine.
    /// </summary>
    public async Task PlayTestToneAsync(
        double frequencyHz = 440,
        int durationMs = 2000,
        float amplitude = 0.30f,
        CancellationToken cancellationToken = default)
    {
        double sampleRate = LatencyProfile.SampleRate;
        double phaseStep = 2 * Math.PI * frequencyHz / sampleRate;
        double phase = 0;
        double clampedAmplitude = Math.Clamp(amplitude, 0f, 1f) * short.MaxValue;
        int totalFrames = (int)(sampleRate * durationMs / 1000.0);
        var samples = new short[totalFrames * LatencyProfile.Channels];

        for (int i = 0; i < totalFrames; i++)
        {
            short value = (short)(Math.Sin(phase) * clampedAmplitude);
            for (int channel = 0; channel < LatencyProfile.Channels; channel++)
            {
                samples[i * LatencyProfile.Channels + channel] = value;
            }

            phase += phaseStep;
            if (phase > 2 * Math.PI)
            {
                phase -= 2 * Math.PI;
            }
        }

        await PlayPcmAsync(samples, LatencyProfile.SampleRate, LatencyProfile.Channels, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Streams a block of PCM (for example synthesized speech) to the receiver,
    /// converting it to the AirPlay format and pacing it in real time.
    /// </summary>
    public async Task PlayPcmAsync(
        short[] samples,
        int sampleRate,
        int channels,
        CancellationToken cancellationToken = default)
    {
        short[] pcm = ConvertToAirPlayFormat(samples, sampleRate, channels);
        int totalFrames = pcm.Length / LatencyProfile.Channels;
        int produced = 0;
        int packetsSent = 0;
        double msPerPacket = LatencyProfile.FramesPerPacket * 1000.0 / LatencyProfile.SampleRate;
        var clock = Stopwatch.StartNew();

        while (produced < totalFrames && !cancellationToken.IsCancellationRequested)
        {
            int frames = Math.Min(LatencyProfile.FramesPerPacket, totalFrames - produced);
            short[] block = pcm.AsSpan(
                produced * LatencyProfile.Channels,
                frames * LatencyProfile.Channels).ToArray();

            PushPcm(new CapturedFrame(block, LatencyProfile.Channels, LatencyProfile.SampleRate));
            produced += frames;
            packetsSent++;

            // Pace against an absolute schedule: Windows timers only have ~15 ms
            // resolution, so sleeping "per packet" would send at half speed and
            // starve the receiver's buffer.
            double remainingMs = packetsSent * msPerPacket - clock.Elapsed.TotalMilliseconds;
            if (remainingMs > 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(remainingMs), cancellationToken).ConfigureAwait(false);
            }
        }

        // Let the pump drain the queue before the caller tears the session down.
        while (_audioQueue.Count > 0)
        {
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }

        await Task.Delay(300, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Converts arbitrary PCM to the 44.1 kHz / stereo / 16-bit AirPlay format.</summary>
    private static short[] ConvertToAirPlayFormat(
        short[] samples,
        int sampleRate,
        int channels)
    {
        int targetChannels = LatencyProfile.Channels;
        int sourceFrames = channels == 0 ? 0 : samples.Length / channels;

        if (sampleRate == LatencyProfile.SampleRate && channels == targetChannels)
        {
            return samples.ToArray();
        }

        int targetFrames = sampleRate == LatencyProfile.SampleRate
            ? sourceFrames
            : (int)(sourceFrames * (double)LatencyProfile.SampleRate / sampleRate);

        var converted = new short[targetFrames * targetChannels];
        for (int frame = 0; frame < targetFrames; frame++)
        {
            // Linear interpolation; fine for speech prompts.
            double sourcePosition = frame * (double)sampleRate / LatencyProfile.SampleRate;
            int sourceFrame = Math.Min((int)sourcePosition, Math.Max(0, sourceFrames - 1));

            for (int channel = 0; channel < targetChannels; channel++)
            {
                int sourceChannel = channels switch
                {
                    0 => 0,
                    1 => 0,
                    _ => Math.Min(channel, channels - 1),
                };

                converted[frame * targetChannels + channel] = samples[sourceFrame * channels + sourceChannel];
            }
        }

        return converted;
    }

    public async Task SetVolumeAsync(float volume, CancellationToken cancellationToken = default)
    {
        float clamped = Math.Clamp(volume, 0f, 1f);
        Volume = clamped;

        // AirPlay takes the volume in dB relative to full scale.
        double decibels = clamped <= 0.0001 ? -144d : 20d * Math.Log10(clamped);
        string body = $"volume: {decibels.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture)}\r\n";
        byte[] payload = System.Text.Encoding.ASCII.GetBytes(body);

        RtspResponse response = await _client
            .SendAsync("SET_PARAMETER", "/volume", payload, "text/parameters", cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccess)
        {
            AppLog.Warn($"SET_PARAMETER volume → {response.StatusCode}");
        }
    }

    /// <summary>One RTSP feedback round trip; keeps the receiver's session alive.</summary>
    public async Task<bool> SendFeedbackAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // AirPlay 2 keepalive: POST <session>/feedback with a binary plist body
            // carrying the current RTP time. The Rust build uses the same request;
            // GET_PARAMETER is answered with 400 by a HomePod.
            byte[] body = BinaryPlist.Write(new Dictionary<string, object?>
            {
                ["streams"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = (long)RtpPayloadTypes.AudioRealtime,
                        ["rtpTime"] = (long)(_sender?.NextTimestamp ?? 0),
                    },
                },
            });

            RtspResponse response = await _client
                .SendAsync("POST", "/feedback", body, "application/x-apple-binary-plist", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccess)
            {
                AppLog.Debug($"feedback → {response.StatusCode} {response.Reason}");
            }

            return response.IsSuccess;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            AppLog.Warn($"feedback falló: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Asks the receiver for its current volume. AirPlay 2 receivers answer
    /// <c>GET_PARAMETER</c> either with a <c>text/parameters</c> body
    /// (<c>volume: -18.123456</c> in dB) or with a plist; the value is converted to
    /// the 0..1 scale the UI uses. Returns null when the receiver does not answer —
    /// callers then keep the sender-side preference.
    /// </summary>
    public async Task<float?> TryGetReceiverVolumeAsync(CancellationToken cancellationToken = default)
    {
        var attempts = new (string Uri, byte[] Body, string ContentType)[]
        {
            ("/", "volume\r\n"u8.ToArray(), "text/parameters"),
            (_client.BaseUri, "volume\r\n"u8.ToArray(), "text/parameters"),
            (_client.BaseUri, BinaryPlist.Write(new Dictionary<string, object?> { ["volume"] = true }), "application/x-apple-binary-plist"),
        };

        foreach ((string uri, byte[] body, string contentType) in attempts)
        {
            try
            {
                RtspResponse response = await _client
                    .SendAsync("GET_PARAMETER", uri, body, contentType, cancellationToken)
                    .ConfigureAwait(false);

                AppLog.Debug(
                    $"consulta de volumen ({contentType}, uri '{uri}') → {response.StatusCode}: " +
                    $"{Truncate(response.BodyText, 120)}");

                if (response.IsSuccess && TryParseVolume(response, out float volume))
                {
                    AppLog.Info($"volumen del receptor leído: {volume:0.###} ({response.BodyText.Trim()})");
                    return volume;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                AppLog.Debug($"consulta de volumen falló ({contentType}): {ex.Message}");
            }
        }

        return null;
    }

    private static bool TryParseVolume(RtspResponse response, out float volume)
    {
        volume = 0f;
        string text = response.BodyText.Trim();

        // text/parameters form: "volume: -18.123456" (dB relative to full scale).
        int separator = text.IndexOf(':');
        if (separator > 0 && text.StartsWith("volume", StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(
                text[(separator + 1)..].Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double decibels))
        {
            volume = (float)Math.Clamp(Math.Pow(10, decibels / 20.0), 0.0, 1.0);
            return true;
        }

        // plist form: { volume = 0.2 } or { volume = -18.1 } (dB).
        if (response.Body is { Length: > 0 })
        {
            try
            {
                Dictionary<string, object?>? plist = BinaryPlist.AsDictionary(BinaryPlist.Read(response.Body));
                if (plist is not null && plist.TryGetValue("volume", out object? value))
                {
                    double raw = value switch
                    {
                        double d => d,
                        long l => l,
                        _ => double.NaN,
                    };

                    if (!double.IsNaN(raw))
                    {
                        volume = raw <= 0
                            ? (float)Math.Clamp(Math.Pow(10, raw / 20.0), 0.0, 1.0)
                            : (float)Math.Clamp(raw, 0.0, 1.0);
                        return true;
                    }
                }
            }
            catch (FormatException)
            {
                // Not a plist: the text attempt above already had its chance.
            }
        }

        return false;
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length] + "…";

    private void StartPump(IPAddress receiverAddress, int controlPort, string? captureDeviceId, bool startCapture)
    {
        if (startCapture)
        {
            lock (_captureLock)
            {
                _captureRequested = true;
                _captureDeviceId = captureDeviceId;
                _appliedCaptureDeviceId = captureDeviceId;
                _capture = CreateCapture(captureDeviceId);
                _capture.Start();
            }
        }

        _pump = Task.Factory.StartNew(
            () => PumpLoop(_cts.Token),
            _cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        _heartbeat = Task.Run(() => HeartbeatLoop(_cts.Token), CancellationToken.None);
        AppLog.Info("bombeador de audio iniciado (captura → ALAC → RTP)");
    }

    private WasapiLoopbackCapture CreateCapture(string? deviceId)
    {
        var capture = new WasapiLoopbackCapture(CaptureFormat.AirPlayDefault, deviceId);
        capture.FrameCaptured += PushPcm;
        capture.CaptureInterrupted += message => ErrorOccurred?.Invoke(message);
        return capture;
    }

    /// <summary>
    /// True while the live capture takes whatever Windows is currently playing to,
    /// i.e. the caller did not pin a specific endpoint.
    /// </summary>
    public bool FollowsSystemDefaultCapture
    {
        get
        {
            lock (_captureLock)
            {
                return _captureRequested && _appliedCaptureDeviceId is null;
            }
        }
    }

    /// <summary>
    /// Rebuilds the WASAPI capture on <paramref name="deviceId"/> (null = follow the
    /// system default) without touching the RTSP session, so the receiver keeps
    /// playing out of its buffer while the new endpoint is opened.
    /// </summary>
    /// <remarks>
    /// Called when the user switches the Windows output device or picks another
    /// entry in the settings, i.e. possibly from the UI thread or from the endpoint
    /// watcher. The rebuild itself runs on a worker thread, and only the last
    /// requested endpoint survives a burst of changes.
    /// </remarks>
    public void RestartCapture(string? deviceId)
    {
        lock (_captureLock)
        {
            if (_captureClosed)
            {
                return;
            }

            _captureDeviceId = deviceId;

            if (!_captureRequested)
            {
                // No capture to rebuild (test-tone sessions): keep the choice so a
                // later stream starts from it.
                return;
            }
        }

        if (Interlocked.Exchange(ref _captureRestartPending, 1) == 1)
        {
            return;
        }

        _ = Task.Run(RebuildCapture);
    }

    private void RebuildCapture()
    {
        bool rebuilt = false;
        WasapiLoopbackCapture? previous = null;
        WasapiLoopbackCapture? replacement = null;
        string? requested = null;

        try
        {
            lock (_captureLock)
            {
                if (_captureClosed || !_captureRequested)
                {
                    return;
                }

                requested = _captureDeviceId;
                replacement = CreateCapture(requested);
                previous = _capture;
                _capture = replacement;
                _appliedCaptureDeviceId = requested;
            }

            // Outside the lock: stopping the previous capture joins its thread (up
            // to two seconds), and a caller asking for yet another endpoint must not
            // wait behind that.
            previous?.Dispose();
            replacement.Start();
            rebuilt = true;
            AppLog.Info($"captura WASAPI reconstruida: {requested ?? "salida predeterminada del sistema"}");
        }
        catch (Exception ex)
        {
            AppLog.Error("no pude reconstruir la captura WASAPI", ex);
            ErrorOccurred?.Invoke($"capture_interrupted: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _captureRestartPending, 0);

            // A request that arrived while this rebuild was running would have been
            // coalesced into _captureDeviceId, so run once more if they differ.
            if (rebuilt && NeedsCaptureRebuild())
            {
                RestartCapture(CurrentRequestedCaptureDevice());
            }
        }
    }

    private bool NeedsCaptureRebuild()
    {
        lock (_captureLock)
        {
            return !_captureClosed
                && _captureRequested
                && !string.Equals(_appliedCaptureDeviceId, _captureDeviceId, StringComparison.OrdinalIgnoreCase);
        }
    }

    private string? CurrentRequestedCaptureDevice()
    {
        lock (_captureLock)
        {
            return _captureDeviceId;
        }
    }

    private void PumpLoop(CancellationToken token)
    {
        int channels = LatencyProfile.Channels;
        int samplesPerPacket = LatencyProfile.FramesPerPacket * channels;
        var packet = new short[samplesPerPacket];
        int filled = 0;
        var stopwatch = Stopwatch.StartNew();
        long nextReport = 0;

        try
        {
            foreach (short[] chunk in _audioQueue.GetConsumingEnumerable(token))
            {
                int offset = 0;
                while (offset < chunk.Length)
                {
                    int take = Math.Min(samplesPerPacket - filled, chunk.Length - offset);
                    Array.Copy(chunk, offset, packet, filled, take);
                    filled += take;
                    offset += take;

                    if (filled < samplesPerPacket)
                    {
                        continue;
                    }

                    byte[] encoded = _encoder.EncodeFrame(packet);
                    uint packetTimestamp = _sender?.NextTimestamp ?? 0;
                    _sender?.SendFrame(encoded, LatencyProfile.FramesPerPacket);
                    long sent = Interlocked.Increment(ref _framesSent);
                    filled = 0;

                    // First packet, then about once per second of audio: tell the
                    // receiver which NTP time this RTP timestamp corresponds to.
                    // Without it the HomePod accepts the session (its LED lights
                    // up) but never starts rendering, so nothing is audible.
                    bool needsSync = sent == 1 ||
                                     _lastSyncRtpTimestamp == 0 ||
                                     packetTimestamp - _lastSyncRtpTimestamp >= (uint)LatencyProfile.SampleRate;
                    if (needsSync)
                    {
                        SendSyncPacket(packetTimestamp);
                        _lastSyncRtpTimestamp = packetTimestamp;
                    }

                    // Keep the pump paced with real time so the receiver's jitter
                    // buffer does not drift. The target is computed from the packet
                    // count (absolute schedule) rather than sleeping a fixed amount
                    // per packet, which the ~15 ms Windows timer would stretch.
                    double targetMs = sent * 1000d * LatencyProfile.FramesPerPacket / LatencyProfile.SampleRate;
                    double aheadMs = targetMs - stopwatch.Elapsed.TotalMilliseconds;
                    if (aheadMs > 1.5)
                    {
                        Thread.Sleep((int)Math.Min(aheadMs - 0.5, 50));
                    }
                }

                if (stopwatch.ElapsedMilliseconds - nextReport >= 10_000)
                {
                    nextReport = stopwatch.ElapsedMilliseconds;
                    AppLog.Info(
                        $"airplay-pump: enviados {FramesSent} paquetes, {FramesDropped} en cola, " +
                        $"control recibidos {ControlPacketsReceived} (reintentos {RetransmitRequestsAnswered}), " +
                        $"NTP recibidos {TimingRequestsReceived}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            AppLog.Error("airplay-pump: bucle terminado con error", ex);
            ErrorOccurred?.Invoke($"capture_interrupted: {ex.Message}");
        }
    }

    private async Task HeartbeatLoop(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(FeedbackInterval);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                await SendFeedbackAsync(token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Reads the RTCP control channel: logs what the receiver asks for and answers
    /// retransmission requests (payload type 85 → 86), which the receiver needs
    /// whenever a UDP audio packet is lost.
    /// </summary>
    private void StartControlLoop()
    {
        _controlLoop = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested && _controlSocket is not null)
            {
                System.Net.Sockets.UdpReceiveResult result;
                try
                {
                    result = await _controlSocket.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (System.Net.Sockets.SocketException ex)
                {
                    AppLog.Warn($"control socket: {ex.Message}");
                    continue;
                }

                long count = Interlocked.Increment(ref _controlPacketsReceived);
                byte payloadType = result.Buffer.Length > 1 ? (byte)(result.Buffer[1] & 0x7F) : (byte)0;
                if (count <= 5)
                {
                    AppLog.Info(
                        $"control ← {result.Buffer.Length} bytes, tipo {payloadType}, de {result.RemoteEndPoint}");
                }

                if (payloadType == RtpPayloadTypes.RetransmitRequest && _sender is not null)
                {
                    _sender.HandleRetransmitRequest(result.Buffer);
                    Interlocked.Increment(ref _retransmitRequestsAnswered);
                }
            }
        });
    }

    /// <summary>
    /// Sends one sync packet (payload type 84, 20 bytes) from our declared control
    /// port to the receiver's control port, mapping the RTP timestamp to the
    /// current NTP time. Layout matches the Rust sender:
    /// <code>
    /// [0]=0x90 first / 0x80 later  [1]=0xD4 (marker | PT 84)
    /// [2..4] sync sequence  [4..8] current RTP timestamp
    /// [8..16] NTP timestamp (64 bit)  [16..20] current RTP timestamp
    /// </code>
    /// </summary>
    private void SendSyncPacket(uint rtpTimestamp)
    {
        if (_controlSocket is null || _receiverControlEndPoint is null)
        {
            return;
        }

        var packet = new byte[20];
        packet[0] = Interlocked.Read(ref _syncPacketsSent) == 0 ? (byte)0x90 : (byte)0x80;
        packet[1] = 0xD4;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)Interlocked.Read(ref _syncPacketsSent));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), rtpTimestamp);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(8), NtpTimingServer.NowNtpTimestamp());
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16), rtpTimestamp);

        try
        {
            _controlSocket.Send(packet, packet.Length, _receiverControlEndPoint);
            long count = Interlocked.Increment(ref _syncPacketsSent);
            if (count <= 3)
            {
                AppLog.Debug(
                    $"sync #{count} → {_receiverControlEndPoint} rtp={rtpTimestamp} " +
                    $"ntp={NtpTimingServer.NowNtpTimestamp()}");
            }
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            AppLog.Warn($"no pude enviar el paquete sync: {ex.Message}");
        }
    }

    public async Task StopAsync()
    {
        if (_teardownSent)
        {
            return;
        }

        _teardownSent = true;

        // No capture rebuild must be scheduled after this point, otherwise a switch
        // that lands during teardown would open a new endpoint nobody reads from.
        WasapiLoopbackCapture? capture;
        lock (_captureLock)
        {
            _captureClosed = true;
            capture = _capture;
        }

        _cts.Cancel();
        _audioQueue.CompleteAdding();
        capture?.Stop();

        try
        {
            await _client.SendAsync("TEARDOWN", _client.BaseUri, cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // The receiver may already be gone.
        }

        if (_pump is not null)
        {
            try
            {
                await _pump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                AppLog.Warn("el bombeador no terminó a tiempo");
            }
        }

        AppLog.Info($"stream detenido (paquetes enviados: {FramesSent})");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _capture?.Dispose();
        _sender?.Dispose();

        try
        {
            _controlSocket?.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Already closed.
        }

        _controlSocket = null;
        _timingServer.Dispose();
        _audioQueue.Dispose();
        _cts.Dispose();
        await _client.DisposeAsync().ConfigureAwait(false);
    }

    private static uint CurrentRtpTimestamp()
    {
        // NTP style offset is what receivers expect for the initial RTP timestamp;
        // milliseconds since midnight at 44.1 kHz keeps it inside uint range.
        DateTime now = DateTime.UtcNow;
        double secondsSinceMidnight = now.TimeOfDay.TotalSeconds;
        return (uint)(secondsSinceMidnight * LatencyProfile.SampleRate) % uint.MaxValue;
    }

    private static byte[] BuildSetupPhase1(int timingPort, string deviceId)
    {
        // Same shape as the Rust SetupPhase1Request: device id, session UUID and
        // the NTP port the receiver should sync against.
        var body = new Dictionary<string, object?>
        {
            ["deviceID"] = deviceId,
            ["sessionUUID"] = Guid.NewGuid().ToString().ToUpperInvariant(),
            ["timingPort"] = (long)timingPort,
            ["timingProtocol"] = "NTP",
        };
        return BinaryPlist.Write(body);
    }

    private static byte[] BuildSetupPhase2(
        AlacEncoder encoder,
        byte[] sessionKey,
        int controlPort,
        uint latencyMs)
    {
        (uint latencyMinFrames, uint latencyMaxFrames) = LatencyProfile.ToFrames(latencyMs);

        // Field-for-field identical to the StreamDef the Rust build sends; the
        // compression type in particular has to be 2 (ALAC), otherwise the
        // receiver treats the ALAC frames as raw PCM and plays silence.
        var stream = new Dictionary<string, object?>
        {
            ["type"] = (long)RtpPayloadTypes.AudioRealtime,
            ["audioFormat"] = 0x40000L,      // ALAC
            ["audioMode"] = "default",
            ["sr"] = (long)LatencyProfile.SampleRate,
            ["ct"] = 2L,                     // compression type: ALAC
            ["controlPort"] = (long)controlPort,
            ["isMedia"] = true,
            ["latencyMin"] = (long)latencyMinFrames,
            ["latencyMax"] = (long)latencyMaxFrames,
            ["shk"] = sessionKey,
            ["asc"] = encoder.MagicCookie,
            ["spf"] = (long)LatencyProfile.FramesPerPacket,
            ["supportsDynamicStreamID"] = true,
            ["streamConnectionID"] = (long)Random.Shared.Next(1, int.MaxValue),
        };

        var body = new Dictionary<string, object?>
        {
            ["streams"] = new List<object?> { stream },
        };

        return BinaryPlist.Write(body);
    }

    private static Dictionary<string, object?>? ParsePlist(RtspResponse response)
    {
        if (response.Body is null || response.Body.Length == 0)
        {
            return null;
        }

        try
        {
            return BinaryPlist.AsDictionary(BinaryPlist.Read(response.Body));
        }
        catch (FormatException ex)
        {
            AppLog.Debug($"respuesta plist no reconocida: {ex.Message}");
            return null;
        }
    }

    /// <summary>Logs every key/value of a receiver plist response (diagnostics).</summary>
    private static void DumpPlist(string what, Dictionary<string, object?>? dictionary)
    {
        if (dictionary is null)
        {
            AppLog.Debug($"{what}: (sin cuerpo plist)");
            return;
        }

        foreach (KeyValuePair<string, object?> entry in dictionary)
        {
            string rendered = entry.Value switch
            {
                null => "null",
                byte[] bytes => $"data[{bytes.Length}] {Convert.ToHexString(bytes.AsSpan(0, Math.Min(32, bytes.Length)))}",
                List<object?> list => $"array[{list.Count}]",
                Dictionary<string, object?> nested => $"dict[{nested.Count}] {string.Join(',', nested.Keys)}",
                _ => entry.Value.ToString() ?? string.Empty,
            };
            AppLog.Debug($"  {what}: {entry.Key} = {rendered}");
        }
    }

    private static (int? DataPort, int? ControlPort) ExtractStreamPorts(RtspResponse response)
    {
        Dictionary<string, object?>? body = ParsePlist(response);
        List<object?>? streams = BinaryPlist.GetArray(body, "streams");
        if (streams is not { Count: > 0 } || streams[0] is not Dictionary<string, object?> first)
        {
            // AirPlay 1 style receivers answer with a "Transport" header.
            string? transport = response.Header("Transport");
            if (transport is not null)
            {
                foreach (string part in transport.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = part.Trim();
                    if (trimmed.StartsWith("server_port=", StringComparison.OrdinalIgnoreCase))
                    {
                        return (int.TryParse(trimmed["server_port=".Length..], out int port) ? port : null, null);
                    }
                }
            }

            return (null, null);
        }

        return (
            (int?)BinaryPlist.GetInteger(first, "dataPort"),
            (int?)BinaryPlist.GetInteger(first, "controlPort"));
    }

    private static void LogDeviceInfo(RtspResponse response)
    {
        Dictionary<string, object?>? info = ParsePlist(response);
        if (info is null)
        {
            AppLog.Debug($"GET /info → {response.StatusCode} (sin cuerpo plist)");
            return;
        }

        string model = BinaryPlist.GetString(info, "model") ?? "?";
        string source = BinaryPlist.GetString(info, "srcvers") ?? "?";
        AppLog.Info($"dispositivo: model={model} srcvers={source} status={response.StatusCode}");
    }

    private static int ReserveUdpPort(out System.Net.Sockets.UdpClient socket)
    {
        socket = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Any, 0));
        int port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        return port;
    }
}
