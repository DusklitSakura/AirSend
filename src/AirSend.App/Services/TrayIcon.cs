using System.Runtime.InteropServices;
using AirSend.Core.Logging;

namespace AirSend.Services;

/// <summary>
/// System tray icon with a two item menu ("show/hide", "quit") and left click to
/// toggle the window.
/// </summary>
/// <remarks>
/// Counterpart of <c>setup_tray()</c> in <c>src-tauri/src/lib.rs</c>, implemented
/// with <c>Shell_NotifyIcon</c> because WinUI 3 has no built-in tray control.
/// The icon lives on a message-only window so the main window keeps its own
/// message handling untouched.
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    private const int WmApp = 0x8000;
    private const int WmTrayCallback = WmApp + 1;
    private const int WmCommand = 0x0111;
    private const int WmDestroy = 0x0002;
    private const int WmLeftButtonUp = 0x0202;
    private const int WmRightButtonUp = 0x0205;
    private const int WmRButtonUp = WmRightButtonUp;

    private const int NimAdd = 0x00000000;
    private const int NimModify = 0x00000001;
    private const int NimDelete = 0x00000002;
    private const int NifMessage = 0x00000001;
    private const int NifIcon = 0x00000002;
    private const int NifTip = 0x00000004;

    private const int MfString = 0x00000000;
    private const int MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x00000010;
    private const uint LrDefaultSize = 0x00000040;

    private const int ShowCommandId = 1001;
    private const int QuitCommandId = 1002;

    private readonly WndProc _wndProc;
    private readonly IntPtr _hwnd;
    private readonly IntPtr _previousProc;
    private IntPtr _icon;
    private bool _added;
    private string _showLabel = "Mostrar / ocultar ventana";
    private string _quitLabel = "Salir";
    private string _tooltip = "AirSend";

    public TrayIcon(string iconPath)
    {
        _wndProc = WindowProcedure;

        IntPtr hInstance = GetModuleHandle(null);

        // Use a class USER32 already knows ("STATIC") and subclass it: registering
        // our own WNDCLASSEX is fragile (marshalling differences across Windows
        // builds gave "invalid parameter"), while subclassing only needs the
        // window handle we create below.
        _hwnd = CreateWindowEx(
            0,
            "STATIC",
            "AirSendTray",
            0,
            0,
            0,
            0,
            0,
            HwndMessage,
            IntPtr.Zero,
            hInstance,
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException($"could not create the tray window (error {Marshal.GetLastWin32Error()})");
        }

        _previousProc = SetWindowLongPtr(
            _hwnd,
            GwlpWndProc,
            Marshal.GetFunctionPointerForDelegate(_wndProc));

        _icon = File.Exists(iconPath)
            ? LoadImage(IntPtr.Zero, iconPath, ImageIcon, 0, 0, LrLoadFromFile | LrDefaultSize)
            : LoadIcon(IntPtr.Zero, 32512);

        AddIcon();
    }

    /// <summary>False when the shell refuses the icon (no Explorer, sandbox, ...).</summary>
    public bool IsAvailable => _added;

    /// <summary>Raised on left click and on the "show/hide" menu entry.</summary>
    public event Action? ShowHideRequested;

    /// <summary>Raised on the "quit" menu entry; the app exits for real.</summary>
    public event Action? QuitRequested;

    public void UpdateLabels(string tooltip, string showLabel, string quitLabel)
    {
        _tooltip = tooltip;
        _showLabel = showLabel;
        _quitLabel = quitLabel;
        ModifyIcon();
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = CreateData();
            Shell_NotifyIcon(NimDelete, ref data);
            _added = false;
        }

        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
        }
    }

    private void AddIcon()
    {
        NotifyIconData data = CreateData();
        _added = Shell_NotifyIcon(NimAdd, ref data);
        if (!_added)
        {
                AppLog.Warn("log.tray.add_failed", new { code = Marshal.GetLastWin32Error() });
        }
    }

    private void ModifyIcon()
    {
        if (!_added)
        {
            return;
        }

        NotifyIconData data = CreateData();
        Shell_NotifyIcon(NimModify, ref data);
    }

    private NotifyIconData CreateData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = _hwnd,
        Id = 1,
        Flags = NifMessage | NifIcon | NifTip,
        CallbackMessage = WmTrayCallback,
        Icon = _icon,
        Tip = _tooltip.Length > 127 ? _tooltip[..127] : _tooltip,
    };

    private IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch ((int)message)
        {
            case WmTrayCallback:
            {
                int notification = (int)(lParam.ToInt64() & 0xFFFF);
                if (notification == WmLeftButtonUp)
                {
                    ShowHideRequested?.Invoke();
                }
                else if (notification == WmRButtonUp)
                {
                    ShowMenu();
                }

                return IntPtr.Zero;
            }

            case WmCommand:
            {
                int command = (int)(wParam.ToInt64() & 0xFFFF);
                if (command == ShowCommandId)
                {
                    ShowHideRequested?.Invoke();
                }
                else if (command == QuitCommandId)
                {
                    QuitRequested?.Invoke();
                }

                return IntPtr.Zero;
            }

            case WmDestroy:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return _previousProc != IntPtr.Zero
            ? CallWindowProc(_previousProc, hwnd, message, wParam, lParam)
            : DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            AppendMenu(menu, MfString, ShowCommandId, _showLabel);
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, QuitCommandId, _quitLabel);

            GetCursorPos(out Point cursor);
            SetForegroundWindow(_hwnd);
            int command = TrackPopupMenu(
                menu,
                TpmRightButton | TpmReturnCmd,
                cursor.X,
                cursor.Y,
                0,
                _hwnd,
                IntPtr.Zero);

            if (command == ShowCommandId)
            {
                ShowHideRequested?.Invoke();
            }
            else if (command == QuitCommandId)
            {
                QuitRequested?.Invoke();
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid ItemGuid;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    private static readonly IntPtr HwndMessage = new(-3);
    private const int GwlpWndProc = -4;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint exStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr newValue);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, int id, string? text);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, int name);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
