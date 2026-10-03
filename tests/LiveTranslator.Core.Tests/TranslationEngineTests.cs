using System.Diagnostics;

using LiveTranslator.Core.Models;
using LiveTranslator.Core.Pipeline;
using LiveTranslator.Core.Providers;

namespace LiveTranslator.Core.Tests;

public sealed class TranslationEngineTests : IAsyncDisposable
{
    private readonly List<EntrySnapshot> _entries = [];
    private readonly List<PartialSnapshot> _partials = [];
    private readonly List<string> _errors = [];
    private TranslationEngine? _engine;

    private TranslationEngine Create(ITranslationProvider provider, Action<PipelineOptions>? configure = null)
    {
        var options = new PipelineOptions { PartialIntervalMs = 10, PartialMinChars = 1, ContextSentences = 2 };
        configure?.Invoke(options);
        _engine = new TranslationEngine(new EngineConfig(provider, "scope", Languages.Get("zh-CN"), "SYS", options));
        _engine.EntryChanged += s => { lock (_entries) _entries.Add(s); };
        _engine.PartialChanged += s => { lock (_partials) _partials.Add(s); };
        _engine.Error += m => { lock (_errors) _errors.Add(m); };
        return _engine;
    }

    private EntrySnapshot? Latest(long id)
    {
        lock (_entries)
            return _entries.LastOrDefault(e => e.Id == id);
    }

    private long[] EntryIds()
    {
        lock (_entries)
            return _entries.Select(e => e.Id).Distinct().ToArray();
    }

    private bool IsDone(long id) => Latest(id)?.Status == EntryStatus.Done;

    public async ValueTask DisposeAsync()
    {
        if (_engine is not null)
            await _engine.DisposeAsync();
    }

    [Fact]
    public async Task Final_translation_streams_token_by_token()
    {
        var provider = FakeProvider.Echo(tokenMs: 30);
        var engine = Create(provider, o => o.PartialTranslation = false);

        engine.Submit("Hello");
        engine.Submit("Hello world.");
        await TestData.WaitUntil(() => EntryIds().Length == 1 && IsDone(EntryIds()[0]));

        var id = EntryIds()[0];
        List<EntrySnapshot> history;
        lock (_entries)
            history = _entries.Where(e => e.Id == id).ToList();
        Assert.Contains(history, e => e.Status == EntryStatus.Streaming && e.Translation.Length > 0); // visible before the end
        var final = history[^1];
        Assert.Equal("Hello world.", final.Source);
        Assert.Equal("T(Hello world. )", final.Translation);
        Assert.NotNull(final.FirstTokenMs);
        Assert.True(final.TotalMs >= final.FirstTokenMs);
        Assert.Equal(["Hello world."], provider.Calls);
    }

    [Fact]
    public async Task In_flight_partial_is_promoted_instead_of_sending_another_request()
    {
        var provider = FakeProvider.Echo(firstTokenMs: 250);
        var engine = Create(provider);

        engine.Submit("Good");
        await TestData.WaitUntil(() => provider.CallCount == 1);
        engine.Submit("Good morning");
        await TestData.WaitUntil(() => provider.CallCount == 2);
        engine.Submit("Good morning.");

        await TestData.WaitUntil(() => EntryIds().Length == 1 && IsDone(EntryIds()[0]));
        Assert.Equal(["Good", "Good morning"], provider.Calls);
        Assert.Equal("T(Good morning )", Latest(EntryIds()[0])!.Translation);
    }

    [Fact]
    public async Task Finished_partial_makes_the_final_translation_instant()
    {
        var provider = FakeProvider.Echo();
        var engine = Create(provider);

        engine.Submit("Good morning");
        await TestData.WaitUntil(() =>
        {
            lock (_partials)
                return _partials.Any(p => p.Translation == "T(Good morning )" && !p.IsTranslating);
        });
        engine.Submit("Good morning.");

        await TestData.WaitUntil(() => EntryIds().Length == 1 && IsDone(EntryIds()[0]));
        var entry = Latest(EntryIds()[0])!;
        Assert.Equal(1, provider.CallCount);
        Assert.Equal("T(Good morning )", entry.Translation);
        Assert.True(entry.TotalMs < 50, $"took {entry.TotalMs} ms");
    }

    [Fact]
    public async Task Repeated_snapshots_and_punctuation_changes_do_not_retranslate()
    {
        var provider = FakeProvider.Echo();
        var engine = Create(provider, o => o.PartialTranslation = false);

        engine.Submit("First sentence. Sec");
        await TestData.WaitUntil(() => EntryIds().Length == 1 && IsDone(EntryIds()[0]));
        engine.Submit("First sentence. Second one");
        engine.Submit("First sentence! Second one");
        await TestData.WaitUntil(() => Latest(EntryIds()[0])!.Source == "First sentence!");
        await Task.Delay(50);

        Assert.Equal(["First sentence."], provider.Calls);
        Assert.Single(EntryIds());
    }

    [Fact]
    public async Task Recognition_revision_retranslates_the_same_entry()
    {
        var provider = FakeProvider.Echo();
        var engine = Create(provider, o => o.PartialTranslation = false);

        engine.Submit("Hello.");
        await TestData.WaitUntil(() => EntryIds().Length == 1 && IsDone(EntryIds()[0]));
        engine.Submit("Hello, world.");
        await TestData.WaitUntil(() => Latest(1)?.Translation == "T(Hello, world. )");

        Assert.Equal(["Hello.", "Hello, world."], provider.Calls);
        Assert.Single(EntryIds());
        lock (_entries)
            Assert.Contains(_entries, e => e.IsProvisional && e.Translation == "T(Hello. )"); // old text kept while waiting
    }

    [Fact]
    public async Task Text_already_on_screen_at_startup_is_not_translated_except_the_latest_sentence()
    {
        var provider = FakeProvider.Echo();
        var engine = Create(provider, o => o.PartialTranslation = false);

        engine.Submit("Old one. Old two. Current.");
        await TestData.WaitUntil(() => EntryIds().Length == 1 && IsDone(EntryIds()[0]));
        await Task.Delay(50);

        Assert.Equal(["Current."], provider.Calls);
    }

    [Fact]
    public async Task Distinct_sentences_sharing_a_prefix_are_both_translated()
    {
        var provider = FakeProvider.Echo();
        var engine = Create(provider, o => o.PartialTranslation = false);

        engine.Submit("Start");
        // Snapshots are latest-wins; make sure "Start" was processed before the next one arrives.
        await TestData.WaitUntil(() => { lock (_partials) return _partials.Any(p => p.Source == "Start"); });
        engine.Submit("Yes. Yes, I think so.");
        await TestData.WaitUntil(() => EntryIds().Length == 2 && EntryIds().All(IsDone));

        Assert.Equal(new[] { "Yes.", "Yes, I think so." }.Order(StringComparer.Ordinal), provider.Calls.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Cache_answers_repeated_text_without_a_request()
    {
        var provider = FakeProvider.Echo(firstTokenMs: 20);
        var engine = Create(provider);

        engine.TranslateText("Thank you very much");
        await TestData.WaitUntil(() => EntryIds().Length == 1 && IsDone(EntryIds()[0]));
        engine.TranslateText("thank you, very much!");
        await TestData.WaitUntil(() => EntryIds().Length == 2 && IsDone(EntryIds()[1]));

        Assert.Equal(1, provider.CallCount);
        Assert.Equal("T(Thank you very much )", Latest(EntryIds()[1])!.Translation);
    }

    [Fact]
    public async Task Context_of_finished_sentences_is_sent_with_later_requests()
    {
        var requests = new List<TranslationRequest>();
        var provider = new FakeProvider((r, ct) =>
        {
            lock (requests)
                requests.Add(r);
            return FakeProvider.EchoAsync(r.Text, 0, 0, ct);
        });
        var engine = Create(provider, o => o.PartialTranslation = false);

        engine.TranslateText("One");
        await TestData.WaitUntil(() => EntryIds().Length == 1 && IsDone(EntryIds()[0]));
        engine.TranslateText("Two");
        await TestData.WaitUntil(() => EntryIds().Length == 2 && IsDone(EntryIds()[1]));

        Assert.Empty(requests[0].Context);
        var pair = Assert.Single(requests[1].Context);
        Assert.Equal(new ContextPair("One", "T(One )"), pair);
        Assert.Equal("SYS", requests[1].SystemPrompt);
    }

    [Fact]
    public async Task Provider_failure_marks_entry_as_error_and_raises_event()
    {
        var provider = new FakeProvider((_, _) => Fail());
        var engine = Create(provider, o => o.PartialTranslation = false);

        engine.TranslateText("Hello");
        await TestData.WaitUntil(() => Latest(1)?.Status == EntryStatus.Error);

        Assert.Equal("HTTP 401（鉴权失败）", Latest(1)!.Error);
        lock (_errors)
            Assert.Contains("HTTP 401（鉴权失败）", _errors);

        static async IAsyncEnumerable<string> Fail()
        {
            await Task.Yield();
            throw new ProviderException("HTTP 401（鉴权失败）");
#pragma warning disable CS0162 // iterator needs a yield to be an iterator
            yield break;
#pragma warning restore CS0162
        }
    }

    [Fact]
    public async Task Finished_sentences_are_translated_concurrently()
    {
        var provider = FakeProvider.Echo(firstTokenMs: 300);
        var engine = Create(provider, o => o.PartialTranslation = false);

        var sw = Stopwatch.StartNew();
        engine.TranslateText("Alpha");
        engine.TranslateText("Beta");
        engine.TranslateText("Gamma");
        await TestData.WaitUntil(() => EntryIds().Length == 3 && EntryIds().All(IsDone));

        Assert.True(sw.ElapsedMilliseconds < 800, $"took {sw.ElapsedMilliseconds} ms; serial would be ≥ 900 ms");
    }

    [Fact]
    public async Task Partial_translation_shows_while_the_sentence_is_still_being_spoken()
    {
        var provider = FakeProvider.Echo();
        var engine = Create(provider);

        engine.Submit("We are going to");
        await TestData.WaitUntil(() =>
        {
            lock (_partials)
                return _partials.Any(p => p.Source == "We are going to" && p.Translation == "T(We are going to )");
        });
        Assert.Empty(EntryIds());
    }
}
