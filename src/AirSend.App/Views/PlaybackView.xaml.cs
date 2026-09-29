using AirSend.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace AirSend.Views;

public sealed partial class PlaybackView : UserControl
{
    public PlaybackView(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public MainViewModel ViewModel { get; }

    /// <summary>Focus can scroll a hidden view; reset it when the tab is shown.</summary>
    public void ScrollToTop() => PlaybackScroller.ChangeView(null, 0, null, disableAnimation: true);
}
