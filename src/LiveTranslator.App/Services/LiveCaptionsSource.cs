using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace LiveTranslator.App.Services;

public enum CaptionState { Stopped, Starting, Listening, Unavailable }

/// <summary>
/// Reads the recognized text out of Windows Live Captions through UI Automation.
/// Window discovery and hiding follow LiveCaptions-Translator (Apache-2.0).
/// </summary>
/// <remarks>
/// Polling runs on a dedicated thread and raises <see cref="TextChanged"/> the instant the text
/// differs, so caption latency is bounded by the poll interval (15 ms by default) rather than by
/// a chain of sleeping loops.
/// </remarks>
public sealed class LiveCaptionsSource : IDisposable
{
    private const string ProcessName = "LiveCaptions";
    private const string WindowClass = "LiveCaptionsDesktopWindow";
    private const string TextBlockId = "CaptionsTextBlock";

    private readonly object _gate = new();
    private Thread? _thread;
    private volatile bool _running;
    private volatile int _pollMs = 15;
    private volatile bool _hideWindow = true;
    private AutomationElement? _window;
    private AutomationElement? _textBlock;
    private Process? _launchedByUs;
    private bool _hidden;
    private CaptionState _lastState = CaptionState.Stopped;
    private string _lastMessage = "";

    public event Action<string>? TextChanged;
    public event Action<CaptionState, string>? StateChanged;

    public int PollMs
    {
        get => _pollMs;
        set => _pollMs = Math.Clamp(value, 5, 500);
    }

    public bool HideWindow
    {
        get => _hideWindow;
        set
        {
            _hideWindow = value;
            lock (_gate)
            {
                if (_window is null)
                    return;
                if (value) Hide(_window);
                else Restore(_window);
            }
        }
    }

    public bool IsRunning => _running;

    public void Start()
    {
        lock (_gate)
        {
            if (_running)
                return;
            _running = true;
            _thread = new Thread(Run) { IsBackground = true, Name = "LiveCaptions reader", Priority = ThreadPriority.AboveNormal };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }
    }

    public void Stop()
    {
        Thread? thread;
        lock (_gate)
        {
            _running = false;
            thread = _thread;
            _thread = null;
        }
        thread?.Join(TimeSpan.FromSeconds(2));
        Report(CaptionState.Stopped, "已暂停");
    }

    /// <summary>Stops reading and gives Live Captions back: restored if it was already open, closed if we started it.</summary>
    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            try
            {
                if (_launchedByUs is { HasExited: false } process)
                {
                    process.Kill();
                    process.WaitForExit(2000);
                }
                else if (_window is not null)
                {
                    Restore(_window);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or ElementNotAvailableException or COMException)
            {
                // Already gone.
            }
            _window = null;
            _textBlock = null;
        }
    }

    private void Run()
    {
        var last = "";
        Report(CaptionState.Starting, "正在连接 Windows 实时辅助字幕…");
        while (_running)
        {
            try
            {
                if (_textBlock is null)
                {
                    var attach = TryAttach();
                    if (attach != Attach.Ready)
                    {
                        // The caption text element only exists while speech is on screen, so poll for it
                        // quickly; a missing window is a slower condition (launching / not installed).
                        Sleep(attach == Attach.WaitingForSpeech ? 50 : 1000);
                        continue;
                    }
                }

                var text = _textBlock!.Current.Name ?? "";
                if (!string.Equals(text, last, StringComparison.Ordinal))
                {
                    last = text;
                    TextChanged?.Invoke(text);
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
            {
                lock (_gate)
                {
                    _textBlock = null;
                    if (!IsAlive(_window))
                    {
                        // Live Captions was closed: reattach, relaunching it if needed.
                        _window = null;
                        _hidden = false;
                        Report(CaptionState.Starting, "实时辅助字幕已断开，正在重新连接…");
                    }
                }
            }
            Sleep(_pollMs);
        }
    }

    private enum Attach { Ready, WaitingForSpeech, NoWindow }

    private Attach TryAttach()
    {
        lock (_gate)
        {
            if (_window is null)
            {
                _window = FindWindow() ?? Launch();
                if (_window is null)
                {
                    Report(CaptionState.Unavailable,
                        "找不到 Windows 实时辅助字幕（需要 Windows 11 22H2 及以上）。仍可在右下角手动输入文字翻译。");
                    return Attach.NoWindow;
                }
                EnsureOnScreen(_window);
            }

            if (_hideWindow && !_hidden)
                Hide(_window);

            _textBlock = _window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, TextBlockId));
        }
        Report(CaptionState.Listening, _textBlock is null ? "已连接实时辅助字幕，等待语音…" : "正在监听实时辅助字幕");
        return _textBlock is null ? Attach.WaitingForSpeech : Attach.Ready;
    }

    private static bool IsAlive(AutomationElement? window)
    {
        if (window is null)
            return false;
        try
        {
            _ = window.Current.ProcessId;
            return true;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            return false;
        }
    }

    private void Report(CaptionState state, string message)
    {
        if (state == _lastState && message == _lastMessage)
            return;
        _lastState = state;
        _lastMessage = message;
        StateChanged?.Invoke(state, message);
    }

    private static AutomationElement? FindWindow() =>
        AutomationElement.RootElement.FindFirst(TreeScope.Children,
            new PropertyCondition(AutomationElement.ClassNameProperty, WindowClass));

    private AutomationElement? Launch()
    {
        try
        {
            if (Process.GetProcessesByName(ProcessName).Length == 0)
                _launchedByUs = Process.Start(new ProcessStartInfo(ProcessName + ".exe") { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            return null; // not installed on this Windows version
        }

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && _running)
        {
            if (FindWindow() is { } window)
                return window;
            Thread.Sleep(100);
        }
        return null;
    }

    private static nint Handle(AutomationElement window) => new(window.Current.NativeWindowHandle);

    /// <summary>UI Automation stops reporting text for a window placed off-screen.</summary>
    private static void EnsureOnScreen(AutomationElement window)
    {
        var hWnd = Handle(window);
        if (!NativeMethods.GetWindowRect(hWnd, out var rect))
            return;
        if (rect.Left < 0 || rect.Top < 0 || rect.Right - rect.Left < 100 || rect.Bottom - rect.Top < 100)
            NativeMethods.MoveWindow(hWnd, 800, 600, 600, 200, true);
    }

    private void Hide(AutomationElement window)
    {
        var hWnd = Handle(window);
        NativeMethods.ShowWindow(hWnd, NativeMethods.SW_MINIMIZE);
        NativeMethods.SetExStyleFlag(hWnd, NativeMethods.WS_EX_TOOLWINDOW, true); // also hides it from the taskbar
        _hidden = true;
    }

    private void Restore(AutomationElement window)
    {
        var hWnd = Handle(window);
        NativeMethods.SetExStyleFlag(hWnd, NativeMethods.WS_EX_TOOLWINDOW, false);
        NativeMethods.ShowWindow(hWnd, NativeMethods.SW_RESTORE);
        _hidden = false;
    }

    private void Sleep(int ms)
    {
        // Sliced so Stop() returns promptly even during the long "not found" back-off.
        var end = Environment.TickCount64 + ms;
        while (_running)
        {
            var left = end - Environment.TickCount64;
            if (left <= 0)
                return;
            Thread.Sleep((int)Math.Min(left, 100));
        }
    }
}
