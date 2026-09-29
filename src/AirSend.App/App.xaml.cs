using AirSend.Core.Logging;
using AirSend.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace AirSend;

public partial class App : Application
{
    private TrayIcon? _tray;
    private bool _quitting;

    public App()
    {
        try
        {
            AppLog.Initialize();
            AppLog.Info("AirSend (WinUI 3) iniciando");

            UnhandledException += OnUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                WriteStartupError("AppDomain", args.ExceptionObject as Exception);

            InitializeComponent();

            Settings = new SettingsStore();
            Localization = new Localization(Settings);
            Coordinator = new AirPlayCoordinator(Settings);

            AppLog.Info(
                $"idioma de la interfaz: {Localization.Tag} " +
                $"(sistema: {System.Globalization.CultureInfo.CurrentUICulture.Name})");
        }
        catch (Exception ex)
        {
            WriteStartupError("App ctor", ex);
            throw;
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        AppLog.Error("excepción no controlada", e.Exception);
        WriteStartupError("XAML", e.Exception);
    }

    /// <summary>
    /// Some startup failures happen before the log directory is usable, so the
    /// last resort is a file right next to the executable (always writable in the
    /// zip layout).
    /// </summary>
    private static void WriteStartupError(string stage, Exception? exception)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "startup-error.log");
            var report = new System.Text.StringBuilder()
                .Append('[').Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("] ").Append(stage).AppendLine();

            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                report.Append("  ").Append(current.GetType().FullName).Append(": ").AppendLine(current.Message);
                report.AppendLine(current.StackTrace);
            }

            report.AppendLine();
            File.AppendAllText(path, report.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"startup error ({stage}): {exception}");
        }
    }

    public static Window Window { get; private set; } = null!;

    public static DispatcherQueue DispatcherQueue { get; private set; } = null!;

    public static SettingsStore Settings { get; private set; } = null!;

    public static Localization Localization { get; private set; } = null!;

    public static AirPlayCoordinator Coordinator { get; private set; } = null!;

    public static nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(Window);

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Single instance: a second launch just wakes up the running window. Two
        // copies would fight over the same HomePod session.
        if (!SingleInstance.TryAcquire())
        {
            SingleInstance.SignalPrimaryInstance();
            Exit();
            return;
        }

        Window = new MainWindow();
        DispatcherQueue = DispatcherQueue.GetForCurrentThread();
        Window.Activate();

        // Launched by the Run key: stay in the tray, auto-connect does the rest.
        if (Environment.GetCommandLineArgs().Any(
                argument => argument.Equals("--minimized", StringComparison.OrdinalIgnoreCase)))
        {
            AppLog.Info("inicio automático: la ventana queda en la bandeja");
            if (Window is MainWindow mainWindow)
            {
                mainWindow.Hide();
            }
        }

        // The listener runs on its own thread; window APIs must go through the UI thread.
        SingleInstance.StartListening(() => DispatcherQueue.TryEnqueue(ShowMainWindow));
        SetUpTray();
    }

    /// <summary>Closes the window but keeps streaming (close to tray), like the Rust app.</summary>
    public static void HideMainWindow()
    {
        if (Window is MainWindow main)
        {
            main.Hide();
        }
    }

    public static void ShowMainWindow()
    {
        if (Window is MainWindow main)
        {
            main.Show();
        }
    }

    public static void ToggleMainWindow()
    {
        if (Window is MainWindow main)
        {
            main.ToggleVisibility();
        }
    }

    public void QuitApplication()
    {
        _quitting = true;
        _tray?.Dispose();
        _tray = null;
        Exit();
    }

    private void SetUpTray()
    {
        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            _tray = new TrayIcon(iconPath);
            _tray.ShowHideRequested += ToggleMainWindow;
            _tray.QuitRequested += QuitApplication;
            UpdateTrayLabels();
            Localization.LanguageChanged += UpdateTrayLabels;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"bandeja del sistema no disponible: {ex.Message}");
        }
    }

    private void UpdateTrayLabels()
    {
        _tray?.UpdateLabels(
            Localization.T("app_name"),
            Localization.T("tray_show"),
            Localization.T("tray_quit"));
    }

    internal bool IsQuitting => _quitting;

    /// <summary>
    /// True when the tray icon is live. Without it, hiding the window would leave
    /// the user with no way back, so closing exits instead.
    /// </summary>
    internal bool HasTrayIcon => _tray is { IsAvailable: true };
}
