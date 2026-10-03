using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

using LiveTranslator.App.Services;

namespace LiveTranslator.App.Views;

public partial class OverlayWindow : Window
{
    private readonly AppHost _host;

    public OverlayWindow(AppHost host)
    {
        InitializeComponent();
        _host = host;
        DataContext = host.ViewModel;
        WindowBounds.Restore(this, host.Settings.Overlay.Bounds);
        if (string.IsNullOrEmpty(host.Settings.Overlay.Bounds))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2;
            Top = SystemParameters.WorkArea.Bottom - Height - 40;
        }
        ApplyStyle();
        SourceInitialized += (_, _) => ApplyClickThrough();
        Closing += (_, _) =>
        {
            _host.Settings.Overlay.Bounds = WindowBounds.Capture(this);
            _host.Save();
        };
    }

    public void ApplyStyle()
    {
        var o = _host.Settings.Overlay;
        TranslationText.FontSize = PreviousText.FontSize = o.FontSize;
        OriginalText.FontSize = Math.Max(10, o.FontSize * 0.8);
        OriginalText.Visibility = o.ShowOriginal ? Visibility.Visible : Visibility.Collapsed;
        PreviousText.Visibility = o.ShowPrevious ? Visibility.Visible : Visibility.Collapsed;
        OriginalToggle.IsChecked = o.ShowOriginal;
        Backdrop.Opacity = o.BackgroundOpacity;
        Brush text;
        try
        {
            text = new SolidColorBrush((Color)ColorConverter.ConvertFromString(o.TextColor));
        }
        catch (FormatException)
        {
            text = Brushes.WhiteSmoke;
        }
        TranslationText.Foreground = PreviousText.Foreground = text;
    }

    /// <summary>Click-through lets the overlay sit over a video player without stealing clicks.</summary>
    public void ApplyClickThrough()
    {
        var hWnd = new WindowInteropHelper(this).Handle;
        if (hWnd == 0)
            return;
        NativeMethods.SetExStyleFlag(hWnd, NativeMethods.WS_EX_TRANSPARENT, _host.Settings.Overlay.ClickThrough);
        if (_host.Settings.Overlay.ClickThrough)
            Controls.Visibility = Visibility.Hidden;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e) => Controls.Visibility = Visibility.Visible;

    private void Window_MouseLeave(object sender, MouseEventArgs e) => Controls.Visibility = Visibility.Hidden;

    private void FontSmaller_Click(object sender, RoutedEventArgs e) => ChangeFont(-2);

    private void FontLarger_Click(object sender, RoutedEventArgs e) => ChangeFont(+2);

    private void ChangeFont(double delta)
    {
        var o = _host.Settings.Overlay;
        o.FontSize = Math.Clamp(o.FontSize + delta, 10, 120);
        ApplyStyle();
        _host.Save();
    }

    private void OriginalToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return; // ApplyStyle() setting the initial state
        _host.Settings.Overlay.ShowOriginal = OriginalToggle.IsChecked == true;
        ApplyStyle();
        _host.Save();
    }

    private void Lock_Click(object sender, RoutedEventArgs e)
    {
        _host.Settings.Overlay.ClickThrough = true;
        _host.Save();
        ApplyClickThrough();
        (Application.Current.MainWindow as MainWindow)?.SyncClickThroughMenu();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
