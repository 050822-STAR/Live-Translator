using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

using LiveTranslator.Core.Models;
using LiveTranslator.Core.Providers;

namespace LiveTranslator.Core.Tests;

/// <summary>Captures the outgoing request and answers with a scripted response.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request, body));
        return _respond(request);
    }

    public static HttpResponseMessage Sse(params string[] dataPayloads)
    {
        var sb = new StringBuilder();
        foreach (var d in dataPayloads)
            sb.Append("data: ").Append(d).Append("\n\n");
        return Text(sb.ToString(), "text/event-stream");
    }

    public static HttpResponseMessage Text(string body, string mediaType, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, mediaType),
    };

    public static HttpResponseMessage Streaming(Stream stream, string mediaType = "text/event-stream")
    {
        var content = new StreamContent(stream);
        content.Headers.ContentType = new(mediaType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
}

/// <summary>A response body whose bytes the test releases one chunk at a time.</summary>
internal sealed class ChunkedStream : Stream
{
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
    private byte[] _current = [];
    private int _offset;

    public void Push(string text) => _chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(text));
    public void Complete() => _chunks.Writer.TryComplete();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_offset >= _current.Length)
        {
            if (!await _chunks.Reader.WaitToReadAsync(ct) || !_chunks.Reader.TryRead(out var next))
                return 0;
            _current = next;
            _offset = 0;
        }
        int n = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsMemory(_offset, n).CopyTo(buffer);
        _offset += n;
        return n;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Scriptable provider for engine tests.</summary>
internal sealed class FakeProvider : ITranslationProvider
{
    private readonly Func<TranslationRequest, CancellationToken, IAsyncEnumerable<string>> _script;

    public FakeProvider(Func<TranslationRequest, CancellationToken, IAsyncEnumerable<string>> script, string name = "fake")
    {
        _script = script;
        Name = name;
    }

    public string Name { get; }
    public List<string> Calls { get; } = [];
    public int CallCount { get { lock (Calls) return Calls.Count; } }

    public IAsyncEnumerable<string> TranslateStreamAsync(TranslationRequest request, CancellationToken ct = default)
    {
        lock (Calls)
            Calls.Add(request.Text);
        return _script(request, ct);
    }

    public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>Echoes "T(text)" split into a few tokens, with a delay before the first and between tokens.</summary>
    public static FakeProvider Echo(int firstTokenMs = 0, int tokenMs = 0, string name = "fake") =>
        new((r, ct) => EchoAsync(r.Text, firstTokenMs, tokenMs, ct), name);

    public static async IAsyncEnumerable<string> EchoAsync(string text, int firstTokenMs, int tokenMs, [EnumeratorCancellation] CancellationToken ct)
    {
        if (firstTokenMs > 0)
            await Task.Delay(firstTokenMs, ct);
        yield return "T(";
        foreach (var word in text.Split(' '))
        {
            if (tokenMs > 0)
                await Task.Delay(tokenMs, ct);
            yield return word + " ";
        }
        yield return ")";
    }
}

internal static class TestData
{
    public static ProviderProfile Profile(ProviderProtocol protocol, string baseUrl, string model = "m", string key = "sk-test") => new()
    {
        Name = "test",
        Protocol = protocol,
        BaseUrl = baseUrl,
        Model = model,
        ApiKey = key,
        Temperature = 0.2,
        MaxTokens = 100,
        TimeoutSeconds = 10,
    };

    public static TranslationRequest Request(string text = "Hello", params ContextPair[] context) =>
        new(text, Languages.Get("zh-CN"), context, "SYS");

    public static async Task<List<string>> Collect(IAsyncEnumerable<string> stream)
    {
        var list = new List<string>();
        await foreach (var s in stream)
            list.Add(s);
        return list;
    }

    /// <summary>Polls until the condition holds; fails the test after the timeout.</summary>
    public static async Task WaitUntil(Func<bool> condition, int timeoutMs = 3000, string? because = null)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Timed out waiting: " + (because ?? "condition"));
            await Task.Delay(5);
        }
    }
}
