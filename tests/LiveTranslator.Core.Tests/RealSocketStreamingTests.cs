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

        var options = new PipelineOptions { PartialIntervalMs = 350, PartialMinChars = 6 };
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

    /// <summary>Minimal HTTP/1.1 server speaking OpenAI-style SSE with chunked encoding.</summary>
    private sealed class LoopbackLlmServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;
        private int _requests;

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
                    _ = Task.Run(() => ServeAsync(client, tokens, ttftMs, tokenMs));
                }
            });
        }

        public string BaseUrl { get; }
        public int RequestCount => Volatile.Read(ref _requests);

        public static LoopbackLlmServer Start(string[] tokens, int ttftMs, int tokenMs) => new(tokens, ttftMs, tokenMs);

        private async Task ServeAsync(TcpClient client, string[] tokens, int ttftMs, int tokenMs)
        {
            using var _ = client;
            var stream = client.GetStream();
            try
            {
                while (await ReadRequestAsync(stream))
                {
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

        private static async Task<bool> ReadRequestAsync(NetworkStream stream)
        {
            var header = new StringBuilder();
            var one = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await stream.ReadAsync(one) == 0)
                    return false;
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
                    return false;
                remaining -= n;
            }
            return true;
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
