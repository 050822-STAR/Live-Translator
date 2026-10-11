using System.Collections.ObjectModel;

using LiveTranslator.Core.Models;
using LiveTranslator.Core.Pipeline;

namespace LiveTranslator.App.ViewModels;

/// <summary>One transcript line: a sentence, joined from its fragments when it was translated in pieces.</summary>
public sealed class EntryViewModel : ObservableModel
{
    private readonly SentenceLine _line;
    private string _source = "";
    private string _translation = "";
    private bool _isProvisional;
    private bool _isWorking;
    private string _latency = "";
    private string? _error;

    public EntryViewModel(long group)
    {
        Id = group;
        _line = new SentenceLine(group);
    }

    /// <summary>The sentence's group id.</summary>
    public long Id { get; }
    public string Source { get => _source; private set => Set(ref _source, value); }
    public string Translation { get => _translation; private set => Set(ref _translation, value); }
    public bool IsProvisional { get => _isProvisional; private set => Set(ref _isProvisional, value); }
    public bool IsWorking { get => _isWorking; private set => Set(ref _isWorking, value); }
    public string Latency { get => _latency; private set => Set(ref _latency, value); }
    public string? Error { get => _error; private set => Set(ref _error, value); }
    public bool HasError => Error is not null;

    public void Update(EntrySnapshot s)
    {
        _line.Apply(s);
        Refresh();
    }

    /// <summary>Continues the line with the clause still being spoken (empty text removes it).</summary>
    public void SetLive(string source, string translation)
    {
        _line.SetLive(source, translation);
        Refresh();
    }

    private void Refresh()
    {
        Source = _line.Source;
        Translation = _line.Translation;
        IsProvisional = _line.IsProvisional;
        IsWorking = _line.IsWorking;
        var hadError = HasError;
        Error = _line.Error;
        if (hadError != HasError)
            RaisePropertyChanged(nameof(HasError));
        Latency = _line.Latest switch
        {
            null => "",
            { Status: EntryStatus.Done, TotalMs: { } total, FirstTokenMs: { } first } when first != total => $"{first} / {total} ms",
            { Status: EntryStatus.Done, TotalMs: { } total } => $"{total} ms",
            { FirstTokenMs: { } first } => $"{first} ms…",
            _ => "",
        };
    }
}

public sealed class MainViewModel : ObservableModel
{
    private const int LatencyWindow = 30;

    private readonly Dictionary<long, EntryViewModel> _byId = [];
    private readonly Dictionary<long, LatencySample> _latency = [];
    private readonly Queue<long> _latencyOrder = new();
    private string _latencySummary = "";
    private string _partialSource = "";
    private string _partialTranslation = "";
    private bool _partialTranslating;
    private string _status = "准备就绪";
    private bool _statusIsError;
    private bool _isRunning;
    private string _providerName = "";
    private string _languageName = "";
    private double _originalFontSize = 14;
    private double _translationFontSize = 19;
    private bool _showLatency = true;
    private int _historyLimit = 30;
    private string _overlayOriginal = "";
    private string _overlayTranslation = "";
    private string _overlayPrevious = "";
    private PartialSnapshot? _live;
    private EntryViewModel? _liveRow;

    public ObservableCollection<EntryViewModel> Entries { get; } = [];

    public string PartialSource { get => _partialSource; private set { if (Set(ref _partialSource, value)) RaisePropertyChanged(nameof(HasPartial)); } }
    public string PartialTranslation { get => _partialTranslation; private set => Set(ref _partialTranslation, value); }
    public bool PartialTranslating { get => _partialTranslating; private set => Set(ref _partialTranslating, value); }
    public bool HasPartial => PartialSource.Length > 0;
    public bool IsEmpty => Entries.Count == 0 && !HasPartial;

    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool StatusIsError { get => _statusIsError; private set => Set(ref _statusIsError, value); }
    public bool IsRunning { get => _isRunning; set => Set(ref _isRunning, value); }
    public string ProviderName { get => _providerName; set => Set(ref _providerName, value); }
    public string LanguageName { get => _languageName; set => Set(ref _languageName, value); }
    public double OriginalFontSize { get => _originalFontSize; set => Set(ref _originalFontSize, value); }
    public double TranslationFontSize { get => _translationFontSize; set => Set(ref _translationFontSize, value); }
    public bool ShowLatency { get => _showLatency; set => Set(ref _showLatency, value); }

    /// <summary>Rolling latency of recent sentences, e.g. to judge the effect of a settings change.</summary>
    public string LatencySummary { get => _latencySummary; private set => Set(ref _latencySummary, value); }

    public int HistoryLimit
    {
        get => _historyLimit;
        set
        {
            if (Set(ref _historyLimit, Math.Max(1, value)))
                Trim();
        }
    }

    public string OverlayOriginal { get => _overlayOriginal; private set => Set(ref _overlayOriginal, value); }
    public string OverlayTranslation { get => _overlayTranslation; private set => Set(ref _overlayTranslation, value); }
    public string OverlayPrevious { get => _overlayPrevious; private set => Set(ref _overlayPrevious, value); }

    public void Apply(EntrySnapshot snapshot)
    {
        var group = snapshot.Group != 0 ? snapshot.Group : snapshot.Id;
        if (!_byId.TryGetValue(group, out var vm))
        {
            vm = new EntryViewModel(group);
            _byId[group] = vm;
            // Group ids grow monotonically, so appending keeps chronological order.
            Entries.Add(vm);
            Trim();
            RaisePropertyChanged(nameof(IsEmpty));
        }
        vm.Update(snapshot);
        if (snapshot is { Status: EntryStatus.Done, TotalMs: { } total })
            RecordLatency(snapshot.Id, new LatencySample(snapshot.FirstTokenMs ?? total, total, snapshot.Trace?.HeadersMs, snapshot.Trace?.Chunks));
        if (_live?.Group == group)
            RefreshLive(); // the line the unfinished clause continues has just appeared
        RefreshOverlay();
    }

    private void RecordLatency(long id, LatencySample sample)
    {
        if (!_latency.ContainsKey(id))
        {
            _latencyOrder.Enqueue(id);
            while (_latencyOrder.Count > LatencyWindow)
                _latency.Remove(_latencyOrder.Dequeue());
        }
        _latency[id] = sample; // a revised sentence replaces its earlier measurement
        LatencySummary = Summarize([.. _latency.Values]);
    }

    /// <param name="NetworkMs">Request sent → response headers: connection and gateway time, before the model answers.</param>
    /// <param name="Chunks">Pieces the answer streamed in; about one means it arrived in a single burst.</param>
    internal sealed record LatencySample(int First, int Total, int? NetworkMs, int? Chunks);

    internal static string Summarize(IReadOnlyCollection<LatencySample> samples)
    {
        if (samples.Count == 0)
            return "";
        var first = samples.Select(s => s.First).Order().ToArray();
        var total = samples.Select(s => s.Total).Order().ToArray();
        var summary = $"近 {samples.Count} 段 · 首字中位 {Percentile(first, 0.5)} ms · 90% ≤ {Percentile(first, 0.9)} ms · 完成中位 {Percentile(total, 0.5)} ms";
        var network = samples.Select(s => s.NetworkMs).OfType<int>().Order().ToArray();
        if (network.Length > 0)
            summary += $" · 网络中位 {Percentile(network, 0.5)} ms";
        var chunks = samples.Select(s => s.Chunks).OfType<int>().Where(c => c > 0).ToArray();
        if (chunks.Length > 0)
            summary += $" · 平均分 {chunks.Average():0.#} 次到达";
        return summary;
    }

    // Nearest-rank percentile: always a value that was actually observed.
    private static int Percentile(int[] sorted, double p) =>
        sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];

    public void Apply(PartialSnapshot snapshot)
    {
        _live = snapshot;
        RefreshLive();
        RefreshOverlay();
    }

    /// <summary>
    /// The unfinished clause continues the line of its sentence's finished fragments when that line
    /// is on screen; otherwise it is shown on its own below the transcript.
    /// </summary>
    private void RefreshLive()
    {
        var live = _live;
        var row = live is { Group: not 0 } && _byId.TryGetValue(live.Group, out var r) ? r : null;
        if (_liveRow is not null && _liveRow != row)
            _liveRow.SetLive("", "");
        _liveRow = row;
        if (row is not null)
        {
            row.SetLive(live!.Source, live.Translation);
            PartialSource = PartialTranslation = "";
        }
        else
        {
            PartialSource = live?.Source ?? "";
            PartialTranslation = live?.Translation ?? "";
        }
        PartialTranslating = live?.IsTranslating ?? false;
        RaisePropertyChanged(nameof(IsEmpty));
    }

    public void SetStatus(string message, bool isError = false)
    {
        Status = message;
        StatusIsError = isError;
    }

    public void Clear()
    {
        Entries.Clear();
        _byId.Clear();
        _liveRow = null;
        RaisePropertyChanged(nameof(IsEmpty));
        RefreshOverlay();
    }

    private void Trim()
    {
        while (Entries.Count > HistoryLimit)
        {
            _byId.Remove(Entries[0].Id);
            if (_liveRow == Entries[0])
                _liveRow = null;
            Entries.RemoveAt(0);
        }
    }

    /// <summary>Overlay shows the live sentence when there is one, otherwise the latest finished one.</summary>
    private void RefreshOverlay()
    {
        var last = Entries.Count > 0 ? Entries[^1] : null;
        if (PartialTranslation.Length > 0 || (HasPartial && last is null))
        {
            OverlayOriginal = PartialSource;
            OverlayTranslation = PartialTranslation;
            OverlayPrevious = last?.Translation ?? "";
        }
        else
        {
            OverlayOriginal = HasPartial ? PartialSource : last?.Source ?? "";
            OverlayTranslation = last?.Translation ?? "";
            OverlayPrevious = Entries.Count > 1 ? Entries[^2].Translation : "";
        }
    }
}
