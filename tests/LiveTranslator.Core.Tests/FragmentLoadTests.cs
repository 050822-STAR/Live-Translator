using System.Diagnostics;
using System.Runtime.CompilerServices;

using LiveTranslator.Core.Models;
using LiveTranslator.Core.Pipeline;
using LiveTranslator.Core.Providers;

using Xunit.Abstractions;

namespace LiveTranslator.Core.Tests;

/// <summary>
/// Simulated speech against a model whose time to first token grows with the input, comparing
/// fragment translation with re-translating the growing sentence: load on the service, and how long
/// after a sentence ends its translation is complete.
/// </summary>
public sealed class FragmentLoadTests
{
    private static readonly string[] Sentences =
    [
        "そのランクをやるためにまず50勝しなきゃいけないんだけど、それが終わってなくて、まだやってないんだよね。",
        "昨日の配信でも言ったけど、今週はちょっと忙しいから、配信はお休みにします。",
    ];

    private readonly ITestOutputHelper _output;

    public FragmentLoadTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Fragments_cut_service_load_and_finish_sentences_sooner()
    {
        var whole = await SimulateAsync(fragments: false);
        var split = await SimulateAsync(fragments: true);

        _output.WriteLine($"整句模式：{whole}");
        _output.WriteLine($"分片模式：{split}");
        Assert.True(split.InputChars < whole.InputChars * 0.7, $"fragments sent {split.InputChars} chars vs {whole.InputChars}");
        // Two sentences only, and the suite runs in parallel: allow scheduling noise, but never slower.
        Assert.True(split.MedianFinishMs <= whole.MedianFinishMs + 50, $"fragments finished in {split.MedianFinishMs} ms vs {whole.MedianFinishMs} ms");
    }

    private sealed record Result(int Requests, int InputChars, int MedianFinishMs)
    {
        public override string ToString() => $"{Requests} 次请求，共发送 {InputChars} 字，句末到整句译完中位 {MedianFinishMs} ms";
    }

    private static async Task<Result> SimulateAsync(bool fragments)
    {
        int requests = 0, inputChars = 0;
        // 150 ms to the first token plus 4 ms per input character, then one token per 2 characters.
        var provider = new FakeProvider((r, ct) =>
        {
            Interlocked.Increment(ref requests);
            Interlocked.Add(ref inputChars, r.Text.Length);
            return Model(r.Text, ct);
        });
        var options = new PipelineOptions { FragmentTranslation = fragments, ContextSentences = 0, CacheEnabled = false };
        await using var engine = new TranslationEngine(new EngineConfig(provider, "scope", Languages.Get("zh-CN"), "SYS", options));
        var clock = Stopwatch.StartNew();
        var doneAt = new Dictionary<long, long>();
        var entries = new Dictionary<long, EntrySnapshot>();
        engine.EntryChanged += s =>
        {
            lock (doneAt)
            {
                entries[s.Id] = s;
                if (s.Status == EntryStatus.Done)
                    doneAt[s.Id] = clock.ElapsedMilliseconds;
            }
        };

        var finishMs = new List<int>();
        var spoken = "";
        foreach (var sentence in Sentences)
        {
            // Captions grow two characters at a time, about 16 characters a second.
            for (int i = 2; i < sentence.Length; i += 2)
            {
                engine.Submit(spoken + sentence[..i]);
                await Task.Delay(120);
            }
            engine.Submit(spoken + sentence);
            var endedAt = clock.ElapsedMilliseconds;
            spoken += sentence;

            // Finished when every line produced so far is done.
            await TestData.WaitUntil(() =>
            {
                lock (doneAt)
                    return entries.Count > 0 && entries.Values.All(e => e.Status == EntryStatus.Done) &&
                           entries.Values.Any(e => e.Source.EndsWith('。') && doneAt.ContainsKey(e.Id) && doneAt[e.Id] >= endedAt - 5000);
            }, 10_000);
            lock (doneAt)
                finishMs.Add((int)(doneAt.Values.Max() - endedAt));
            await Task.Delay(300);
        }
        finishMs.Sort();
        return new Result(requests, inputChars, finishMs[finishMs.Count / 2]);
    }

    private static async IAsyncEnumerable<string> Model(string text, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(150 + 4 * text.Length, ct);
        for (int i = 0; i < text.Length; i += 2)
        {
            await Task.Delay(15, ct);
            yield return text.Substring(i, Math.Min(2, text.Length - i));
        }
    }
}
