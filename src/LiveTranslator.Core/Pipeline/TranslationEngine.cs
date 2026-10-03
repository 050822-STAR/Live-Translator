using System.Text;
using System.Threading.Channels;

using LiveTranslator.Core.Models;
using LiveTranslator.Core.Providers;

namespace LiveTranslator.Core.Pipeline;

public enum EntryStatus { Pending, Streaming, Done, Error }

/// <param name="IsProvisional">Translation shown is a placeholder (e.g. the partial translation) until the final one streams in.</param>
/// <param name="FirstTokenMs">From sentence completion to the first token of its own translation.</param>
/// <param name="TotalMs">From sentence completion to the finished translation.</param>
public sealed record EntrySnapshot(
    long Id,
    string Source,
    string Translation,
    EntryStatus Status,
    bool IsProvisional,
    int? FirstTokenMs,
    int? TotalMs,
    string? Error);

/// <summary>The sentence currently being spoken.</summary>
public sealed record PartialSnapshot(string Source, string Translation, bool IsTranslating);

/// <param name="CacheScope">Identifies provider + model + prompt so cached results never cross configurations.</param>
public sealed record EngineConfig(
    ITranslationProvider Provider,
    string CacheScope,
    LanguageInfo Target,
    string SystemPrompt,
    PipelineOptions Options);

/// <summary>
/// Turns a stream of caption snapshots into translated sentences with minimal latency.
/// </summary>
/// <remarks>
/// Latency strategy:
/// <list type="bullet">
/// <item>Event-driven: a snapshot is processed the moment it arrives (no polling sleeps).</item>
/// <item>The unfinished sentence is translated speculatively (rate-limited); when it completes with
/// the same words, that in-flight or finished request is promoted to the final translation instead
/// of starting a new one — often the final result exists before the period does.</item>
/// <item>All translations stream token by token; each token is published immediately.</item>
/// <item>Finished sentences never wait for each other: they run concurrently up to a limit.</item>
/// </list>
/// Event handlers are invoked under the engine lock and must return quickly without blocking.
/// </remarks>
public sealed class TranslationEngine : IAsyncDisposable
{
    private const int CandidateSentences = 4;
    private const int MaxTrackedEntries = 24;
    private const int MaxContextPairs = 10;
    private const int MaxRevisions = 6;
    private static readonly TimeSpan RevisionWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetireAfter = TimeSpan.FromSeconds(8);

    // When the speaker pauses, LiveCaptions usually adds the period a moment later. Firing the
    // speculative request as soon as the text settles (instead of waiting out the full interval)
    // gives it a head start that promotion then turns into a faster final translation.
    private static readonly TimeSpan PartialSettle = TimeSpan.FromMilliseconds(150);

    private readonly object _gate = new();
    private readonly Channel<string> _snapshots = Channel.CreateBounded<string>(new BoundedChannelOptions(1)
    {
        // Only the newest caption matters; intermediate ones are superseded.
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });
    private readonly CancellationTokenSource _life = new();
    private readonly TimeProvider _time;
    private readonly TranslationCache _cache = new(1024);
    private readonly List<Entry> _tracked = [];
    private readonly List<ContextPair> _context = [];
    private readonly Task _loop;

    private EngineConfig _config;
    private SemaphoreSlim _slots;
    private long _nextId;
    private bool _primed;
    private string _lastText = "";
    private long _lastActivity;

    private string _partialSource = "";
    private string _partialKey = "";
    private string _partialTranslation = "";
    private Job? _partialJob;
    private string? _pendingPartial;
    private long _lastPartialStart;
    private long _lastPartialChange;

    public TranslationEngine(EngineConfig config, TimeProvider? time = null)
    {
        _config = config;
        _time = time ?? TimeProvider.System;
        _slots = new SemaphoreSlim(config.Options.MaxConcurrentRequests);
        _lastActivity = _time.GetTimestamp();
        _loop = Task.Run(RunAsync);
    }

    public event Action<EntrySnapshot>? EntryChanged;
    public event Action<PartialSnapshot>? PartialChanged;
    public event Action<string>? Error;

    public EngineConfig Config
    {
        get { lock (_gate) return _config; }
    }

    /// <summary>Time since the last translation request finished or started.</summary>
    public TimeSpan IdleTime => _time.GetElapsedTime(Interlocked.Read(ref _lastActivity));

    /// <summary>Feeds the full caption text; thread-safe and non-blocking.</summary>
    public void Submit(string captionText) => _snapshots.Writer.TryWrite(captionText ?? "");

    /// <summary>Translates text directly, bypassing caption tracking (manual input).</summary>
    public void TranslateText(string text)
    {
        var source = text?.Trim() ?? "";
        var key = TextKey.Of(source);
        if (key.Length == 0)
            return;
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            StartFinal(Track(source, key, hidden: false, now), now);
        }
    }

    public void UpdateConfig(EngineConfig config)
    {
        lock (_gate)
        {
            var providerChanged = !ReferenceEquals(config.Provider, _config.Provider) || config.CacheScope != _config.CacheScope;
            if (config.Options.MaxConcurrentRequests != _config.Options.MaxConcurrentRequests)
                _slots = new SemaphoreSlim(config.Options.MaxConcurrentRequests); // running jobs release the old one they hold
            _config = config;
            if (providerChanged)
            {
                CancelPartial();
                _partialTranslation = "";
                _pendingPartial = WantsPartialTranslation(_partialSource, _partialKey, config.Options) ? _partialSource : null;
                EmitPartial();
            }
        }
    }

    /// <summary>Forgets caption tracking (e.g. after pausing). Running sentence translations still finish.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            CancelPartial();
            _tracked.Clear();
            _primed = false;
            _lastText = "";
            _partialSource = _partialKey = _partialTranslation = "";
            _pendingPartial = null;
            EmitPartial();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _life.Cancel();
        _snapshots.Writer.TryComplete();
        try
        {
            await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Shutting down; nothing useful to report.
        }
        lock (_gate)
        {
            CancelPartial();
            foreach (var e in _tracked)
                e.Job?.Abandon();
        }
    }

    // ───────────────────────────── snapshot loop ─────────────────────────────

    private async Task RunAsync()
    {
        var reader = _snapshots.Reader;
        var ct = _life.Token;
        try
        {
            while (true)
            {
                TimeSpan? due;
                lock (_gate)
                    due = PartialDueIn(_time.GetTimestamp());

                if (due is { } wait)
                {
                    if (wait <= TimeSpan.Zero)
                    {
                        lock (_gate)
                            FirePendingPartial(_time.GetTimestamp());
                        continue;
                    }
                    using var wake = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var readable = reader.WaitToReadAsync(wake.Token).AsTask();
                    var delay = Task.Delay(wait, _time, wake.Token);
                    var first = await Task.WhenAny(readable, delay).ConfigureAwait(false);
                    wake.Cancel();
                    if (first != readable)
                        continue;
                    if (!await readable.ConfigureAwait(false))
                        return;
                }
                else if (!await reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    return;
                }

                while (reader.TryRead(out var text))
                {
                    try
                    {
                        Process(text);
                    }
                    catch (Exception ex)
                    {
                        Error?.Invoke($"字幕处理出错: {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private void Process(string raw)
    {
        var text = TextNormalizer.Normalize(raw);
        lock (_gate)
        {
            if (text == _lastText)
                return;
            _lastText = text;

            var now = _time.GetTimestamp();
            var segmentation = Segmenter.Split(text);
            if (!_primed)
            {
                // Text already on screen when we attach belongs to the past: remember it so it is
                // not translated, except the latest sentence (which may be the first thing said).
                _primed = true;
                for (int i = 0; i < segmentation.Complete.Count - 1; i++)
                    Track(segmentation.Complete[i], TextKey.Of(segmentation.Complete[i]), hidden: true, now);
                if (segmentation.Complete.Count > 0)
                    CommitSentences([segmentation.Complete[^1]], now);
            }
            else
            {
                CommitSentences(segmentation.Complete, now);
            }
            Retire(TextKey.Of(text), now);
            UpdatePartial(segmentation.Partial, now);
        }
    }

    // ─────────────────────────── finished sentences ───────────────────────────

    /// <summary>
    /// LiveCaptions keeps a rolling window and keeps rewriting recent words, so every snapshot
    /// repeats sentences we have already seen, sometimes slightly corrected. Each one is matched
    /// against tracked entries: identical → ignore, corrected → re-translate in place, else new.
    /// </summary>
    private void CommitSentences(IReadOnlyList<string> complete, long now)
    {
        var consumed = new HashSet<Entry>();
        for (int i = Math.Max(0, complete.Count - CandidateSentences); i < complete.Count; i++)
        {
            var sentence = complete[i];
            var key = TextKey.Of(sentence);
            if (key.Length == 0)
                continue;

            Entry? exact = null, similar = null;
            for (int j = _tracked.Count - 1; j >= 0; j--)
            {
                var e = _tracked[j];
                if (consumed.Contains(e))
                    continue;
                if (e.Key == key)
                {
                    exact = e;
                    break;
                }
                if (similar is null && CanRevise(e, key, now))
                    similar = e;
            }

            if (exact is not null)
            {
                consumed.Add(exact);
                exact.SeenAt = now;
                if (exact.Source != sentence && !exact.Hidden)
                {
                    exact.Source = sentence; // punctuation/casing only: same words, same translation
                    Emit(exact);
                }
            }
            else if (similar is not null)
            {
                consumed.Add(similar);
                Revise(similar, sentence, key, now);
            }
            else
            {
                var entry = Track(sentence, key, hidden: false, now);
                consumed.Add(entry);
                StartFinal(entry, now);
            }
        }
    }

    private bool CanRevise(Entry e, string key, long now) =>
        e.Revisions < MaxRevisions &&
        _time.GetElapsedTime(e.ChangedAt, now) <= RevisionWindow &&
        TextKey.IsSameUtterance(e.Key, key);

    private void Revise(Entry e, string sentence, string key, long now)
    {
        e.Source = sentence;
        e.Key = key;
        e.Revisions++;
        e.ChangedAt = e.SeenAt = now;
        if (e.Hidden)
            return;
        e.Provisional = e.Translation.Length > 0; // keep showing the old translation until the new one streams
        e.Status = EntryStatus.Pending;
        e.Error = null;
        e.StartedAt = now;
        e.FirstTokenMs = e.TotalMs = null;
        StartFinal(e, now);
    }

    private Entry Track(string source, string key, bool hidden, long now)
    {
        var e = new Entry
        {
            Id = ++_nextId,
            Source = source,
            Key = key,
            Hidden = hidden,
            ChangedAt = now,
            SeenAt = now,
            StartedAt = now,
        };
        _tracked.Add(e);
        if (_tracked.Count > MaxTrackedEntries)
            _tracked.RemoveAt(0);
        return e;
    }

    private void Retire(string textKey, long now)
    {
        for (int i = _tracked.Count - 1; i >= 0; i--)
        {
            var e = _tracked[i];
            if (_time.GetElapsedTime(e.SeenAt, now) > RetireAfter && !textKey.Contains(e.Key, StringComparison.Ordinal))
                _tracked.RemoveAt(i);
        }
    }

    private void StartFinal(Entry e, long now)
    {
        e.Job?.Abandon();
        e.Job = null;
        var cfg = _config;

        // Fast path 1: the speculative partial request already covers exactly these words.
        if (_partialJob is { Failed: false } partial && partial.Key == e.Key)
        {
            _partialJob = null;
            partial.Entry = e;
            e.Job = partial;
            if (partial.Completed)
            {
                FinishEntry(e, partial, now);
                return;
            }
            if (partial.HasOutput)
                ApplyToEntry(e, partial, now);
            else
                SeedFromPartial(e);
            Emit(e);
            return;
        }

        // Fast path 2: seen before.
        if (cfg.Options.CacheEnabled && _cache.TryGet(CacheKey(cfg, e.Key), out var cached))
        {
            e.Translation = cached;
            e.Status = EntryStatus.Done;
            e.Provisional = false;
            e.FirstTokenMs = e.TotalMs = Ms(e.StartedAt, now);
            AddContext(e.Source, cached);
            Emit(e);
            return;
        }

        SeedFromPartial(e);
        var job = NewJob(e.Source, e.Key, e, cfg, now);
        e.Job = job;
        Emit(e);
        Launch(job, slotHeld: false);
    }

    /// <summary>Shows the partial translation immediately so the line never goes blank while waiting.</summary>
    private void SeedFromPartial(Entry e)
    {
        if (e.Translation.Length == 0 && _partialTranslation.Length > 0 && TextKey.IsSameUtterance(_partialKey, e.Key))
        {
            e.Translation = _partialTranslation;
            e.Provisional = true;
        }
    }

    // ──────────────────────────── partial sentence ────────────────────────────

    private void UpdatePartial(string partial, long now)
    {
        var options = _config.Options;
        partial = Tail(partial, options.PartialMaxChars);
        if (partial == _partialSource)
            return;

        var key = TextKey.Of(partial);
        if (!TextKey.IsSameUtterance(_partialKey, key))
        {
            // A different utterance: the old speculative translation is useless now.
            CancelPartial();
            _partialTranslation = "";
        }
        _partialSource = partial;
        _partialKey = key;
        _pendingPartial = null;
        _lastPartialChange = now;

        if (WantsPartialTranslation(partial, key, options) && _partialJob?.Key != key)
        {
            _pendingPartial = partial;
            if (PartialDueIn(now) <= TimeSpan.Zero)
                FirePendingPartial(now);
        }
        EmitPartial();
    }

    private static bool WantsPartialTranslation(string text, string key, PipelineOptions options) =>
        options.PartialTranslation && key.Length > 0 && TextKey.WeightedLength(text) >= options.PartialMinChars;

    /// <summary>
    /// A pending partial fires once the interval has passed since the previous request, or earlier
    /// if the text has settled — but never closer than half the interval to the previous request.
    /// </summary>
    private TimeSpan? PartialDueIn(long now)
    {
        if (_pendingPartial is null)
            return null;
        var interval = TimeSpan.FromMilliseconds(_config.Options.PartialIntervalMs);
        var sinceStart = _time.GetElapsedTime(_lastPartialStart, now);
        var byInterval = interval - sinceStart;
        var bySettle = PartialSettle - _time.GetElapsedTime(_lastPartialChange, now);
        var minSpacing = interval / 2 - sinceStart;
        return byInterval < bySettle ? byInterval : (bySettle > minSpacing ? bySettle : minSpacing);
    }

    private void FirePendingPartial(long now)
    {
        var text = _pendingPartial;
        if (text is null)
            return;
        _pendingPartial = null;
        var key = TextKey.Of(text);
        var cfg = _config;
        if (_partialJob is { Failed: false } current && current.Key == key)
            return;
        _lastPartialStart = now;

        if (cfg.Options.CacheEnabled && _cache.TryGet(CacheKey(cfg, key), out var cached))
        {
            CancelPartial();
            _partialJob = Job.FromCache(text, key, cached, cfg);
            _partialTranslation = cached;
            EmitPartial();
            return;
        }

        // Partials are best-effort: never queue behind finished sentences, just retry next interval.
        if (!_slots.Wait(0))
        {
            _pendingPartial = text;
            return;
        }
        CancelPartial();
        var job = NewJob(text, key, null, cfg, now);
        _partialJob = job;
        Launch(job, slotHeld: true);
        EmitPartial();
    }

    private void CancelPartial()
    {
        _partialJob?.Abandon();
        _partialJob = null;
    }

    /// <summary>Very long unpunctuated speech: keep only the tail, cut at a clause boundary.</summary>
    private static string Tail(string text, int maxChars)
    {
        if (text.Length <= maxChars)
            return text;
        int cut = text.Length - maxChars;
        int boundary = text.IndexOfAny([',', '，', '、', ' '], cut);
        return boundary >= 0 && boundary < text.Length - 1 ? text[(boundary + 1)..].Trim() : text[cut..].Trim();
    }

    // ───────────────────────────── request jobs ─────────────────────────────

    private Job NewJob(string text, string key, Entry? entry, EngineConfig cfg, long now) => new()
    {
        Text = text,
        Key = key,
        Entry = entry,
        Config = cfg,
        Slots = _slots,
        Cts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token),
        Context = cfg.Options.ContextSentences > 0
            ? _context.Skip(Math.Max(0, _context.Count - cfg.Options.ContextSentences)).ToArray()
            : [],
        CreatedAt = now,
    };

    private void Launch(Job job, bool slotHeld)
    {
        Interlocked.Exchange(ref _lastActivity, job.CreatedAt);
        _ = Task.Run(() => ExecuteAsync(job, slotHeld));
    }

    private async Task ExecuteAsync(Job job, bool slotHeld)
    {
        var ct = job.Cts!.Token;
        try
        {
            if (!slotHeld)
            {
                await job.Slots!.WaitAsync(ct).ConfigureAwait(false);
                slotHeld = true;
            }
            var cfg = job.Config;
            var request = new TranslationRequest(job.Text, cfg.Target, job.Context, cfg.SystemPrompt);
            var filter = new ThinkTagFilter();
            await foreach (var chunk in cfg.Provider.TranslateStreamAsync(request, ct).ConfigureAwait(false))
            {
                var visible = filter.Push(chunk);
                if (visible.Length > 0)
                {
                    lock (_gate)
                        OnOutput(job, visible);
                }
            }
            var tail = filter.Flush();
            lock (_gate)
            {
                if (tail.Length > 0)
                    OnOutput(job, tail);
                OnCompleted(job);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Superseded by newer text or shutting down.
        }
        catch (Exception ex)
        {
            lock (_gate)
                OnFailed(job, ex);
        }
        finally
        {
            if (slotHeld)
                job.Slots!.Release();
            lock (_gate)
                job.Cts.Dispose(); // under the lock so it never races Abandon()
            Interlocked.Exchange(ref _lastActivity, _time.GetTimestamp());
        }
    }

    private void OnOutput(Job job, string text)
    {
        if (job.Abandoned)
            return;
        job.Buffer.Append(text);
        job.HasOutput = true;
        var now = _time.GetTimestamp();
        if (job.Entry is { } e)
        {
            if (e.Job != job)
                return;
            ApplyToEntry(e, job, now);
            Emit(e);
        }
        else if (job == _partialJob)
        {
            _partialTranslation = CleanStreaming(job.Buffer.ToString());
            EmitPartial();
        }
    }

    private void OnCompleted(Job job)
    {
        if (job.Abandoned)
            return;
        job.Completed = true;
        var now = _time.GetTimestamp();
        if (job.Entry is { } e)
        {
            if (e.Job == job)
                FinishEntry(e, job, now);
        }
        else if (job == _partialJob)
        {
            _partialTranslation = CleanFinal(job.Buffer.ToString());
            if (job.Config.Options.CacheEnabled && _partialTranslation.Length > 0)
                _cache.Set(CacheKey(job.Config, job.Key), _partialTranslation);
            EmitPartial();
        }
    }

    private void OnFailed(Job job, Exception ex)
    {
        if (job.Abandoned)
            return;
        job.Failed = true;
        var message = ex is ProviderException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
        if (job.Entry is { } e)
        {
            if (e.Job != job)
                return;
            e.Job = null;
            e.Status = EntryStatus.Error;
            e.Error = message;
            Emit(e);
        }
        else if (job == _partialJob)
        {
            EmitPartial();
        }
        Error?.Invoke(message);
    }

    private void ApplyToEntry(Entry e, Job job, long now)
    {
        e.Translation = CleanStreaming(job.Buffer.ToString());
        e.Status = EntryStatus.Streaming;
        e.Provisional = false;
        e.FirstTokenMs ??= Ms(e.StartedAt, now);
    }

    private void FinishEntry(Entry e, Job job, long now)
    {
        var text = CleanFinal(job.Buffer.ToString());
        e.Job = null;
        e.Translation = text;
        e.Status = EntryStatus.Done;
        e.Provisional = false;
        e.FirstTokenMs ??= Ms(e.StartedAt, now);
        e.TotalMs = Ms(e.StartedAt, now);
        if (text.Length > 0)
        {
            if (job.Config.Options.CacheEnabled)
                _cache.Set(CacheKey(job.Config, e.Key), text);
            AddContext(e.Source, text);
        }
        Emit(e);
    }

    private void AddContext(string source, string translation)
    {
        _context.Add(new ContextPair(source, translation));
        if (_context.Count > MaxContextPairs)
            _context.RemoveAt(0);
    }

    // ─────────────────────────────── helpers ───────────────────────────────

    private void Emit(Entry e)
    {
        if (!e.Hidden)
            EntryChanged?.Invoke(e.Snapshot());
    }

    private void EmitPartial() =>
        PartialChanged?.Invoke(new PartialSnapshot(
            _partialSource,
            _partialTranslation,
            _partialJob is { Completed: false, Failed: false, Abandoned: false }));

    private int Ms(long from, long to) => (int)Math.Round(_time.GetElapsedTime(from, to).TotalMilliseconds);

    private static string CacheKey(EngineConfig cfg, string key) => $"{cfg.CacheScope}\u001f{cfg.Target.Code}\u001f{key}";

    private static string CleanStreaming(string text) => text.Replace("\r", "").Replace('\n', ' ').TrimStart();

    private static string CleanFinal(string text) => CleanStreaming(text).Trim();

    private sealed class Entry
    {
        public long Id;
        public string Source = "";
        public string Key = "";
        public bool Hidden;
        public string Translation = "";
        public EntryStatus Status = EntryStatus.Pending;
        public bool Provisional;
        public string? Error;
        public int? FirstTokenMs;
        public int? TotalMs;
        public long ChangedAt;
        public long SeenAt;
        public long StartedAt;
        public int Revisions;
        public Job? Job;

        public EntrySnapshot Snapshot() => new(Id, Source, Translation, Status, Provisional, FirstTokenMs, TotalMs, Error);
    }

    private sealed class Job
    {
        public required string Text;
        public required string Key;
        public required EngineConfig Config;
        public Entry? Entry;
        public SemaphoreSlim? Slots;
        public CancellationTokenSource? Cts;
        public IReadOnlyList<ContextPair> Context = [];
        public long CreatedAt;
        public readonly StringBuilder Buffer = new();
        public bool HasOutput;
        public bool Completed;
        public bool Failed;
        public bool Abandoned;

        public static Job FromCache(string text, string key, string translation, EngineConfig cfg)
        {
            var job = new Job { Text = text, Key = key, Config = cfg, HasOutput = true, Completed = true };
            job.Buffer.Append(translation);
            return job;
        }

        public void Abandon()
        {
            Abandoned = true;
            try
            {
                Cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already finished.
            }
        }
    }
}
