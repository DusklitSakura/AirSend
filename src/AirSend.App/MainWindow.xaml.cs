using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace AirSend;

/// <summary>
/// Main window: a compact single-column utility, sized from the content rubric
/// (device list + player card + manual entry). The upstream Tauri window is
/// 420x540; this port keeps the same width and grows in height for the extra
/// player, manual and log sections.
/// </summary>
public sealed partial class MainWindow : Window
{
    // Vertical navigation pane (176 DIP) plus a content column wide enough for the
    // SettingsCard rows to keep their control on the right instead of wrapping.
    private const int WidthDip = 880;
    private const int HeightDip = 800;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        // Absolute path: the working directory of an unpackaged app is not
        // guaranteed to be the application folder.
        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        AppWindow.SetIcon(File.Exists(icon) ? icon : "Assets/AppIcon.ico");

        ResizeForDpi();

        // Closing hides the window instead of stopping the audio pump.
        AppWindow.Closing += OnClosing;

        RootFrame.Navigate(typeof(MainPage));
    }

    public void ToggleVisibility()
    {
        if (AppWindow.IsVisible)
        {
            Hide();
        }
        else
        {
            Show();
        }
    }

    public void Hide() => AppWindow.Hide();

    public void Show()
    {
        AppWindow.Show();
        Activate();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (Application.Current is App app && !app.IsQuitting && app.HasTrayIcon)
        {
            // Close to tray: the stream keeps playing in the background.
            args.Cancel = true;
            AppWindow.Hide();
            return;
        }

        // No tray icon (locked down shell, sandbox): closing the window has to end
        // the process, otherwise the window could never be reopened.
        (Application.Current as App)?.QuitApplication();
    }

    private void ResizeForDpi()
    {
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = GetDpiForWindow(hwnd) / 96.0;

        // AppWindow.Resize takes physical pixels, not DIPs.
        AppWindow.Resize(new SizeInt32((int)(WidthDip * scale), (int)(HeightDip * scale)));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
