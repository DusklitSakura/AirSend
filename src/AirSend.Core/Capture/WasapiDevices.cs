using System.Runtime.InteropServices;
using AirSend.Core.Logging;

namespace AirSend.Core.Capture;

/// <summary>
/// One entry of the playback endpoint list. <paramref name="IsDefault"/> marks the
/// endpoint Windows currently plays to; the UI decides how to label it so the
/// marker can be localized.
/// </summary>
public sealed record AudioRenderDevice(string Id, string Name, bool IsDefault);

/// <summary>
/// Enumerates the active playback endpoints so the user can pick which one to
/// capture with WASAPI loopback (for example a virtual cable that Windows shows
/// in the speaker list).
/// </summary>
public static class WasapiDevices
{
    private const int ClsctxAll = 23;
    private const int StgmRead = 0;
    private const int DeviceStateActive = 0x00000001;
    private const int DataFlowRender = 0;
    private const int RoleConsole = 0;

    public static IReadOnlyList<AudioRenderDevice> EnumerateRenderDevices()
    {
        var devices = new List<AudioRenderDevice>();

        try
        {
            Type type = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))
                ?? throw new InvalidOperationException("MMDeviceEnumerator not registered");
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(type)!;

            string? defaultId = null;
            if (enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleConsole, out IMMDevice defaultDevice) == 0)
            {
                defaultId = GetId(defaultDevice);
                Marshal.ReleaseComObject(defaultDevice);
            }

            if (enumerator.EnumAudioEndpoints(DataFlowRender, DeviceStateActive, out IMMDeviceCollection collection) != 0)
            {
                return devices;
            }

            try
            {
                if (collection.GetCount(out uint count) != 0)
                {
                    return devices;
                }

                for (uint index = 0; index < count; index++)
                {
                    if (collection.Item(index, out IMMDevice device) != 0)
                    {
                        continue;
                    }

                    try
                    {
                        string? id = GetId(device);
                        string name = GetFriendlyName(device) ?? id ?? $"Output {index}";
                        if (id is not null)
                        {
                            devices.Add(new AudioRenderDevice(
                                id,
                                name,
                                string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)));
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(device);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(collection);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            AppLog.Warn($"no pude enumerar los dispositivos de audio: {ex.Message}");
        }

        return devices;
    }

    /// <summary>
    /// Endpoint id Windows is currently using for system sounds, or null when the
    /// machine has no active playback device.
    /// </summary>
    public static string? GetDefaultRenderDeviceId()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;

        try
        {
            Type type = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))
                ?? throw new InvalidOperationException("MMDeviceEnumerator not registered");
            enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(type)!;

            if (enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleConsole, out IMMDevice resolved) != 0)
            {
                return null;
            }

            device = resolved;
            return GetId(resolved);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException or NotSupportedException)
        {
            AppLog.Warn($"no pude leer la salida predeterminada: {ex.Message}");
            return null;
        }
        finally
        {
            if (device is not null)
            {
                Marshal.ReleaseComObject(device);
            }

            if (enumerator is not null)
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
    }

    private static string? GetId(IMMDevice device)
    {
        if (device.GetId(out IntPtr pointer) != 0 || pointer == IntPtr.Zero)
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

    private static string? GetFriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(StgmRead, out IPropertyStore store) != 0)
        {
            return null;
        }

        try
        {
            // PKEY_Device_FriendlyName {a45c254e-df1c-4efd-8020-67d146a850e0}, 14
            var key = new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
            if (store.GetValue(ref key, out PropVariant value) != 0)
            {
                return null;
            }

            try
            {
                return value.GetString();
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid formatId, int propertyId)
    {
        public Guid FormatId = formatId;
        public int PropertyId = propertyId;
    }

    /// <summary>Minimal PROPVARIANT: enough for LPWSTR values (vt = 31).</summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VarType;
        [FieldOffset(8)] public IntPtr PointerValue;

        public string? GetString() =>
            VarType == 31 && PointerValue != IntPtr.Zero ? Marshal.PtrToStringUni(PointerValue) : null;
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);

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
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int Item(uint index, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid interfaceId, int clsContext, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);

        [PreserveSig]
        int OpenPropertyStore(int access, out IPropertyStore properties);

        [PreserveSig]
        int GetId(out IntPtr id);

        [PreserveSig]
        int GetState(out int state);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint index, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int Commit();
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant variant);
}
