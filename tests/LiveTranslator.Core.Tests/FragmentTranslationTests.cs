using LiveTranslator.Core.Models;
using LiveTranslator.Core.Pipeline;
using LiveTranslator.Core.Providers;

namespace LiveTranslator.Core.Tests;

public sealed class FragmentTranslationTests : IAsyncDisposable
{
    private const string Clause = "そのランクをやるためにまず50勝しなきゃいけないんだけど、";
    private const string Rest = "それが終わってなくてやってないんだよね。";

    private readonly List<EntrySnapshot> _entries = [];
    private readonly List<PartialSnapshot> _partials = [];
    private TranslationEngine? _engine;

    private TranslationEngine Create(ITranslationProvider provider, Action<PipelineOptions>? configure = null)
    {
        // Speculation off: only committed fragments are requested, so the calls are deterministic.
        var options = new PipelineOptions { PartialTranslation = false };
        configure?.Invoke(options);
        _engine = new TranslationEngine(new EngineConfig(provider, "scope", Languages.Get("zh-CN"), "SYS", options));
        _engine.EntryChanged += s => { lock (_entries) _entries.Add(s); };
        _engine.PartialChanged += s => { lock (_partials) _partials.Add(s); };
        return _engine;
    }

    private EntrySnapshot[] Latest()
    {
        lock (_entries)
            return _entries.GroupBy(e => e.Id).Select(g => g.Last()).OrderBy(e => e.Id).ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        if (_engine is not null)
            await _engine.DisposeAsync();
    }

    [Fact]
    public async Task Each_clause_is_translated_once_as_soon_as_the_speaker_moves_past_it()
    {
        var provider = FakeProvider.Echo();
        var engine = Create(provider);

        engine.Submit(Clause + "それが終わって");
        await TestData.WaitUntil(() => Latest() is [{ Status: EntryStatus.Done }]);
        Assert.Equal([Clause], provider.Calls); // sent before the sentence ends
        var fragment = Latest()[0];
        PartialSnapshot live;
        lock (_partials)
            live = _partials[^1];
        Assert.Equal("それが終わって", live.Source);
        Assert.Equal(fragment.Group, live.Group); // the unfinished clause continues the same line

        engine.Submit(Clause + Rest);
        await TestData.WaitUntil(() => Latest() is [{ Status: EntryStatus.Done }, { Status: EntryStatus.Done }]);

        Assert.Equal([Clause, Rest], provider.Calls); // the whole sentence is never sent again
        Assert.All(Latest(), e => Assert.Equal(fragment.Id, e.Group));
    }

    [Fact]
    public async Task Clause_too_short_to_stand_alone_waits_for_the_next_one()
    {
        var provider = FakeProvider.Echo();
        var engine = Create(provider);

        engine.Submit("あの、それが終わってなくて");
        await Task.Delay(150);
        Assert.Equal(0, provider.CallCount);

        engine.Submit("あの、それが終わってなくて。");
        await TestData.WaitUntil(() => Latest() is [{ Status: EntryStatus.Done }]);
        Assert.Equal(["あの、それが終わってなくて。"], provider.Calls);
    }

    [Fact]
    public async Task With_fragments_off_a_short_sentence_is_sent_whole()
    {
        var provider = FakeProvider.Echo();
        var engine = Create(provider, o => o.FragmentTranslation = false);

        engine.Submit(Clause + "それが終わって");
        await Task.Delay(150);
        engine.Submit(Clause + Rest);
        await TestData.WaitUntil(() => Latest() is [{ Status: EntryStatus.Done }]);

        Assert.Equal([Clause + Rest], provider.Calls);
    }

    [Fact]
    public async Task English_clauses_are_split_at_commas_too()
    {
        var provider = FakeProvider.Echo();
        var engine = Create(provider);

        engine.Submit("We went to the store, and then we bought");
        await TestData.WaitUntil(() => provider.CallCount == 1);
        engine.Submit("We went to the store, and then we bought some milk.");
        await TestData.WaitUntil(() => Latest() is [{ Status: EntryStatus.Done }, { Status: EntryStatus.Done }]);

        Assert.Equal(["We went to the store,", "and then we bought some milk."], provider.Calls);
    }

    private const string Slept = "ちゃんと休みましたからめっちゃ寝た。";
    private const string Merged = "もうありえんぐらい寝た";

    [Fact]
    public async Task Sentence_whose_full_stop_is_taken_back_is_not_translated_again()
    {
        // LiveCaptions first ends the sentence, then removes the 。 and runs it into the next one.
        var provider = FakeProvider.Echo();
        var engine = Create(provider);
        engine.Submit(Slept);
        await TestData.WaitUntil(() => provider.CallCount == 1);
        engine.Submit(Slept + Merged + "。");
        await TestData.WaitUntil(() => Latest() is [_, { Status: EntryStatus.Done }]);
        var retracted = Latest()[1];

        engine.Submit(Slept + Merged + "昨日はった後に最近、バロなんてやれてないなと");
        await TestData.WaitUntil(() => provider.CallCount == 3);
        engine.Submit(Slept + Merged + "昨日はった後に最近、バロなんてやれてないなと思って。");
        await TestData.WaitUntil(() => provider.CallCount == 4 && Latest().All(e => e.Status == EntryStatus.Done));

        Assert.Equal([Slept, Merged + "。", "昨日はった後に最近、", "バロなんてやれてないなと思って。"], provider.Calls);
        Assert.All(Latest().Skip(1), e => Assert.Equal(retracted.Group, e.Group)); // continues the same line
    }

    [Fact]
    public async Task Sentence_run_into_the_next_in_one_step_is_not_translated_again()
    {
        var provider = FakeProvider.Echo();
        var engine = Create(provider);
        engine.Submit(Slept);
        await TestData.WaitUntil(() => provider.CallCount == 1);
        engine.Submit(Slept + Merged + "。");
        await TestData.WaitUntil(() => Latest() is [_, { Status: EntryStatus.Done }]);
        var retracted = Latest()[1];

        engine.Submit(Slept + Merged + "昨日はゲームした。");
        await TestData.WaitUntil(() => provider.CallCount == 3 && Latest().All(e => e.Status == EntryStatus.Done));

        Assert.Equal("昨日はゲームした。", provider.Calls[^1]);
        Assert.Equal(retracted.Group, Latest()[^1].Group);
    }

    [Fact]
    public async Task New_sentence_that_merely_starts_like_the_previous_one_is_translated_whole()
    {
        var provider = FakeProvider.Echo();
        var engine = Create(provider);
        engine.Submit("それでいいと思う。");
        await TestData.WaitUntil(() => provider.CallCount == 1);

        // The earlier sentence is still on screen with its full stop: nothing was taken back.
        engine.Submit("それでいいと思う。それでいいと思うけど難しいね。");
        await TestData.WaitUntil(() => provider.CallCount == 2);

        Assert.Equal("それでいいと思うけど難しいね。", provider.Calls[^1]);
    }

    [Fact]
    public async Task Empty_answer_is_reported_instead_of_finishing_the_line_blank()
    {
        var provider = new FakeProvider((_, _) => Empty());
        var engine = Create(provider);

        engine.TranslateText("Hello.");
        await TestData.WaitUntil(() => Latest() is [{ Status: EntryStatus.Error }]);

        var entry = Latest()[0];
        Assert.Contains("模型没有返回译文", entry.Error);
        Assert.Contains("结束原因", entry.Error);
        Assert.DoesNotContain(Latest(), e => e.Status == EntryStatus.Done);
    }

    [Fact]
    public void Empty_answer_message_names_timing_and_reason()
    {
        var message = TranslationEngine.EmptyAnswerMessage(new TraceSnapshot(120, null, null, 2063, 0, "length"));

        Assert.Equal("模型没有返回译文（请求用时 2063 ms，响应头 120 ms，结束原因：length）：输出额度在思考阶段就用完了，请换用不带思考的模型，或在额外请求体中关闭思考", message);
        Assert.Equal("模型没有返回译文（结束原因：content_filter）", TranslationEngine.EmptyAnswerMessage(new TraceSnapshot(null, null, null, null, 0, "content_filter")));
    }

    [Fact]
    public void Line_joins_fragments_and_the_live_clause()
    {
        var line = new SentenceLine(5);
        line.Apply(Snapshot(5, Clause, "为了打那个排位，首先必须赢50场才行，不过，"));
        line.Apply(Snapshot(6, "それが終わって", "", EntryStatus.Pending));

        Assert.Equal(Clause + "それが終わって", line.Source);
        Assert.Equal("为了打那个排位，首先必须赢50场才行，不过，…", line.Translation);
        Assert.True(line.IsWorking);

        line.Apply(Snapshot(6, Rest, "那个还没结束，所以还没做呢。"));
        line.SetLive("次は", "下次");
        Assert.Equal(Clause + Rest + "次は", line.Source);
        Assert.Equal("为了打那个排位，首先必须赢50场才行，不过，那个还没结束，所以还没做呢。下次", line.Translation);
        Assert.Equal(6, line.Latest!.Id);

        line.SetLive("", "");
        Assert.False(line.HasLive);
        Assert.False(line.IsWorking);
        Assert.EndsWith("所以还没做呢。", line.Translation);
    }

    [Fact]
    public void Line_reports_a_fragments_error()
    {
        var line = new SentenceLine(1);
        line.Apply(Snapshot(1, "a,", "甲，"));
        line.Apply(Snapshot(2, "b", "", EntryStatus.Error) with { Error = "模型没有返回译文" });

        Assert.Equal("模型没有返回译文", line.Error);
    }

    [Theory]
    [InlineData(new[] { "为了打那个排位，", "那个还没结束" }, "为了打那个排位，那个还没结束")]
    [InlineData(new[] { "We went to the store,", "and then" }, "We went to the store, and then")]
    [InlineData(new[] { " Hello ", "", "world" }, "Hello world")]
    [InlineData(new[] { "大家要保密", "OK" }, "大家要保密OK")]
    public void Join_spaces_only_between_words_of_space_delimited_languages(string[] pieces, string expected) =>
        Assert.Equal(expected, SentenceLine.Join(pieces));

    private static EntrySnapshot Snapshot(long id, string source, string translation, EntryStatus status = EntryStatus.Done) =>
        new(id, source, translation, status, false, 100, 200, null, Group: 5);

    private static async IAsyncEnumerable<string> Empty()
    {
        await Task.Yield();
        yield break;
    }
}
