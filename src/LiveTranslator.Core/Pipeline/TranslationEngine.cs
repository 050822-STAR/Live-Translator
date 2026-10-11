using System.Text;
using System.Threading.Channels;

using LiveTranslator.Core.Models;
using LiveTranslator.Core.Providers;

namespace LiveTranslator.Core.Pipeline;

public enum EntryStatus { Pending, Streaming, Done, Error }

/// <param name="IsProvisional">Translation shown is a placeholder (e.g. the partial translation) until the final one streams in.</param>
/// <param name="FirstTokenMs">From sentence completion to the first token of its own translation.</param>
/// <param name="TotalMs">From sentence completion to the finished translation.</param>
/// <param name="Group">
/// Entries of one sentence that was translated fragment by fragment share a group (the id of its
/// first fragment); everything else is a group of its own. A group is shown as one line.
/// </param>
/// <param name="Trace">Timing of the request that produced the translation, once it has finished.</param>
public sealed record EntrySnapshot(
    long Id,
    string Source,
    string Translation,
    EntryStatus Status,
    bool IsProvisional,
    int? FirstTokenMs,
    int? TotalMs,
    string? Error,
    long Group = 0,
    TraceSnapshot? Trace = null);

/// <summary>The sentence currently being spoken.</summary>
/// <param name="Group">Group of the finished fragments this text continues, or 0 when it starts a new sentence.</param>
public sealed record PartialSnapshot(string Source, string Translation, bool IsTranslating, long Group = 0);

/// <param name="Provider">Translates finished sentences (may be a hedged pair to cut tail latency).</param>
/// <param name="CacheScope">Identifies provider + model + prompt so cached results never cross configurations.</param>
/// <param name="SpeculativeProvider">
/// Translates unfinished text; defaults to <paramref name="Provider"/>. Speculation is frequent and
/// self-correcting, so racing a backup for it would mostly double the cost.
/// </param>
public sealed record EngineConfig(
    ITranslationProvider Provider,
    string CacheScope,
    LanguageInfo Target,
    string SystemPrompt,
    PipelineOptions Options,
    ITranslationProvider? SpeculativeProvider = null);

/// <summary>
/// Turns a stream of caption snapshots into translated sentences with minimal latency.
/// </summary>
/// <remarks>
/// Latency strategy:
/// <list type="bullet">
/// <item>Event-driven: a snapshot is processed the moment it arrives (no polling sleeps).</item>
/// <item>The unfinished sentence is translated speculatively as a pipeline: a new request starts
/// every interval while older ones keep running, and whichever newer request catches up takes over
/// the display. Requests are never cancelled just because newer words arrived — with a model slower
/// than the speaker that would starve the line until the speaker pauses.</item>
/// <item>When the sentence completes with the same words, that speculative request is promoted to
/// the final translation instead of starting a new one.</item>
/// <item>Visible text only grows: a newer stream replaces what is on screen once it has caught up,
/// so the line never collapses back to its first token and re-types itself.</item>
/// <item>Fragment translation: each clause is committed and translated once, as soon as the speaker
/// has moved past it, so every request carries only a few words; speculation covers only the clause
/// still being spoken. Long unpunctuated speech is cut at word gaps.</item>
/// <item>Finished sentences have their own concurrency budget and never queue behind speculation.</item>
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

    // Dropping a running request for the settled text needs a real pause, not an ordinary gap
    // between words — otherwise steady speech would cancel every request before its first token.
    private const double PreemptMinMs = 300, PreemptMaxMs = 1000;

    // A clause is only split off once the speaker is this far past it (weighted characters), so
    // LiveCaptions has stopped rewriting its last words.
    private const int ChunkSettledTail = 8;

    // A taken-back sentence must be at least this long (letters) to be recognised at the start of new
    // text; shorter ones ("はい") too easily begin an unrelated sentence. It must also be followed by at
    // least this much new text: a word or two more is a recognition fix, re-translated in place instead.
    private const int MinRetractedKey = 4;
    private const int MinAdoptedTail = 6;

    private static readonly char[] ClauseBreaks = [',', '，', '、', ';', '；', ':', '：'];
    private static readonly char[] LeadingJunk = [' ', ',', '，', '、', ';', '；', ':', '：', '.', '。'];

    private readonly object _gate = new();
    private readonly Channel<string> _snapshots = Channel.CreateBounded<string>(new BoundedChannelOptions(1)
    {
        // Only the newest caption matters; intermediate ones are superseded.
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });
    private readonly SemaphoreSlim _wake = new(0, 1);
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
    private readonly List<Job> _partials = []; // speculative requests for the current utterance, oldest first
    private Job? _shownPartial;                 // request whose output is on screen
    private long _partialSeq;
    private string? _pendingPartial;
    private long _lastPartialStart;
    private long _lastPartialChange;
    private double _changeGapMs;                // smoothed time between caption updates (speaking cadence)
    private string _chunkKey = "";              // already-committed clauses of the unfinished sentence
    private long _chunkGroup;                   // display group of those clauses

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
                CancelPartials();
                _partialTranslation = "";
                _pendingPartial = WantsPartialTranslation(_partialSource, _partialKey, config.Options) ? _partialSource : null;
                EmitPartial();
            }
        }
        SignalWake();
    }

    /// <summary>Forgets caption tracking (e.g. after pausing). Running sentence translations still finish.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            CancelPartials();
            _tracked.Clear();
            _primed = false;
            _lastText = "";
            _chunkKey = "";
            _chunkGroup = 0;
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
            CancelPartials();
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

                if (due is { } overdue && overdue <= TimeSpan.Zero)
                {
                    lock (_gate)
                        FirePendingPartial(_time.GetTimestamp());
                    continue;
                }

                // Wake on: a new caption, the next partial being due, or a speculative request
                // finishing (which frees pipeline capacity for the pending text).
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var readable = reader.WaitToReadAsync(wait.Token).AsTask();
                var woken = _wake.WaitAsync(wait.Token);
                var first = due is { } delay
                    ? await Task.WhenAny(readable, woken, Task.Delay(delay, _time, wait.Token)).ConfigureAwait(false)
                    : await Task.WhenAny(readable, woken).ConfigureAwait(false);
                wait.Cancel();
                ct.ThrowIfCancellationRequested();
                if (first != readable)
                    continue;
                if (!await readable.ConfigureAwait(false))
                    return;

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

    private void SignalWake()
    {
        try
        {
            if (_wake.CurrentCount == 0)
                _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled.
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

            if (_chunkKey.Length == 0)
                AdoptRetractedSentence(TextKey.Of(segmentation.Partial), segmentation.Complete);
            var partial = StripCommittedChunks(segmentation.Partial);
            partial = SplitLongPartial(partial, now);
            UpdatePartial(partial, now);
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
            else if ((_chunkKey.Length > 0 || AdoptRetractedSentence(key, complete)) && TryStripChunkPrefix(sentence, out var tail))
            {
                // The long sentence we were committing clause by clause has ended: its earlier
                // clauses are already translated, only the tail is new. The whole sentence is
                // remembered (hidden) so later snapshots of it are recognised as seen.
                var group = _chunkGroup;
                _chunkKey = "";
                _chunkGroup = 0;
                var whole = Track(sentence, key, hidden: true, now);
                whole.Group = group; // lets this sentence be recognised again if its full stop is taken back
                consumed.Add(whole);
                var tailKey = TextKey.Of(tail);
                if (tailKey.Length > 0)
                {
                    var entry = Track(tail, tailKey, hidden: false, now);
                    entry.Group = group;
                    consumed.Add(entry);
                    StartFinal(entry, now);
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

    /// <summary>
    /// LiveCaptions sometimes takes back a finished sentence's full stop and runs it into the next one
    /// ("…寝た。" becomes "…寝た昨日は…"). The finished sentence then reappears at the start of the new
    /// text: it is adopted as an already-translated fragment of that sentence, so it is not sent again
    /// and the continuation joins its line. A sentence still on screen with its own full stop is not
    /// taken back; a new sentence that merely starts the same way is translated whole. A sentence that
    /// only grew by a word or two is left to <see cref="Revise"/>, which re-translates it as a whole.
    /// </summary>
    private bool AdoptRetractedSentence(string key, IReadOnlyList<string> complete)
    {
        if (key.Length == 0)
            return false;
        HashSet<string>? finished = null;
        for (int j = _tracked.Count - 1; j >= Math.Max(0, _tracked.Count - CandidateSentences); j--)
        {
            var e = _tracked[j];
            if (e.Key.Length < MinRetractedKey || key.Length - e.Key.Length < MinAdoptedTail ||
                !key.StartsWith(e.Key, StringComparison.Ordinal))
                continue;
            finished ??= complete.Select(TextKey.Of).ToHashSet();
            if (finished.Contains(e.Key))
                continue;
            _chunkKey = e.Key;
            _chunkGroup = e.Group;
            return true;
        }
        return false;
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
        e.Provisional = e.Translation.Length > 0; // keep showing the old translation until the new one catches up
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
        e.Group = e.Id;
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

        // Fast path 1: a speculative request already covers exactly these words.
        if (_partials.LastOrDefault(j => j.Key == e.Key) is { } partial)
        {
            _partials.Remove(partial);
            if (_shownPartial == partial)
                _shownPartial = null;
            SignalWake();
            partial.Entry = e;
            e.Job = partial;
            if (partial.Completed)
            {
                FinishEntry(e, partial, now);
                return;
            }
            SeedFromPartial(e);
            if (partial.HasOutput)
            {
                e.FirstTokenMs ??= Ms(e.StartedAt, now);
                ApplyToEntry(e, partial);
            }
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
        job.Slots = _slots;
        e.Job = job;
        Emit(e);
        Launch(job);
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

    // ───────────────────────────── long sentences ─────────────────────────────

    /// <summary>Removes the clauses already committed as chunks from the start of the unfinished text.</summary>
    private string StripCommittedChunks(string partial)
    {
        if (_chunkKey.Length == 0)
            return partial;
        if (TryStripChunkPrefix(partial, out var rest))
            return rest;
        // The chunked sentence is no longer on screen (rewritten or scrolled away): start over.
        _chunkKey = "";
        _chunkGroup = 0;
        return partial;
    }

    /// <summary>
    /// Finds where the committed clauses end in <paramref name="text"/>. Tolerates LiveCaptions
    /// rewriting a word or two inside them by aligning on the best-matching prefix length.
    /// </summary>
    private bool TryStripChunkPrefix(string text, out string rest)
    {
        var key = TextKey.Of(text);
        int cutKeyLength;
        if (key.StartsWith(_chunkKey, StringComparison.Ordinal))
        {
            cutKeyLength = _chunkKey.Length;
        }
        else
        {
            int best = -1;
            double bestSimilarity = 0;
            for (int length = Math.Max(1, _chunkKey.Length - 4); length <= Math.Min(key.Length, _chunkKey.Length + 4); length++)
            {
                var similarity = TextKey.Similarity(key[..length], _chunkKey);
                if (similarity > bestSimilarity)
                {
                    bestSimilarity = similarity;
                    best = length;
                }
            }
            if (best < 0 || bestSimilarity < 0.8)
            {
                rest = text;
                return false;
            }
            cutKeyLength = best;
        }

        int i = 0;
        for (int counted = 0; i < text.Length && counted < cutKeyLength; i++)
        {
            if (char.IsLetterOrDigit(text[i]))
                counted++;
        }
        // Never split a word of a space-delimited language in half.
        while (i < text.Length && char.IsLetterOrDigit(text[i]) && !TextNormalizer.IsCjk(text[i]))
            i++;
        rest = text[i..].TrimStart(LeadingJunk);
        return true;
    }

    /// <summary>
    /// Commits settled leading clauses of the unfinished sentence as fragments of their own. With
    /// fragment translation on, every clause goes out once the speaker has moved past it, so each
    /// request carries only a few words and no clause is translated twice; otherwise only very long
    /// sentences are split, to keep their latency flat. Long speech without any punctuation is cut
    /// at a word gap in either mode.
    /// </summary>
    private string SplitLongPartial(string partial, long now)
    {
        var options = _config.Options;
        var limit = options.LongSentenceSplitChars;
        while (true)
        {
            var cut = options.FragmentTranslation ? FindClauseCut(partial, options.FragmentMinChars) : -1;
            if (cut <= 0 && limit > 0 && TextKey.WeightedLength(partial) > limit)
                cut = FindChunkCut(partial, limit);
            if (cut <= 0)
                break;
            var chunk = partial[..cut].Trim();
            var key = TextKey.Of(chunk);
            if (key.Length == 0)
                break;
            var entry = Track(chunk, key, hidden: false, now);
            if (_chunkKey.Length == 0)
                _chunkGroup = entry.Id;
            entry.Group = _chunkGroup;
            StartFinal(entry, now);
            _chunkKey += key;
            partial = partial[cut..].TrimStart(LeadingJunk);
        }
        return partial;
    }

    /// <summary>
    /// The first clause boundary the speaker has clearly moved past (LiveCaptions no longer rewrites
    /// it), once the clause is long enough to translate on its own.
    /// </summary>
    private static int FindClauseCut(string text, int minChars)
    {
        for (int i = 1; i < text.Length; i++)
        {
            if (Array.IndexOf(ClauseBreaks, text[i]) >= 0 &&
                TextKey.WeightedLength(text[..(i + 1)]) >= minChars &&
                TextKey.WeightedLength(text[(i + 1)..]) >= ChunkSettledTail)
                return i + 1;
        }
        return -1;
    }

    private static int FindChunkCut(string text, int limit)
    {
        // Prefer the last clause boundary the speaker has clearly moved past.
        for (int i = text.Length - 1; i > 0; i--)
        {
            if (Array.IndexOf(ClauseBreaks, text[i]) >= 0 &&
                TextKey.WeightedLength(text[(i + 1)..]) >= ChunkSettledTail &&
                TextKey.WeightedLength(text[..(i + 1)]) >= limit / 3)
                return i + 1;
        }

        // No punctuation at all: once far too long, cut near the limit at a word gap (anywhere for CJK).
        if (TextKey.WeightedLength(text) <= limit * 2)
            return -1;
        int weighted = 0, cut = 0;
        for (; cut < text.Length && weighted < limit; cut++)
            weighted += TextNormalizer.IsCjk(text[cut]) ? 2 : char.IsWhiteSpace(text[cut]) ? 0 : 1;
        if (!TextNormalizer.IsCjk(text[Math.Max(0, cut - 1)]))
        {
            var space = text.LastIndexOf(' ', Math.Min(cut, text.Length - 1));
            if (space > 0)
                cut = space;
        }
        return TextKey.WeightedLength(text[cut..]) >= ChunkSettledTail ? cut : -1;
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
            // A different utterance: the old speculative translations are useless now.
            CancelPartials();
            _partialTranslation = "";
        }
        else if (_lastPartialChange != 0)
        {
            var gap = _time.GetElapsedTime(_lastPartialChange, now).TotalMilliseconds;
            if (gap < 2000)
                _changeGapMs = _changeGapMs <= 0 ? gap : _changeGapMs * 0.7 + gap * 0.3;
        }
        _partialSource = partial;
        _partialKey = key;
        _pendingPartial = null;
        _lastPartialChange = now;

        if (WantsPartialTranslation(partial, key, options) && !_partials.Any(j => j.Key == key))
        {
            _pendingPartial = partial;
            if (PartialDueIn(now) is { } due && due <= TimeSpan.Zero)
                FirePendingPartial(now);
        }
        EmitPartial();
    }

    private static bool WantsPartialTranslation(string text, string key, PipelineOptions options) =>
        options.PartialTranslation && key.Length > 0 && TextKey.WeightedLength(text) >= options.PartialMinChars;

    private int RunningPartials => _partials.Count(j => !j.Completed);

    /// <summary>
    /// New words are sent the moment they appear, as long as the previous request started at least
    /// the interval ago (the interval is a minimum spacing, not a timer to wait out): with a word every
    /// ~250 ms that is one request per word, so the translation follows the speaker a few words at a
    /// time. With the pipeline full, only settled text fires (by pre-empting an older request); until
    /// then the loop waits for the pause point or for a running request to finish.
    /// </summary>
    private TimeSpan? PartialDueIn(long now)
    {
        if (_pendingPartial is null)
            return null;
        if (RunningPartials >= _config.Options.PartialMaxInFlight)
            return PreemptAfter - _time.GetElapsedTime(_lastPartialChange, now);
        return TimeSpan.FromMilliseconds(_config.Options.PartialIntervalMs) - _time.GetElapsedTime(_lastPartialStart, now);
    }

    /// <summary>A pause counts as real once it is twice the speaker's usual gap between updates.</summary>
    private TimeSpan PreemptAfter =>
        TimeSpan.FromMilliseconds(Math.Clamp(2 * (_changeGapMs > 0 ? _changeGapMs : 200), PreemptMinMs, PreemptMaxMs));

    /// <summary>
    /// The speaker paused, so this text is very likely what the sentence ends with — the request the
    /// final translation will reuse. Make room for it by dropping the oldest running request
    /// (preferring one whose output is not on screen); its text is already outdated.
    /// </summary>
    private bool PreemptForSettledText(long now)
    {
        if (_time.GetElapsedTime(_lastPartialChange, now) < PreemptAfter)
            return false;
        var victim = _partials.FirstOrDefault(j => !j.Completed && j != _shownPartial)
                     ?? _partials.FirstOrDefault(j => !j.Completed);
        if (victim is null)
            return false;
        Discard(victim);
        return true;
    }

    private void FirePendingPartial(long now)
    {
        var text = _pendingPartial;
        if (text is null)
            return;
        var key = TextKey.Of(text);
        var cfg = _config;
        if (_partials.Any(j => j.Key == key))
        {
            _pendingPartial = null;
            return;
        }
        if (RunningPartials >= cfg.Options.PartialMaxInFlight && !PreemptForSettledText(now))
            return; // stays pending until it settles or a running request finishes
        _pendingPartial = null;
        _lastPartialStart = now;

        if (cfg.Options.CacheEnabled && _cache.TryGet(CacheKey(cfg, key), out var cached))
        {
            var hit = Job.FromCache(text, key, cached, cfg);
            hit.Seq = ++_partialSeq;
            _partials.Add(hit);
            TryShowPartial(hit);
            EmitPartial();
            return;
        }

        var job = NewJob(text, key, null, cfg, now);
        job.Seq = ++_partialSeq;
        _partials.Add(job);
        Launch(job);
        EmitPartial();
    }

    /// <summary>
    /// Puts a speculative request's output on screen if it is the newest one to get there.
    /// A newer request only takes over once it has caught up with the visible text (or finished),
    /// so the line keeps growing instead of collapsing to a first token and re-typing itself.
    /// </summary>
    private bool TryShowPartial(Job job)
    {
        if (_shownPartial is { } shown && shown.Seq > job.Seq)
        {
            Discard(job); // something newer is already on screen
            return false;
        }
        var text = job.Completed ? CleanFinal(job.Buffer.ToString()) : CleanStreaming(job.Buffer.ToString());
        if (_shownPartial != job)
        {
            if (!job.Completed && text.Length < _partialTranslation.Length)
                return false;
            foreach (var older in _partials.Where(p => p.Seq < job.Seq).ToList())
                Discard(older);
            _shownPartial = job;
        }
        _partialTranslation = text;
        return true;
    }

    private void Discard(Job job)
    {
        job.Abandon();
        _partials.Remove(job);
        if (_shownPartial == job)
            _shownPartial = null;
        SignalWake();
    }

    private void CancelPartials()
    {
        foreach (var job in _partials)
            job.Abandon();
        _partials.Clear();
        _shownPartial = null;
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
        Speculative = entry is null,
        Config = cfg,
        Cts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token),
        Context = cfg.Options.ContextSentences > 0
            ? _context.Skip(Math.Max(0, _context.Count - cfg.Options.ContextSentences)).ToArray()
            : [],
        CreatedAt = now,
    };

    private void Launch(Job job)
    {
        Interlocked.Exchange(ref _lastActivity, job.CreatedAt);
        _ = Task.Run(() => ExecuteAsync(job));
    }

    private async Task ExecuteAsync(Job job)
    {
        var ct = job.Cts!.Token;
        var slots = job.Slots; // finished sentences only; speculation is bounded by PartialMaxInFlight
        var slotHeld = false;
        try
        {
            if (slots is not null)
            {
                await slots.WaitAsync(ct).ConfigureAwait(false);
                slotHeld = true;
            }
            var cfg = job.Config;
            var provider = job.Speculative ? cfg.SpeculativeProvider ?? cfg.Provider : cfg.Provider;
            var request = new TranslationRequest(job.Text, cfg.Target, job.Context, cfg.SystemPrompt) { Trace = job.Trace };
            var filter = new ThinkTagFilter();
            await foreach (var chunk in provider.TranslateStreamAsync(request, ct).ConfigureAwait(false))
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
                slots!.Release();
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
        if (job.Entry is { } e)
        {
            if (e.Job != job)
                return;
            e.FirstTokenMs ??= Ms(e.StartedAt, _time.GetTimestamp());
            if (ApplyToEntry(e, job))
                Emit(e);
        }
        else if (_partials.Contains(job) && TryShowPartial(job))
        {
            EmitPartial();
        }
    }

    private void OnCompleted(Job job)
    {
        if (job.Abandoned)
            return;
        job.Completed = true;
        if (job.Entry is { } e)
        {
            if (e.Job == job)
                FinishEntry(e, job, _time.GetTimestamp());
        }
        else if (_partials.Contains(job))
        {
            var text = CleanFinal(job.Buffer.ToString());
            if (text.Length == 0)
            {
                Discard(job); // an empty answer must neither blank the line nor become the final translation
            }
            else
            {
                if (job.Config.Options.CacheEnabled)
                    _cache.Set(CacheKey(job.Config, job.Key), text);
                TryShowPartial(job);
            }
            EmitPartial();
            SignalWake();
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
        else if (_partials.Remove(job))
        {
            if (_shownPartial == job)
                _shownPartial = null;
            EmitPartial();
            SignalWake();
        }
        Error?.Invoke(message);
    }

    /// <summary>Updates the entry unless that would replace a longer placeholder with a shorter stream.</summary>
    private static bool ApplyToEntry(Entry e, Job job)
    {
        var text = CleanStreaming(job.Buffer.ToString());
        if (e.Provisional && text.Length < e.Translation.Length)
            return false;
        e.Translation = text;
        e.Status = EntryStatus.Streaming;
        e.Provisional = false;
        return true;
    }

    private void FinishEntry(Entry e, Job job, long now)
    {
        var text = CleanFinal(job.Buffer.ToString());
        e.Job = null;
        e.Trace = job.Trace.Snapshot();
        e.FirstTokenMs ??= Ms(e.StartedAt, now);
        e.TotalMs = Ms(e.StartedAt, now);
        if (text.Length == 0)
        {
            // The service ended its answer without any text. Say so, with the request's timing, and
            // keep whatever placeholder is on screen instead of finishing the line blank.
            e.Status = EntryStatus.Error;
            e.Error = EmptyAnswerMessage(e.Trace);
            Emit(e);
            Error?.Invoke(e.Error);
            return;
        }
        e.Translation = text;
        e.Status = EntryStatus.Done;
        e.Provisional = false;
        if (job.Config.Options.CacheEnabled)
            _cache.Set(CacheKey(job.Config, e.Key), text);
        AddContext(e.Source, text);
        Emit(e);
    }

    internal static string EmptyAnswerMessage(TraceSnapshot trace)
    {
        var parts = new List<string>();
        if (trace.EndMs is { } end)
            parts.Add($"请求用时 {end} ms");
        if (trace.HeadersMs is { } headers)
            parts.Add($"响应头 {headers} ms");
        parts.Add($"结束原因：{trace.FinishReason ?? "服务未说明"}");
        var message = $"模型没有返回译文（{string.Join("，", parts)}）";
        // Content-free "length" means the whole output budget went into reasoning before any answer.
        if (trace.FinishReason == "length")
            message += "：输出额度在思考阶段就用完了，请换用不带思考的模型，或在额外请求体中关闭思考";
        return message;
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
        PartialChanged?.Invoke(new PartialSnapshot(_partialSource, _partialTranslation, RunningPartials > 0, _chunkKey.Length > 0 ? _chunkGroup : 0));

    private int Ms(long from, long to) => (int)Math.Round(_time.GetElapsedTime(from, to).TotalMilliseconds);

    private static string CacheKey(EngineConfig cfg, string key) => $"{cfg.CacheScope}\u001f{cfg.Target.Code}\u001f{key}";

    private static string CleanStreaming(string text) => text.Replace("\r", "").Replace('\n', ' ').TrimStart();

    private static string CleanFinal(string text) => CleanStreaming(text).Trim();

    private sealed class Entry
    {
        public long Id;
        public long Group;
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
        public TraceSnapshot? Trace;

        public EntrySnapshot Snapshot() => new(Id, Source, Translation, Status, Provisional, FirstTokenMs, TotalMs, Error, Group, Trace);
    }

    private sealed class Job
    {
        public required string Text;
        public required string Key;
        public required EngineConfig Config;
        public Entry? Entry;
        public bool Speculative; // started for unfinished text (stays true if later promoted)
        public SemaphoreSlim? Slots;
        public CancellationTokenSource? Cts;
        public IReadOnlyList<ContextPair> Context = [];
        public long CreatedAt;
        public long Seq;
        public readonly StringBuilder Buffer = new();
        public readonly RequestTrace Trace = new();
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
