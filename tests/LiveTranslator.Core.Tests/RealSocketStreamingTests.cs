using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

using LiveTranslator.Core.Http;
using LiveTranslator.Core.Models;
using LiveTranslator.Core.Pipeline;
using LiveTranslator.Core.Providers;

using Xunit.Abstractions;

namespace LiveTranslator.Core.Tests;

/// <summary>
/// Exercises the production HTTP stack (SocketsHttpHandler) against a loopback server that
/// behaves like an LLM: a time-to-first-token pause, then one token every <c>tokenMs</c>.
/// </summary>
public sealed class RealSocketStreamingTests
{
    private readonly ITestOutputHelper _output;

    public RealSocketStreamingTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Tokens_reach_the_caller_while_the_server_is_still_generating()
    {
        const int ttftMs = 300, tokenMs = 60;
        var tokens = Enumerable.Range(1, 10).Select(i => $"词{i}").ToArray();
        await using var server = LoopbackLlmServer.Start(tokens, ttftMs, tokenMs);
        var profile = TestData.Profile(ProviderProtocol.OpenAI, server.BaseUrl);
        var provider = new OpenAICompatibleProvider(profile, TranslatorHttpClient.Create("none"));

        var sw = Stopwatch.StartNew();
        long firstTokenAt = -1;
        var received = new List<string>();
        await foreach (var chunk in provider.TranslateStreamAsync(TestData.Request("hello")))
        {
            if (firstTokenAt < 0)
                firstTokenAt = sw.ElapsedMilliseconds;
            received.Add(chunk);
        }
        var totalAt = sw.ElapsedMilliseconds;

        _output.WriteLine($"first token {firstTokenAt} ms, complete {totalAt} ms (server TTFT {ttftMs} ms, {tokens.Length}×{tokenMs} ms tokens)");
        Assert.Equal(tokens, received);
        // A buffered (non-streaming) client could not show anything before the whole answer exists.
        Assert.True(firstTokenAt < totalAt - (tokens.Length - 2) * tokenMs,
            $"first token at {firstTokenAt} ms is not ahead of completion at {totalAt} ms");
    }

    [Fact]
    public async Task End_to_end_latency_report()
    {
        // Simulated model: 300 ms to first token, 12 tokens at 40 ms.
        const int ttftMs = 300, tokenMs = 40;
        var tokens = Enumerable.Range(1, 12).Select(i => $"t{i} ").ToArray();
        await using var server = LoopbackLlmServer.Start(tokens, ttftMs, tokenMs);
        var http = TranslatorHttpClient.Create("none");
        var provider = new OpenAICompatibleProvider(TestData.Profile(ProviderProtocol.OpenAI, server.BaseUrl), http);
        // Baseline: what a non-streaming client waits for one complete answer from this server
        // (measured, because timer granularity makes the real server slower than nominal).
        var baseline = Stopwatch.StartNew();
        await TestData.Collect(provider.TranslateStreamAsync(TestData.Request("warm-up and baseline")));
        var fullRequestMs = baseline.ElapsedMilliseconds;

        var options = new PipelineOptions { PartialMinChars = 6 }; // shipped defaults
        await using var engine = new TranslationEngine(new EngineConfig(provider, "bench", Languages.Get("zh-CN"), "SYS", options));

        var entries = new List<(long At, EntrySnapshot S)>();
        var partials = new List<(long At, PartialSnapshot S)>();
        var sw = Stopwatch.StartNew();
        engine.EntryChanged += s => { lock (entries) entries.Add((sw.ElapsedMilliseconds, s)); };
        engine.PartialChanged += s => { lock (partials) partials.Add((sw.ElapsedMilliseconds, s)); };

        // Speech arrives word by word, ~250 ms apart; LiveCaptions adds the period after a short
        // pause (400 ms here), by which time the speculative request for the full text is in flight.
        var words = "So today we are going to talk about latency".Split(' ');
        for (int i = 1; i <= words.Length; i++)
        {
            engine.Submit(string.Join(' ', words.Take(i)));
            await Task.Delay(250);
        }
        await Task.Delay(150);
        var sentenceEndAt = sw.ElapsedMilliseconds;
        engine.Submit(string.Join(' ', words) + ".");

        await TestData.WaitUntil(() => { lock (entries) return entries.Any(e => e.S.Status == EntryStatus.Done); }, 10_000);

        long firstPartialTranslationAt;
        lock (partials)
            firstPartialTranslationAt = partials.First(p => p.S.Translation.Length > 0).At;
        EntrySnapshot done;
        long doneAt;
        lock (entries)
            (doneAt, done) = entries.First(e => e.S.Status == EntryStatus.Done);

        _output.WriteLine($"server requests: {server.RequestCount} (1 baseline + speculative partials)");
        _output.WriteLine($"speech start → first visible partial translation: {firstPartialTranslationAt} ms");
        _output.WriteLine($"sentence end → final translation complete: {doneAt - sentenceEndAt} ms " +
                          $"(engine-reported first token {done.FirstTokenMs} ms, total {done.TotalMs} ms)");
        _output.WriteLine($"baseline: one complete non-streaming request takes {fullRequestMs} ms; a wait-for-period, " +
                          "non-streaming pipeline cannot show the final translation sooner than that after the period");

        // Promotion means the final answer arrives faster than a fresh full request after the period.
        Assert.True(doneAt - sentenceEndAt < fullRequestMs,
            $"final took {doneAt - sentenceEndAt} ms after sentence end; a full request takes {fullRequestMs} ms");
    }

    [Fact]
    public async Task Word_level_latency_report()
    {
        // How long after a word appears in the captions does its translation show up? Simulates a fast
        // non-streaming service like Google's free endpoint (≈ 200–280 ms per request) and a speaker
        // producing a word every 220 ms. The fake translation is tagged with the last word it covers.
        const int words = 30, wordGapMs = 220;
        var random = new Random(7);
        var provider = new FakeProvider((r, ct) => TaggedAsync(r.Text, 200 + random.Next(80), ct));
        var options = new PipelineOptions { ContextSentences = 0 };
        await using var engine = new TranslationEngine(new EngineConfig(provider, "bench", Languages.Get("zh-CN"), "SYS", options));

        var sw = Stopwatch.StartNew();
        var shownAt = new long?[words + 1];   // first time a translation covering word k was visible
        engine.PartialChanged += s =>
        {
            var match = System.Text.RegularExpressions.Regex.Match(s.Translation, @"^\[(\d+)\]");
            if (!match.Success)
                return;
            var covered = int.Parse(match.Groups[1].Value);
            lock (shownAt)
            {
                for (int k = 1; k <= covered && k <= words; k++)
                    shownAt[k] ??= sw.ElapsedMilliseconds;
            }
        };

        var spokenAt = new long[words + 1];
        for (int k = 1; k <= words; k++)
        {
            spokenAt[k] = sw.ElapsedMilliseconds;
            engine.Submit(string.Join(' ', Enumerable.Range(1, k).Select(i => $"w{i}")));
            await Task.Delay(wordGapMs);
        }
        await Task.Delay(1000);

        List<long> lags;
        lock (shownAt)
            lags = Enumerable.Range(2, words - 1).Select(k => (shownAt[k] ?? sw.ElapsedMilliseconds) - spokenAt[k]).Order().ToList();
        var median = lags[lags.Count / 2];
        var p90 = lags[(int)Math.Ceiling(lags.Count * 0.9) - 1];
        _output.WriteLine($"word → visible translation: median {median} ms, 90% ≤ {p90} ms, worst {lags[^1]} ms " +
                          $"(service ≈ 200–280 ms; {provider.CallCount} requests for {words} words)");

        Assert.True(median < 900, $"median word latency {median} ms");

        static async IAsyncEnumerable<string> TaggedAsync(string text, int delayMs, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Delay(delayMs, ct);
            var last = System.Text.RegularExpressions.Regex.Matches(text, @"w(\d+)").Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(0).Max();
            yield return $"[{last}] {text}";
        }
    }

    [Fact]
    public async Task Continuous_speech_latency_report()
    {
        // A cloud-like model (600 ms to first token, 30 ms per token) and a speaker producing a word
        // every 200 ms without pausing — the case where captions used to lag a whole sentence behind.
        const int ttftMs = 600, tokenMs = 30, wordGapMs = 200;
        var tokens = Enumerable.Range(1, 8).Select(i => $"w{i} ").ToArray();
        await using var server = LoopbackLlmServer.Start(tokens, ttftMs, tokenMs);
        var provider = new OpenAICompatibleProvider(TestData.Profile(ProviderProtocol.OpenAI, server.BaseUrl), TranslatorHttpClient.Create("none"));
        await provider.WarmUpAsync();
        var options = new PipelineOptions { PartialMinChars = 6, ContextSentences = 0 }; // shipped defaults
        await using var engine = new TranslationEngine(new EngineConfig(provider, "bench", Languages.Get("zh-CN"), "SYS", options));

        var shown = new List<(long At, string Text)>();
        var sw = Stopwatch.StartNew();
        engine.PartialChanged += s =>
        {
            lock (shown)
            {
                if (s.Translation.Length > 0 && (shown.Count == 0 || shown[^1].Text != s.Translation))
                    shown.Add((sw.ElapsedMilliseconds, s.Translation));
            }
        };

        var words = "today I want to walk you through how the new pipeline keeps captions moving while people talk".Split(' ');
        for (int i = 1; i <= words.Length; i++)
        {
            engine.Submit(string.Join(' ', words.Take(i)));
            await Task.Delay(wordGapMs);
        }
        var speechEnded = sw.ElapsedMilliseconds;

        List<long> during;
        lock (shown)
            during = shown.Where(s => s.At <= speechEnded).Select(s => s.At).ToList();
        var gaps = during.Zip(during.Skip(1), (a, b) => b - a).ToList();
        var maxGap = during.Count == 0 ? speechEnded : Math.Max(gaps.DefaultIfEmpty(0).Max(), speechEnded - during[^1]);

        _output.WriteLine($"speech: {words.Length} words over {speechEnded} ms; model TTFT {ttftMs} ms; server requests {server.RequestCount}");
        _output.WriteLine($"visible caption updates while speaking: {during.Count}");
        _output.WriteLine($"first visible translation: {(during.Count > 0 ? during[0] + " ms" : "none before the speaker stopped")}");
        _output.WriteLine($"longest time the caption stood still while speaking: {maxGap} ms");

        Assert.True(during.Count >= 5, $"only {during.Count} caption updates in {speechEnded} ms of speech");
        Assert.True(maxGap < 2000, $"caption stood still for {maxGap} ms while the speaker kept talking");
    }

    [Fact]
    public async Task Warm_up_opens_enough_connections_for_overlapping_requests()
    {
        // Speculative requests overlap. On HTTP/1.1 every concurrent request needs its own connection,
        // so warming only one would leave the others to pay for a fresh TCP/TLS handshake mid-speech.
        const int parallel = 4;
        await using var server = LoopbackLlmServer.Start(["ok"], ttftMs: 200, tokenMs: 0);
        var provider = new OpenAICompatibleProvider(TestData.Profile(ProviderProtocol.OpenAI, server.BaseUrl), TranslatorHttpClient.Create("none"));

        await provider.WarmUpAsync(parallel);
        var afterWarmUp = server.ConnectionCount;
        await Task.WhenAll(Enumerable.Range(0, parallel).Select(_ => TestData.Collect(provider.TranslateStreamAsync(TestData.Request("x")))));

        _output.WriteLine($"connections after warm-up: {afterWarmUp}, after {parallel} concurrent requests: {server.ConnectionCount}");
        Assert.Equal(parallel, afterWarmUp);
        Assert.Equal(afterWarmUp, server.ConnectionCount); // every request found a ready connection
    }

    [Fact]
    public async Task Every_request_identifies_the_app()
    {
        // Google's free endpoint treats HTTP/2 requests without a User-Agent as bots (429 "Sorry").
        await using var server = LoopbackLlmServer.Start(["ok"], ttftMs: 0, tokenMs: 0);
        var provider = new OpenAICompatibleProvider(TestData.Profile(ProviderProtocol.OpenAI, server.BaseUrl), TranslatorHttpClient.Create("none"));

        await TestData.Collect(provider.TranslateStreamAsync(TestData.Request()));

        Assert.Equal(TranslatorHttpClient.UserAgent, server.LastUserAgent);
        Assert.StartsWith("LiveTranslator/", server.LastUserAgent);
    }

    [Fact]
    public async Task Streams_uncompressed_and_trace_separates_network_from_model_time()
    {
        // The server sends its headers at once, then "thinks" for 300 ms: network vs model time.
        const int ttftMs = 300, tokenMs = 40;
        var tokens = Enumerable.Range(1, 5).Select(i => $"t{i}").ToArray();
        await using var server = LoopbackLlmServer.Start(tokens, ttftMs, tokenMs);
        var provider = new OpenAICompatibleProvider(TestData.Profile(ProviderProtocol.OpenAI, server.BaseUrl), TranslatorHttpClient.Create("none"));
        var trace = new RequestTrace();

        await TestData.Collect(provider.TranslateStreamAsync(TestData.Request("hello") with { Trace = trace }));

        var t = trace.Snapshot();
        _output.WriteLine($"headers {t.HeadersMs} ms, first {t.FirstChunkMs} ms, last {t.LastChunkMs} ms, end {t.EndMs} ms, {t.Chunks} chunks");
        // No Accept-Encoding: a compressed stream can be held back by a CDN or proxy until a block fills.
        Assert.Null(server.LastHeader("Accept-Encoding"));
        Assert.Contains("text/event-stream", server.LastHeader("Accept"));
        Assert.True(t.HeadersMs < ttftMs - 100, $"headers at {t.HeadersMs} ms");
        Assert.True(t.FirstChunkMs >= ttftMs - 20, $"first chunk at {t.FirstChunkMs} ms");
        Assert.True(t.LastChunkMs - t.FirstChunkMs >= (tokens.Length - 2) * tokenMs, "chunks should arrive spread out, as streamed");
        Assert.Equal(tokens.Length, t.Chunks);
        Assert.True(t.EndMs >= t.LastChunkMs);
    }

    /// <summary>Minimal HTTP/1.1 server speaking OpenAI-style SSE with chunked encoding.</summary>
    private sealed class LoopbackLlmServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;
        private int _requests;
        private int _connections;
        private string? _lastUserAgent;
        private string _lastHeaders = "";

        private LoopbackLlmServer(string[] tokens, int ttftMs, int tokenMs)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1";
            _acceptLoop = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    Interlocked.Increment(ref _connections);
                    _ = Task.Run(() => ServeAsync(client, tokens, ttftMs, tokenMs));
                }
            });
        }

        public string BaseUrl { get; }
        public int RequestCount => Volatile.Read(ref _requests);

        /// <summary>TCP connections accepted so far; a reused pooled connection does not add one.</summary>
        public int ConnectionCount => Volatile.Read(ref _connections);

        /// <summary>User-Agent header of the most recent request (null when it had none).</summary>
        public string? LastUserAgent => Volatile.Read(ref _lastUserAgent);

        /// <summary>A header of the most recent request (null when it had none).</summary>
        public string? LastHeader(string name) => Volatile.Read(ref _lastHeaders).Split("\r\n")
            .FirstOrDefault(l => l.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))?[(name.Length + 1)..].Trim();

        public static LoopbackLlmServer Start(string[] tokens, int ttftMs, int tokenMs) => new(tokens, ttftMs, tokenMs);

        private async Task ServeAsync(TcpClient client, string[] tokens, int ttftMs, int tokenMs)
        {
            using var _ = client;
            var stream = client.GetStream();
            try
            {
                while (await ReadRequestAsync(stream) is { } method)
                {
                    if (method == "HEAD")
                    {
                        // Warm-up probe: answer like an API root would, keeping the connection open. The
                        // pause stands in for a network round trip; on loopback an instant reply would let
                        // one probe reuse another's connection, which never happens across a real network.
                        await Task.Delay(50, _cts.Token);
                        await WriteAsync(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n");
                        continue;
                    }
                    Interlocked.Increment(ref _requests);
                    await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\n\r\n");
                    await Task.Delay(ttftMs, _cts.Token);
                    for (int i = 0; i < tokens.Length; i++)
                    {
                        if (i > 0)
                            await Task.Delay(tokenMs, _cts.Token);
                        var escaped = tokens[i].Replace("\\", "\\\\").Replace("\"", "\\\"");
                        await WriteChunkAsync(stream, $"data: {{\"choices\":[{{\"delta\":{{\"content\":\"{escaped}\"}}}}]}}\n\n");
                    }
                    await WriteChunkAsync(stream, "data: [DONE]\n\n");
                    await WriteAsync(stream, "0\r\n\r\n");
                }
            }
            catch (Exception)
            {
                // Client cancelled (superseded partial) or server stopping.
            }
        }

        /// <summary>Reads one request; returns its method, or null when the client closed the connection.</summary>
        private async Task<string?> ReadRequestAsync(NetworkStream stream)
        {
            var header = new StringBuilder();
            var one = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await stream.ReadAsync(one) == 0)
                    return null;
                header.Append((char)one[0]);
            }
            var lengthLine = header.ToString().Split("\r\n")
                .FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            var remaining = lengthLine is null ? 0 : int.Parse(lengthLine["Content-Length:".Length..].Trim());
            var buffer = new byte[4096];
            while (remaining > 0)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)));
                if (n == 0)
                    return null;
                remaining -= n;
            }
            var userAgent = header.ToString().Split("\r\n")
                .FirstOrDefault(l => l.StartsWith("User-Agent:", StringComparison.OrdinalIgnoreCase));
            Volatile.Write(ref _lastUserAgent, userAgent?["User-Agent:".Length..].Trim());
            Volatile.Write(ref _lastHeaders, header.ToString());
            return header.ToString().Split(' ', 2)[0];
        }

        private static Task WriteChunkAsync(NetworkStream stream, string payload)
        {
            var bytes = Encoding.UTF8.GetByteCount(payload);
            return WriteAsync(stream, $"{bytes:X}\r\n{payload}\r\n");
        }

        private static async Task WriteAsync(NetworkStream stream, string text)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(text));
            await stream.FlushAsync();
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                await _acceptLoop;
            }
            catch (Exception)
            {
            }
        }
    }
}
