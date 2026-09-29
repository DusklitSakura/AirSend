using System.Runtime.InteropServices;
using AirSend.Core.Audio;
using AirSend.Core.Logging;

namespace AirSend.Core.Capture;

public sealed class AudioCaptureException(string message) : Exception(message);

/// <summary>
/// System audio capture through WASAPI loopback on the default render device.
/// Port of <c>crates/audio-capture/src/windows.rs</c> (which uses the <c>wasapi</c>
/// crate): the client asks for 44.1 kHz / 16 bit / stereo and lets the audio engine
/// convert with <c>AUTOCONVERTPCM</c>, so no resampler is needed here either.
/// </summary>
public sealed class WasapiLoopbackCapture : IDisposable
{
    // AUDCLNT_STREAMFLAGS_*
    private const uint Loopback = 0x00020000;
    private const uint SrcDefaultQuality = 0x08000000;
    private const uint AutoConvertPcm = 0x80000000;

    private const int ClsctxAll = 23;
    private const ushort WaveFormatPcm = 1;

    private readonly CaptureFormat _format;
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private IAudioClient? _audioClient;

    private readonly string? _deviceId;

    public WasapiLoopbackCapture(CaptureFormat format = default, string? deviceId = null)
    {
        _format = format.SampleRate == 0 ? CaptureFormat.AirPlayDefault : format;
        _deviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
    }

    public event Action<CapturedFrame>? FrameCaptured;

    /// <summary>Raised when the capture stops without the caller asking for it.</summary>
    public event Action<string>? CaptureInterrupted;

    public string DeviceName { get; private set; } = "default render device";

    public bool IsRunning => _thread is { IsAlive: true };

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        _thread = new Thread(() => Run(token))
        {
            Name = "airsend-capture",
            IsBackground = true,
            // The upstream Rust build raises the pumping thread to
            // THREAD_PRIORITY_TIME_CRITICAL plus MMCSS "Pro Audio" to avoid the
            // periodic scheduler hiccups that are audible as ticks.
            Priority = ThreadPriority.Highest,
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void Stop()
    {
        _cts?.Cancel();
        try
        {
            _audioClient?.Stop();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // Already stopped.
        }

        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    private void Run(CancellationToken token)
    {
        Exception? failure = null;
        try
        {
            CaptureLoop(token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failure = ex;
            AppLog.Error("captura WASAPI: bucle terminado con error", ex);
        }

        if (!token.IsCancellationRequested)
        {
            CaptureInterrupted?.Invoke(failure?.Message ?? "capture_interrupted");
        }
    }

    private void CaptureLoop(CancellationToken token)
    {
        object enumeratorObject = CreateDeviceEnumerator();
        var enumerator = (IMMDeviceEnumerator)enumeratorObject;

        try
        {
            // Default endpoint: capture whatever Windows is playing. Explicit
            // endpoint: capture e.g. a virtual cable the user selected as output.
            IMMDevice device;
            int openResult = _deviceId is null
                ? enumerator.GetDefaultAudioEndpoint(0 /*eRender*/, 0 /*eConsole*/, out device)
                : enumerator.GetDevice(_deviceId, out device);
            Marshal.ThrowExceptionForHR(openResult);

            string? deviceId = GetDeviceId(device);
            DeviceName = deviceId ?? "default render device";

            object audioClientObject = Activate(device, typeof(IAudioClient).GUID);
            var audioClient = (IAudioClient)audioClientObject;
            _audioClient = audioClient;

            IntPtr formatPtr = BuildWaveFormat();
            try
            {
                int hr = audioClient.Initialize(
                    0 /*AUDCLNT_SHAREMODE_SHARED*/,
                    Loopback | AutoConvertPcm | SrcDefaultQuality,
                    0,
                    0,
                    formatPtr,
                    IntPtr.Zero);
                Marshal.ThrowExceptionForHR(hr);
            }
            finally
            {
                Marshal.FreeCoTaskMem(formatPtr);
            }

            object captureClientObject = GetService(audioClient, typeof(IAudioCaptureClient).GUID);
            var captureClient = (IAudioCaptureClient)captureClientObject;

            Marshal.ThrowExceptionForHR(audioClient.Start());
            AppLog.Info($"captura WASAPI iniciada: {DeviceName} ({_format})");

            int bytesPerFrame = _format.Channels * 2;

            while (!token.IsCancellationRequested)
            {
                int hr = captureClient.GetNextPacketSize(out uint framesAvailable);
                if (hr < 0)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                if (framesAvailable == 0)
                {
                    Thread.Sleep(4);
                    continue;
                }

                Marshal.ThrowExceptionForHR(captureClient.GetBuffer(
                    out IntPtr data,
                    out uint frames,
                    out uint flags,
                    out ulong devicePosition,
                    out ulong qpcPosition));

                try
                {
                    if (frames > 0 && data != IntPtr.Zero)
                    {
                        bool silent = (flags & 0x2) != 0; // AUDCLNT_BUFFERFLAGS_SILENT
                        int sampleCount = (int)(frames * _format.Channels);
                        var samples = new short[sampleCount];
                        if (!silent)
                        {
                            Marshal.Copy(data, samples, 0, sampleCount);
                        }

                        FrameCaptured?.Invoke(new CapturedFrame(samples, _format.Channels, _format.SampleRate));
                    }
                }
                finally
                {
                    Marshal.ThrowExceptionForHR(captureClient.ReleaseBuffer(frames));
                }
            }
        }
        finally
        {
            try
            {
                _audioClient?.Stop();
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                // Ignore teardown races.
            }

            if (enumeratorObject is not null && Marshal.IsComObject(enumeratorObject))
            {
                Marshal.ReleaseComObject(enumeratorObject);
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _cts = null;
    }

    private static object CreateDeviceEnumerator()
    {
        Type type = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))
            ?? throw new AudioCaptureException("MMDeviceEnumerator is not registered");
        return Activator.CreateInstance(type)
            ?? throw new AudioCaptureException("could not create MMDeviceEnumerator");
    }

    private static object Activate(IMMDevice device, Guid interfaceId)
    {
        int hr = device.Activate(ref interfaceId, ClsctxAll, IntPtr.Zero, out object instance);
        Marshal.ThrowExceptionForHR(hr);
        return instance;
    }

    private static object GetService(IAudioClient client, Guid interfaceId)
    {
        int hr = client.GetService(ref interfaceId, out object service);
        Marshal.ThrowExceptionForHR(hr);
        return service;
    }

    private static string? GetDeviceId(IMMDevice device)
    {
        int hr = device.GetId(out IntPtr pointer);
        if (hr != 0 || pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    private IntPtr BuildWaveFormat()
    {
        var format = new WaveFormatEx
        {
            FormatTag = WaveFormatPcm,
            Channels = (ushort)_format.Channels,
            SamplesPerSecond = (uint)_format.SampleRate,
            BitsPerSample = 16,
            BlockAlign = (ushort)(_format.Channels * 2),
            AverageBytesPerSecond = (uint)(_format.SampleRate * _format.Channels * 2),
            ExtraSize = 0,
        };

        IntPtr pointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WaveFormatEx>());
        Marshal.StructureToPtr(format, pointer, false);
        return pointer;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid interfaceId, int clsContext, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);

        [PreserveSig]
        int OpenPropertyStore(int access, out IntPtr properties);

        [PreserveSig]
        int GetId(out IntPtr id);

        [PreserveSig]
        int GetState(out int state);
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig]
        int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);

        [PreserveSig]
        int GetBufferSize(out uint bufferFrames);

        [PreserveSig]
        int GetStreamLatency(out long latency);

        [PreserveSig]
        int GetCurrentPadding(out uint padding);

        [PreserveSig]
        int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);

        [PreserveSig]
        int GetMixFormat(out IntPtr format);

        [PreserveSig]
        int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

        [PreserveSig]
        int Start();

        [PreserveSig]
        int Stop();

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int SetEventHandle(IntPtr eventHandle);

        [PreserveSig]
        int GetService(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig]
        int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);

        [PreserveSig]
        int ReleaseBuffer(uint frames);

        [PreserveSig]
        int GetNextPacketSize(out uint frames);
    }
}
