using AirSend.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AirSend.Views;

public sealed partial class DevicesView : UserControl
{
    public DevicesView(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public MainViewModel ViewModel { get; }

    /// <summary>Focus can scroll a hidden view; reset it when the tab is shown.</summary>
    public void ScrollToTop() => DevicesScroller.ChangeView(null, 0, null, disableAnimation: true);

    private void OnAddManualClick(object sender, RoutedEventArgs e) =>
        ViewModel.AddManualDeviceCommand.Execute(null);

    private async void OnDeviceButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DeviceViewModel device })
        {
            await ViewModel.ToggleDeviceCommand.ExecuteAsync(device);
        }
    }
}
