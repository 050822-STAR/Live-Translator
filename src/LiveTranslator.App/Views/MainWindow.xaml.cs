using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using LiveTranslator.App.Services;
using LiveTranslator.App.ViewModels;
using LiveTranslator.Core.Models;

namespace LiveTranslator.App.Views;

public partial class MainWindow : Window
{
    private readonly AppHost _host;
    private OverlayWindow? _overlay;
    private bool _autoScroll = true;

    public MainWindow(AppHost host)
    {
        InitializeComponent();
        _host = host;
        DataContext = host.ViewModel;

        WindowBounds.Restore(this, host.Settings.Display.MainBounds);
        Topmost = host.Settings.Display.Topmost;
        TopmostButton.IsChecked = Topmost;
        ClickThroughItem.IsChecked = host.Settings.Overlay.ClickThrough;

        host.ViewModel.PropertyChanged += OnViewModelChanged;
        Loaded += (_, _) =>
        {
            if (host.Settings.Overlay.Enabled)
                ShowOverlay(true);
        };
        Closing += OnClosing;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        var vm = (MainViewModel)sender!;
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsRunning):
                RunGlyph.Text = vm.IsRunning ? "" : "";
                RunButton.ToolTip = vm.IsRunning ? "暂停" : "开始";
                UpdateStatusDot(vm);
                break;
            case nameof(MainViewModel.StatusIsError):
                StatusText.Foreground = (Brush)FindResource(vm.StatusIsError ? "Danger" : "FgMuted");
                UpdateStatusDot(vm);
                break;
        }
    }

    private void UpdateStatusDot(MainViewModel vm) =>
        StatusDot.Fill = (Brush)FindResource(!vm.IsRunning ? "FgMuted" : vm.StatusIsError ? "Danger" : "Ok");

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_host.ViewModel.IsRunning)
            _host.Pause();
        else
            _host.Start();
    }

    // Checked/Unchecked (not Click) so UI Automation toggles from assistive tools work too.
    private void Overlay_Changed(object sender, RoutedEventArgs e)
    {
        var want = OverlayButton.IsChecked == true;
        if (want != (_overlay is not null))
            ShowOverlay(want);
    }

    private void ShowOverlay(bool show)
    {
        if (show)
        {
            if (_overlay is null)
            {
                _overlay = new OverlayWindow(_host);
                _overlay.Closed += (_, _) =>
                {
                    _overlay = null;
                    OverlayButton.IsChecked = false;
                    if (IsLoaded)
                    {
                        _host.Settings.Overlay.Enabled = false;
                        _host.Save();
                    }
                };
                _overlay.Show();
            }
            _overlay.Activate();
        }
        else
        {
            _overlay?.Close();
        }
        OverlayButton.IsChecked = show;
        _host.Settings.Overlay.Enabled = show;
        _host.Save();
    }

    public void SyncClickThroughMenu() => ClickThroughItem.IsChecked = _host.Settings.Overlay.ClickThrough;

    private void ClickThrough_Click(object sender, RoutedEventArgs e)
    {
        _host.Settings.Overlay.ClickThrough = ClickThroughItem.IsChecked;
        _host.Save();
        _overlay?.ApplyClickThrough();
    }

    private void Topmost_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return; // initial state comes from settings
        Topmost = TopmostButton.IsChecked == true;
        _host.Settings.Display.Topmost = Topmost;
        _host.Save();
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => _host.ViewModel.Clear();

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    public void OpenSettings()
    {
        var dialog = new SettingsWindow(_host) { Owner = this, Topmost = Topmost };
        if (dialog.ShowDialog() != true)
            return;
        Topmost = _host.Settings.Display.Topmost;
        TopmostButton.IsChecked = Topmost;
        SyncClickThroughMenu();
        _overlay?.ApplyStyle();
        _overlay?.ApplyClickThrough();
    }

    private void ProviderButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ProviderButton };
        foreach (var profile in _host.Settings.Profiles)
        {
            var id = profile.Id;
            var item = new MenuItem
            {
                Header = profile.Name,
                IsCheckable = true,
                IsChecked = id == _host.Settings.ActiveProfileId,
            };
            item.Click += (_, _) => _host.Update(s =>
            {
                s.ActiveProfileId = id;
                if (s.BackupProfileId == id)
                    s.BackupProfileId = "";
            });
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var manage = new MenuItem { Header = "管理翻译服务…" };
        manage.Click += (_, _) => OpenSettings();
        menu.Items.Add(manage);
        menu.IsOpen = true;
    }

    private void LanguageButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = LanguageButton };
        foreach (var language in Languages.All)
        {
            var code = language.Code;
            var item = new MenuItem
            {
                Header = language.DisplayName,
                IsCheckable = true,
                IsChecked = code == _host.Settings.TargetLanguage,
            };
            item.Click += (_, _) => _host.Update(s => s.TargetLanguage = code);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void ManualInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || string.IsNullOrWhiteSpace(ManualInput.Text))
            return;
        _host.Engine.TranslateText(ManualInput.Text);
        ManualInput.Clear();
        _autoScroll = true;
        e.Handled = true;
    }

    /// <summary>Follows new text unless the user scrolled up to read history.</summary>
    private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0)
            _autoScroll = Scroller.VerticalOffset >= Scroller.ScrollableHeight - 4;
        else if (_autoScroll)
            Scroller.ScrollToEnd();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _host.Settings.Display.MainBounds = WindowBounds.Capture(this);
        var overlayWasOpen = _overlay is not null;
        _overlay?.Close();
        _host.Settings.Overlay.Enabled = overlayWasOpen;
        _host.Save();
    }
}
