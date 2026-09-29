using System.Diagnostics;
using AirSend.Core.Logging;
using AirSend.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AirSend.Views;

public sealed partial class SettingsView : UserControl
{
    public SettingsView(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public MainViewModel ViewModel { get; }

    /// <summary>Focus can scroll a hidden view; reset it when the tab is shown.</summary>
    public void ScrollToTop() => SettingsScroller.ChangeView(null, 0, null, disableAnimation: true);

    private void OnOpenLogsClick(object sender, RoutedEventArgs e) =>
        OpenFolder(AppLog.LogDirectory ?? AppPaths.DataDirectory());

    private void OnOpenDataClick(object sender, RoutedEventArgs e) => OpenFolder(AppPaths.DataDirectory());

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            AppLog.Warn($"no pude abrir {path}: {ex.Message}");
        }
    }
}
