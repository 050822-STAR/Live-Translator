using System.Diagnostics;
using System.Runtime.CompilerServices;

using LiveTranslator.Core.Models;
using LiveTranslator.Core.Pipeline;
using LiveTranslator.Core.Providers;

namespace LiveTranslator.Core.Tests;

/// <summary>
/// Behaviour during continuous speech: the translation must keep up word by word instead of
/// waiting for the sentence to end, and the visible line must grow rather than flicker.
/// </summary>
public sealed class InteractiveLatencyTests : IAsyncDisposable
{
    private readonly List<(long At, EntrySnapshot S)> _entries = [];
    private readonly List<(long At, PartialSnapshot S)> _partials = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TranslationEngine? _engine;

    private TranslationEngine Create(ITranslationProvider provider, Action<PipelineOptions>? configure = null)
    {
        var options = new PipelineOptions { PartialIntervalMs = 50, PartialMinChars = 1, ContextSentences = 0 };
        configure?.Invoke(options);
        _engine = new TranslationEngine(new EngineConfig(provider, "scope", Languages.Get("zh-CN"), "SYS", options));
        _engine.EntryChanged += s => { lock (_entries) _entries.Add((_clock.ElapsedMilliseconds, s)); };
        _engine.PartialChanged += s => { lock (_partials) _partials.Add((_clock.ElapsedMilliseconds, s)); };
        return _engine;
    }

    public async ValueTask DisposeAsync()
    {
        if (_engine is not null)
            await _engine.DisposeAsync();
    }

    private static readonly string[] Words =
        "one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen".Split(' ');

    /// <summary>Feeds the words one by one, like captions growing while someone talks.</summary>
    private async Task<long> SpeakAsync(TranslationEngine engine, int wordGapMs)
    {
        for (int i = 1; i <= Words.Length; i++)
        {
            engine.Submit(string.Join(' ', Words.Take(i)));
            await Task.Delay(wordGapMs);
        }
        return _clock.ElapsedMilliseconds;
    }

    [Fact]
    public async Task Partial_translation_keeps_updating_while_speech_continues_even_when_the_model_is_slower_than_the_words()
    {
        // Model needs 300 ms to the first token; a new word arrives every 120 ms.
        var engine = Create(FakeProvider.Echo(firstTokenMs: 300));

        var speechEnded = await SpeakAsync(engine, wordGapMs: 120);

        string[] shownWhileSpeaking;
        lock (_partials)
            shownWhileSpeaking = _partials.Where(p => p.At < speechEnded && p.S.Translation.Length > 0)
                                          .Select(p => p.S.Translation).Distinct().ToArray();
        Assert.True(shownWhileSpeaking.Length >= 3,
            $"only {shownWhileSpeaking.Length} partial translations appeared during {speechEnded} ms of speech");
    }

    [Fact]
    public async Task Visible_partial_translation_grows_instead_of_collapsing_and_retyping()
    {
        // Each request streams its answer slowly, so a newer request's first tokens are much
        // shorter than the text already on screen.
        var engine = Create(FakeProvider.Echo(firstTokenMs: 80, tokenMs: 40));

        await SpeakAsync(engine, wordGapMs: 150);
        await Task.Delay(1500);

        List<string> shown;
        lock (_partials)
            shown = _partials.Select(p => p.S.Translation).Where(t => t.Length > 0).ToList();
        Assert.NotEmpty(shown);
        for (int i = 1; i < shown.Count; i++)
        {
            Assert.True(shown[i].Length >= shown[i - 1].Length,
                $"line shrank from \"{shown[i - 1]}\" to \"{shown[i]}\"");
        }
    }

    [Fact]
    public async Task Finished_sentence_does_not_queue_behind_speculative_requests()
    {
        // Partial requests are slow; the finished sentence must start at once anyway.
        var provider = new FakeProvider((r, ct) => r.Text.EndsWith('.')
            ? FakeProvider.EchoAsync(r.Text, 0, 0, ct)
            : FakeProvider.EchoAsync(r.Text, 3000, 0, ct));
        var engine = Create(provider, o => o.MaxConcurrentRequests = 1);

        engine.Submit("Alpha beta");
        await TestData.WaitUntil(() => provider.CallCount == 1);
        var sentAt = _clock.ElapsedMilliseconds;
        engine.TranslateText("Hello there.");

        await TestData.WaitUntil(() => { lock (_entries) return _entries.Any(e => e.S.Status == EntryStatus.Done); }, 5000);
        long doneAt;
        lock (_entries)
            doneAt = _entries.First(e => e.S.Status == EntryStatus.Done).At;
        Assert.True(doneAt - sentAt < 1000, $"finished sentence waited {doneAt - sentAt} ms behind a speculative request");
    }

    [Fact]
    public async Task Final_translation_does_not_replace_the_placeholder_with_a_shorter_stream()
    {
        // The partial translation of the whole sentence is on screen as a placeholder; the final
        // request then streams slowly. The line must not drop back to its first few tokens.
        var provider = new FakeProvider((r, ct) => r.Text.EndsWith('.')
            ? FakeProvider.EchoAsync(r.Text, 50, 60, ct)
            : FakeProvider.EchoAsync(r.Text, 0, 0, ct));
        var engine = Create(provider);

        engine.Submit("We are going to talk about");
        await TestData.WaitUntil(() => { lock (_partials) return _partials.Any(p => p.S.Translation.Length > 0 && !p.S.IsTranslating); });
        engine.Submit("We are going to talk about latency.");
        await TestData.WaitUntil(() => { lock (_entries) return _entries.Any(e => e.S.Status == EntryStatus.Done); }, 5000);

        List<string> lines;
        lock (_entries)
            lines = _entries.Select(e => e.S.Translation).Where(t => t.Length > 0).ToList();
        for (int i = 1; i < lines.Count; i++)
            Assert.True(lines[i].Length >= lines[i - 1].Length, $"entry shrank from \"{lines[i - 1]}\" to \"{lines[i]}\"");
        Assert.Equal("T(We are going to talk about latency. )", lines[^1]);
    }
}

public sealed class SpeculationLimitAndChunkingTests : IAsyncDisposable
{
    private readonly List<EntrySnapshot> _entries = [];
    private TranslationEngine? _engine;

    private TranslationEngine Create(ITranslationProvider provider, Action<PipelineOptions> configure)
    {
        var options = new PipelineOptions { PartialIntervalMs = 10, PartialMinChars = 1, ContextSentences = 0 };
        configure(options);
        _engine = new TranslationEngine(new EngineConfig(provider, "scope", Languages.Get("zh-CN"), "SYS", options));
        _engine.EntryChanged += s => { lock (_entries) _entries.Add(s); };
        return _engine;
    }

    public async ValueTask DisposeAsync()
    {
        if (_engine is not null)
            await _engine.DisposeAsync();
    }

    private string[] Sources()
    {
        lock (_entries)
            return _entries.GroupBy(e => e.Id).Select(g => g.Last().Source).ToArray();
    }

    private bool AllDone()
    {
        lock (_entries)
            return _entries.Count > 0 && _entries.GroupBy(e => e.Id).All(g => g.Last().Status == EntryStatus.Done);
    }

    private static async Task SpeakWordsAsync(TranslationEngine engine, string sentence, int gapMs = 15)
    {
        var words = sentence.Split(' ');
        for (int i = 1; i <= words.Length; i++)
        {
            engine.Submit(string.Join(' ', words.Take(i)));
            await Task.Delay(gapMs);
        }
    }

    [Fact]
    public async Task Speculative_requests_in_flight_never_exceed_the_limit()
    {
        var counter = new ConcurrencyCounter();
        var provider = new FakeProvider((r, ct) => FakeProvider.EchoAsync(r.Text, 400, 0, ct).TrackConcurrency(counter, ct));
        var engine = Create(provider, o => o.PartialMaxInFlight = 2);

        await SpeakWordsAsync(engine, "a b c d e f g h i j k l m n o p q r s t u v w x y z", gapMs: 30);
        await Task.Delay(500);

        Assert.True(counter.Max <= 2, $"{counter.Max} speculative requests ran at once");
        Assert.True(provider.CallCount >= 2, "speculation should overlap, not run one at a time");
    }

    [Fact]
    public async Task Settled_text_is_requested_at_once_even_when_the_pipeline_is_full()
    {
        // The model is slow and only one speculative request may run. When the speaker pauses,
        // the request for the settled text (which the final translation will reuse) must not wait
        // for older, now-outdated requests to finish.
        var provider = FakeProvider.Echo(firstTokenMs: 1500);
        var engine = Create(provider, o => o.PartialMaxInFlight = 1);

        await SpeakWordsAsync(engine, "alpha beta gamma delta", gapMs: 100);
        var pausedAt = Stopwatch.StartNew();
        await TestData.WaitUntil(() => provider.Calls.Contains("alpha beta gamma delta"), 1000,
            "request for the settled text");

        Assert.True(pausedAt.ElapsedMilliseconds < 500, $"settled text waited {pausedAt.ElapsedMilliseconds} ms for a free slot");
    }

    [Fact]
    public async Task Speculation_and_finished_sentences_can_use_different_providers()
    {
        // Lets the app race a backup only for finished sentences, keeping speculation cheap.
        var final = FakeProvider.Echo(name: "final");
        var speculative = FakeProvider.Echo(name: "speculative");
        _engine = new TranslationEngine(new EngineConfig(final, "scope", Languages.Get("zh-CN"), "SYS",
            new PipelineOptions { PartialIntervalMs = 10, PartialMinChars = 1, ContextSentences = 0 }, speculative));
        _engine.EntryChanged += s => { lock (_entries) _entries.Add(s); };

        _engine.Submit("Good morning");
        await TestData.WaitUntil(() => speculative.CallCount == 1);
        _engine.Submit("Good morning. See you");
        await TestData.WaitUntil(() => AllDone() && Sources().Length == 1);
        _engine.TranslateText("Thank you.");
        await TestData.WaitUntil(() => AllDone() && Sources().Length == 2);

        Assert.All(speculative.Calls, text => Assert.DoesNotContain(".", text)); // unfinished text only
        Assert.Equal(["Thank you."], final.Calls); // "Good morning." reused the speculative result
    }

    [Fact]
    public async Task Steady_speech_never_preempts_requests_before_they_answer()
    {
        // Ordinary gaps between words (200 ms) are not a pause: the single running request must be
        // allowed to finish, or the caption would starve again.
        var partials = new List<string>();
        var provider = FakeProvider.Echo(firstTokenMs: 600);
        var engine = Create(provider, o => o.PartialMaxInFlight = 1);
        engine.PartialChanged += s => { lock (partials) partials.Add(s.Translation); };

        await SpeakWordsAsync(engine, "one two three four five six seven eight nine ten eleven twelve", gapMs: 200);

        lock (partials)
            Assert.Contains(partials, t => t.Length > 0);
    }

    [Fact]
    public async Task Long_unpunctuated_speech_is_committed_clause_by_clause_without_duplicates()
    {
        var provider = FakeProvider.Echo();
        var engine = Create(provider, o =>
        {
            o.PartialTranslation = false;
            o.LongSentenceSplitChars = 30;
        });

        const string sentence = "we went to the market this morning, and then we visited the old library downtown, and finally we had lunch";
        await SpeakWordsAsync(engine, sentence);
        // Clauses are translated before the speaker reaches the end of the sentence.
        await TestData.WaitUntil(() => Sources().Length == 2 && AllDone());
        engine.Submit(sentence + ".");
        await TestData.WaitUntil(() => Sources().Length == 3 && AllDone());
        engine.Submit(sentence + ". Next");
        await Task.Delay(100);

        Assert.Equal(
            ["we went to the market this morning,", "and then we visited the old library downtown,", "and finally we had lunch."],
            Sources());
        Assert.Equal(Sources(), provider.Calls.ToArray()); // nothing translated twice, never the whole sentence
    }

    [Fact]
    public async Task Committed_clause_survives_a_recognition_fix_inside_it()
    {
        var provider = FakeProvider.Echo();
        var engine = Create(provider, o =>
        {
            o.PartialTranslation = false;
            o.LongSentenceSplitChars = 30;
        });

        await SpeakWordsAsync(engine, "we went to the market this morning, and then we visited");
        await TestData.WaitUntil(() => Sources().Length == 1 && AllDone());
        // LiveCaptions rewrites a word inside the committed clause, then the sentence ends.
        engine.Submit("we went to the market this mourning, and then we visited the old library.");
        await TestData.WaitUntil(() => Sources().Length == 2 && AllDone());

        Assert.Equal(["we went to the market this morning,", "and then we visited the old library."], Sources());
    }

    [Fact]
    public async Task Cjk_speech_without_punctuation_is_still_split()
    {
        var provider = FakeProvider.Echo();
        var engine = Create(provider, o =>
        {
            o.PartialTranslation = false;
            o.LongSentenceSplitChars = 20;
        });

        // 40 CJK characters (80 weighted) with no punctuation at all.
        var text = string.Concat(Enumerable.Repeat("今天我们讨论一下项目进度", 4))[..40];
        for (int i = 1; i <= text.Length; i++)
            engine.Submit(text[..i]);
        await TestData.WaitUntil(() => Sources().Length >= 1 && AllDone());

        Assert.All(Sources(), s => Assert.True(TextKey.WeightedLength(s) <= 40, $"chunk too long: {s}"));
    }
}

internal static class AsyncEnumerableTestExtensions
{
    /// <summary>Wraps a stream and reports how many of these streams are running at once.</summary>
    public static async IAsyncEnumerable<string> TrackConcurrency(
        this IAsyncEnumerable<string> source, ConcurrencyCounter counter, [EnumeratorCancellation] CancellationToken ct = default)
    {
        counter.Enter();
        try
        {
            await foreach (var item in source.WithCancellation(ct))
                yield return item;
        }
        finally
        {
            counter.Exit();
        }
    }
}

internal sealed class ConcurrencyCounter
{
    private int _current;
    private int _max;

    public int Max => Volatile.Read(ref _max);

    public void Enter()
    {
        var now = Interlocked.Increment(ref _current);
        int seen;
        while (now > (seen = Volatile.Read(ref _max)) && Interlocked.CompareExchange(ref _max, now, seen) != seen)
        {
        }
    }

    public void Exit() => Interlocked.Decrement(ref _current);
}
