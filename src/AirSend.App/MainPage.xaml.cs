using AirSend.Core;
using AirSend.Core.Logging;
using AirSend.Core.Updates;
using AirSend.Services;
using AirSend.ViewModels;
using AirSend.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

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
        ViewModel.UpdateAvailable += release => _ = ShowUpdateDialogAsync(release);
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

    /// <summary>
    /// Offers a newer release. The notes are shown in the dialog, nothing is
    /// installed unless the user presses the primary button, and the dialog stays
    /// open (with a progress bar) while the package is downloaded and unpacked.
    /// Once it is staged the app exits: the files of a running process cannot be
    /// replaced, so a helper script does the swap and starts AirSend again.
    /// </summary>
    private async Task ShowUpdateDialogAsync(UpdateRelease release)
    {
        string scriptPath = string.Empty;

        var notes = new TextBlock
        {
            Text = UpdateNotes.ToPlainText(release.Notes),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
        };

        if (string.IsNullOrWhiteSpace(notes.Text))
        {
            notes.Text = release.Title;
        }

        var progress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 1,
            Visibility = Visibility.Collapsed,
        };

        var status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = App.Localization.T("update_notes_heading"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        content.Children.Add(new ScrollViewer { MaxHeight = 260, Content = notes });
        content.Children.Add(new TextBlock
        {
            Text = App.Localization.T("update_current", new { version = App.Updates.CurrentVersion }),
            Opacity = 0.7,
        });
        // Which package will be fetched, so a wrong architecture or flavour would be
        // visible before anything is downloaded.
        content.Children.Add(new TextBlock
        {
            Text = ViewModel.DescribeInstalledBuild(),
            Opacity = 0.7,
        });
        content.Children.Add(new HyperlinkButton
        {
            Content = App.Localization.T("update_open_page"),
            NavigateUri = new Uri(release.PageUrl),
        });
        content.Children.Add(progress);
        content.Children.Add(status);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = App.Localization.T("update_available_title", new { version = release.Version }),
            Content = content,
            PrimaryButtonText = App.Localization.T("update_install"),
            CloseButtonText = App.Localization.T("update_later"),
            DefaultButton = ContentDialogButton.Primary,
        };

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            // Keep the dialog open: cancelling the click lets us report progress and
            // show the error text if the download or the extraction fails.
            args.Cancel = true;
            dialog.IsPrimaryButtonEnabled = false;
            progress.Visibility = Visibility.Visible;
            status.Visibility = Visibility.Visible;

            var reporter = new Progress<UpdateProgress>(update =>
            {
                switch (update.Phase)
                {
                    case UpdatePhase.Downloading:
                        progress.IsIndeterminate = false;
                        progress.Value = Math.Clamp(update.Fraction, 0d, 1d);
                        status.Text = App.Localization.T(
                            "update_downloading",
                            new
                            {
                                name = update.PackageName ?? string.Empty,
                                percent = (int)Math.Round(update.Fraction * 100),
                            });
                        break;
                    case UpdatePhase.Extracting:
                        progress.IsIndeterminate = true;
                        status.Text = App.Localization.T("update_extracting");
                        break;
                    default:
                        progress.IsIndeterminate = true;
                        status.Text = App.Localization.T("update_preparing");
                        break;
                }
            });

            UpdatePreparation preparation = await ViewModel.PrepareUpdateAsync(release, reporter);

            if (preparation.ScriptPath is null)
            {
                progress.Visibility = Visibility.Collapsed;
                status.Text = preparation.Error ?? string.Empty;
                dialog.IsPrimaryButtonEnabled = true;
                return;
            }

            scriptPath = preparation.ScriptPath;
            dialog.Hide();
        };

        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            // Another ContentDialog may already be open (welcome prompts, volume
            // warning): the next check will offer the update again.
            AppLog.Warn("log.update.dialog_failed", new { err = AirSendError.Describe(ex) });
            return;
        }

        if (scriptPath.Length == 0)
        {
            return;
        }

        ViewModel.ApplyUpdate(scriptPath);
        (Application.Current as App)?.QuitApplication();
    }
}
