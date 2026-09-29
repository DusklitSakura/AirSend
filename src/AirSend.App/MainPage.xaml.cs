using AirSend.ViewModels;
using AirSend.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AirSend;

public sealed partial class MainPage : Page
{
    private readonly DevicesView _devicesView;
    private readonly PlaybackView _playbackView;
    private readonly SettingsView _settingsView;

    public MainPage()
    {
        ViewModel = new MainViewModel(App.Coordinator, App.Localization);
        ViewModel.ConfirmAsync = ShowConfirmationAsync;
        ViewModel.SectionRequested += SelectSection;

        InitializeComponent();

        // The three sections share one view model instance, so state (devices,
        // playback, settings) stays consistent while switching tabs.
        _devicesView = new DevicesView(ViewModel);
        _playbackView = new PlaybackView(ViewModel);
        _settingsView = new SettingsView(ViewModel);

        ContentHost.Children.Add(_devicesView);
        ContentHost.Children.Add(_playbackView);
        ContentHost.Children.Add(_settingsView);
        ShowSection("devices");

        Loaded += OnLoaded;
    }

    public MainViewModel ViewModel { get; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        ViewModel.Attach();
        ViewModel.LoadCaptureSources();
        await ViewModel.BootstrapAsync();
    }

    private void OnSectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // The settings entry is the NavigationView's built-in item.
        if (args.IsSettingsSelected)
        {
            ShowSection("settings");
            return;
        }

        if (args.SelectedItem is NavigationViewItem { Tag: string tag })
        {
            ShowSection(tag);
        }
    }

    private void ShowSection(string tag)
    {
        _devicesView.Visibility = tag == "devices" ? Visibility.Visible : Visibility.Collapsed;
        _playbackView.Visibility = tag == "playback" ? Visibility.Visible : Visibility.Collapsed;
        _settingsView.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;

        // A focused control in a hidden view can leave the section scrolled.
        switch (tag)
        {
            case "devices":
                _devicesView.ScrollToTop();
                break;
            case "playback":
                _playbackView.ScrollToTop();
                break;
            default:
                _settingsView.ScrollToTop();
                break;
        }
    }

    /// <summary>Moves the navigation pane to another section (used by in-page buttons).</summary>
    private void SelectSection(string tag)
    {
        if (tag == "settings")
        {
            Sections.SelectedItem = Sections.SettingsItem;
            return;
        }

        foreach (object item in Sections.MenuItems)
        {
            if (item is NavigationViewItem { Tag: string itemTag } && itemTag == tag)
            {
                Sections.SelectedItem = item;
                return;
            }
        }
    }

    /// <summary>
    /// Modal confirmation used before switching devices or dropping multi-device
    /// playback. The Tauri build uses a <c>&lt;dialog&gt;</c>; WinUI uses ContentDialog.
    /// </summary>
    private async Task<ConfirmationResult> ShowConfirmationAsync(
        string title,
        string message,
        string acceptLabel,
        string cancelLabel,
        string? dontAskLabel)
    {
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });

        var dontAsk = new CheckBox
        {
            Content = dontAskLabel,
            Visibility = dontAskLabel is null ? Visibility.Collapsed : Visibility.Visible,
        };
        if (dontAskLabel is not null)
        {
            content.Children.Add(dontAsk);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = acceptLabel,
            CloseButtonText = cancelLabel,
            DefaultButton = ContentDialogButton.Primary,
        };

        ContentDialogResult result = await dialog.ShowAsync();
        return new ConfirmationResult(
            result == ContentDialogResult.Primary,
            dontAskLabel is not null && dontAsk.IsChecked == true);
    }
}
