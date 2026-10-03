using System.Collections.ObjectModel;

using LiveTranslator.Core.Models;
using LiveTranslator.Core.Pipeline;

namespace LiveTranslator.App.ViewModels;

public sealed class EntryViewModel : ObservableModel
{
    private string _source = "";
    private string _translation = "";
    private bool _isProvisional;
    private bool _isWorking;
    private string _latency = "";
    private string? _error;

    public EntryViewModel(long id) => Id = id;

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
        Source = s.Source;
        Translation = s.Translation.Length > 0 ? s.Translation : s.Status == EntryStatus.Pending ? "…" : s.Translation;
        IsProvisional = s.IsProvisional || s.Status == EntryStatus.Pending;
        IsWorking = s.Status is EntryStatus.Pending or EntryStatus.Streaming;
        var hadError = HasError;
        Error = s.Error;
        if (hadError != HasError)
            RaisePropertyChanged(nameof(HasError));
        Latency = s switch
        {
            { Status: EntryStatus.Done, TotalMs: { } total } when s.FirstTokenMs is { } first && first != total => $"{first} / {total} ms",
            { Status: EntryStatus.Done, TotalMs: { } total } => $"{total} ms",
            { FirstTokenMs: { } first } => $"{first} ms…",
            _ => "",
        };
    }
}

public sealed class MainViewModel : ObservableModel
{
    private readonly Dictionary<long, EntryViewModel> _byId = [];
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
        if (!_byId.TryGetValue(snapshot.Id, out var vm))
        {
            vm = new EntryViewModel(snapshot.Id);
            _byId[snapshot.Id] = vm;
            // Ids grow monotonically, so appending keeps chronological order.
            Entries.Add(vm);
            Trim();
            RaisePropertyChanged(nameof(IsEmpty));
        }
        vm.Update(snapshot);
        RefreshOverlay();
    }

    public void Apply(PartialSnapshot snapshot)
    {
        PartialSource = snapshot.Source;
        PartialTranslation = snapshot.Translation;
        PartialTranslating = snapshot.IsTranslating;
        RaisePropertyChanged(nameof(IsEmpty));
        RefreshOverlay();
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
        RaisePropertyChanged(nameof(IsEmpty));
        RefreshOverlay();
    }

    private void Trim()
    {
        while (Entries.Count > HistoryLimit)
        {
            _byId.Remove(Entries[0].Id);
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
