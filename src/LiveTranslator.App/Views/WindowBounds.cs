using System.Globalization;
using System.Windows;

namespace LiveTranslator.App.Views;

/// <summary>Persists window placement as "left,top,width,height" and refuses positions now off-screen.</summary>
internal static class WindowBounds
{
    public static string Capture(Window window)
    {
        var r = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;
        return string.Join(",", new[] { r.Left, r.Top, r.Width, r.Height }.Select(v => v.ToString("0", CultureInfo.InvariantCulture)));
    }

    public static void Restore(Window window, string? saved)
    {
        var parts = (saved ?? "").Split(',');
        if (parts.Length != 4 ||
            !parts.All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }
        var v = parts.Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        var rect = new Rect(v[0], v[1], Math.Max(v[2], window.MinWidth), Math.Max(v[3], window.MinHeight));
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        // Monitor unplugged since last run: keep the size, re-center.
        if (!screen.IntersectsWith(new Rect(rect.Left, rect.Top, Math.Min(rect.Width, 200), 40)))
        {
            window.Width = rect.Width;
            window.Height = rect.Height;
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = rect.Left;
        window.Top = rect.Top;
        window.Width = rect.Width;
        window.Height = rect.Height;
    }
}
