using System.IO;
using System.Net.Http;
using System.Windows.Threading;

using LiveTranslator.App.ViewModels;
using LiveTranslator.Core.Http;
using LiveTranslator.Core.Models;
using LiveTranslator.Core.Pipeline;
using LiveTranslator.Core.Providers;
using LiveTranslator.Core.Settings;

namespace LiveTranslator.App.Services;

/// <summary>Owns the long-lived services and applies settings changes to them.</summary>
public sealed class AppHost : IAsyncDisposable
{
    private static readonly TimeSpan WarmWhenIdleFor = TimeSpan.FromSeconds(40);

    private readonly UiUpdateQueue _ui;
    private readonly DispatcherTimer _warmTimer;
    private string _proxy;
    private volatile bool _showingError;

    public AppHost(Dispatcher dispatcher, bool captionsEnabled)
    {
        Store = new SettingsStore(Path.Combine(DataDirectory, "settings.json"), new DpapiProtector());
        Settings = Store.Load();
        ViewModel = new MainViewModel();
        _ui = new UiUpdateQueue(dispatcher, ViewModel);
        _proxy = Settings.Network.Proxy;
        Http = CreateHttp(_proxy);
        Log = new HistoryLog(Path.Combine(DataDirectory, "logs"));

        Engine = new TranslationEngine(BuildConfig(Settings));
        Engine.EntryChanged += OnEntryChanged;
        Engine.PartialChanged += _ui.Post;
        Engine.Error += message =>
        {
            _showingError = true;
            _ui.PostStatus(message, isError: true);
        };

        if (captionsEnabled)
        {
            Captions = new LiveCaptionsSource
            {
                PollMs = Settings.Pipeline.CaptionPollMs,
                HideWindow = Settings.Pipeline.HideLiveCaptionsWindow,
            };
            Captions.TextChanged += Engine.Submit;
            Captions.StateChanged += (state, message) => _ui.PostStatus(message, state == CaptionState.Unavailable);
        }

        ApplyToViewModel();
        _warmTimer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) => KeepWarm(), dispatcher);
    }

    /// <summary>%APPDATA%\LiveTranslator, or the LIVETRANSLATOR_HOME folder (portable installs, separate profiles).</summary>
    public static string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("LIVETRANSLATOR_HOME") is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiveTranslator");

    public SettingsStore Store { get; }
    public AppSettings Settings { get; private set; }
    public HttpClient Http { get; private set; }
    public TranslationEngine Engine { get; }
    public LiveCaptionsSource? Captions { get; }
    public MainViewModel ViewModel { get; }
    public HistoryLog Log { get; }

    public event Action? SettingsApplied;

    public void Start()
    {
        Captions?.Start();
        ViewModel.IsRunning = true;
        if (Captions is null)
            ViewModel.SetStatus("手动模式：未启用实时辅助字幕，可在右下角输入文字翻译");
        _warmTimer.Start();
        _ = WarmUpAsync();
    }

    public void Pause()
    {
        Captions?.Stop();
        Engine.Reset();
        ViewModel.IsRunning = false;
    }

    /// <summary>Validates nothing: callers pass a draft that already passed the settings dialog.</summary>
    public void ApplySettings(AppSettings settings)
    {
        settings.Normalize();
        if (settings.Network.Proxy != _proxy)
        {
            _proxy = settings.Network.Proxy;
            // In-flight requests keep the old client; it is left for the GC rather than disposed under them.
            Http = CreateHttp(_proxy);
        }
        Settings = settings;
        Engine.UpdateConfig(BuildConfig(settings));
        if (Captions is not null)
        {
            Captions.PollMs = settings.Pipeline.CaptionPollMs;
            Captions.HideWindow = settings.Pipeline.HideLiveCaptionsWindow;
        }
        ApplyToViewModel();
        Save();
        _ = WarmUpAsync();
        SettingsApplied?.Invoke();
    }

    /// <summary>Applies a small change (quick switches in the main window) through a cloned draft.</summary>
    public void Update(Action<AppSettings> change)
    {
        var draft = SettingsStore.Clone(Settings);
        change(draft);
        ApplySettings(draft);
    }

    public void Save()
    {
        try
        {
            Store.Save(Settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ViewModel.SetStatus($"保存设置失败: {ex.Message}", isError: true);
        }
    }

    public ITranslationProvider CreateProvider(ProviderProfile profile) =>
        ProviderFactory.Validate(profile) is { } problem
            ? new MisconfiguredProvider(profile.Name, problem)
            : ProviderFactory.Create(profile, Http);

    private EngineConfig BuildConfig(AppSettings s)
    {
        var language = Languages.Get(s.TargetLanguage);
        var active = s.FindProfile(s.ActiveProfileId)!;
        var provider = CreateProvider(active);
        var backup = s.FindProfile(s.BackupProfileId);
        if (backup is not null)
            provider = new HedgedProvider(provider, CreateProvider(backup), TimeSpan.FromMilliseconds(s.HedgeDelayMs));

        var prompt = PromptBuilder.RenderSystemPrompt(s.SystemPrompt, language);
        var scope = string.Join('|', active.Id, active.Protocol, active.BaseUrl, active.Model, backup?.Id ?? "", prompt.GetHashCode());
        return new EngineConfig(provider, scope, language, prompt, s.Pipeline);
    }

    private void ApplyToViewModel()
    {
        var active = Settings.FindProfile(Settings.ActiveProfileId);
        var backup = Settings.FindProfile(Settings.BackupProfileId);
        ViewModel.ProviderName = backup is null ? active?.Name ?? "" : $"{active?.Name} ⇄ {backup.Name}";
        ViewModel.LanguageName = Languages.Get(Settings.TargetLanguage).DisplayName;
        ViewModel.OriginalFontSize = Settings.Display.OriginalFontSize;
        ViewModel.TranslationFontSize = Settings.Display.TranslationFontSize;
        ViewModel.ShowLatency = Settings.Display.ShowLatency;
        ViewModel.HistoryLimit = Settings.Display.HistoryCount;
    }

    private void OnEntryChanged(EntrySnapshot snapshot)
    {
        _ui.Post(snapshot);
        if (snapshot.Status != EntryStatus.Done)
            return;
        if (_showingError)
        {
            _showingError = false; // a transient failure should not leave the status bar red
            _ui.PostStatus($"翻译已恢复正常（{snapshot.TotalMs} ms）", isError: false);
        }
        if (snapshot.Translation.Length > 0 && Settings.Display.LogToFile)
            Log.Append(snapshot.Source, snapshot.Translation);
    }

    private HttpClient CreateHttp(string proxy)
    {
        try
        {
            return TranslatorHttpClient.Create(proxy);
        }
        catch (FormatException ex)
        {
            ViewModel.SetStatus(ex.Message + "，已改用系统代理", isError: true);
            return TranslatorHttpClient.Create();
        }
    }

    private Task WarmUpAsync() =>
        Settings.Pipeline.KeepConnectionWarm ? Engine.Config.Provider.WarmUpAsync() : Task.CompletedTask;

    /// <summary>Re-opens the connection before the server's idle timeout drops it, so the next sentence skips TLS setup.</summary>
    private void KeepWarm()
    {
        if (ViewModel.IsRunning && Engine.IdleTime > WarmWhenIdleFor)
            _ = WarmUpAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _warmTimer.Stop();
        Captions?.Dispose();
        await Engine.DisposeAsync().ConfigureAwait(false);
        await Log.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Coalesces engine updates onto the UI thread: however many tokens arrive between two dispatcher
/// turns, each entry is re-rendered once with its newest state.
/// </summary>
internal sealed class UiUpdateQueue
{
    private readonly Dispatcher _dispatcher;
    private readonly MainViewModel _viewModel;
    private readonly object _gate = new();
    private readonly SortedDictionary<long, EntrySnapshot> _entries = [];
    private PartialSnapshot? _partial;
    private (string Message, bool IsError)? _status;
    private bool _scheduled;

    public UiUpdateQueue(Dispatcher dispatcher, MainViewModel viewModel)
    {
        _dispatcher = dispatcher;
        _viewModel = viewModel;
    }

    public void Post(EntrySnapshot snapshot)
    {
        lock (_gate)
        {
            _entries[snapshot.Id] = snapshot;
            ScheduleLocked();
        }
    }

    public void Post(PartialSnapshot snapshot)
    {
        lock (_gate)
        {
            _partial = snapshot;
            ScheduleLocked();
        }
    }

    public void PostStatus(string message, bool isError)
    {
        lock (_gate)
        {
            _status = (message, isError);
            ScheduleLocked();
        }
    }

    private void ScheduleLocked()
    {
        if (_scheduled)
            return;
        _scheduled = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Normal, Flush);
    }

    private void Flush()
    {
        EntrySnapshot[] entries;
        PartialSnapshot? partial;
        (string Message, bool IsError)? status;
        lock (_gate)
        {
            entries = [.. _entries.Values];
            _entries.Clear();
            partial = _partial;
            _partial = null;
            status = _status;
            _status = null;
            _scheduled = false;
        }

        foreach (var entry in entries)
            _viewModel.Apply(entry);
        if (partial is not null)
            _viewModel.Apply(partial);
        if (status is { } s)
            _viewModel.SetStatus(s.Message, s.IsError);
    }
}
