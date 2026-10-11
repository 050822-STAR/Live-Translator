using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using LiveTranslator.Core.Http;
using LiveTranslator.Core.Models;

namespace LiveTranslator.Core.Providers;

public abstract partial class ProviderBase : ITranslationProvider
{
    private static readonly MediaTypeHeaderValue JsonMediaType = new("application/json") { CharSet = "utf-8" };

    protected ProviderBase(ProviderProfile profile, HttpClient http)
    {
        // Snapshot: edits in the settings UI must not mutate a provider that is mid-request.
        Profile = profile.Clone();
        Http = http;
    }

    protected ProviderProfile Profile { get; }
    protected HttpClient Http { get; }

    /// <summary>Client the requests go through; a provider may swap in its own connections.</summary>
    protected virtual HttpClient Client => Http;

    /// <summary>
    /// Request-body fields that switch the model's thinking off, best first (see <see cref="ThinkingOff"/>).
    /// </summary>
    protected IReadOnlyList<JsonObject> ThinkingOffVariants { get; init; } = [];

    // Index of the variant in use; past the end once the service has rejected them all.
    private int _thinkingOffIndex;

    public virtual string Name => string.IsNullOrWhiteSpace(Profile.Name) ? Profile.Protocol.ToString() : Profile.Name;

    public async IAsyncEnumerable<string> TranslateStreamAsync(
        TranslationRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, Profile.TimeoutSeconds)));
        request.Trace?.MarkStart();

        var enumerator = StreamCoreAsync(request, deadline.Token).GetAsyncEnumerator(deadline.Token);
        try
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && ex is not ProviderException)
                {
                    throw Translate(ex);
                }
                if (!hasNext)
                {
                    request.Trace?.MarkEnd();
                    yield break;
                }
                request.Trace?.MarkChunk();
                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    protected abstract IAsyncEnumerable<string> StreamCoreAsync(TranslationRequest request, CancellationToken ct);

    public virtual Task WarmUpAsync(int connections = 1, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(Profile.BaseUrl, UriKind.Absolute, out var uri))
            return Task.CompletedTask;
        var origin = uri.GetLeftPart(UriPartial.Authority) + "/";
        // Probes are sent together so each one holds a connection while the others open theirs.
        return Task.WhenAll(Enumerable.Range(0, Math.Clamp(connections, 1, 16)).Select(_ => ProbeAsync(origin, ct)));
    }

    private async Task ProbeAsync(string origin, CancellationToken ct)
    {
        // Any response — even 404 — leaves a pooled, TLS-established connection behind.
        using var request = new HttpRequestMessage(HttpMethod.Head, origin);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var _ = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Best effort only; the real request reports real errors.
        }
    }

    public virtual Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>
    /// Sends a request carrying the current thinking-off variant (merged into the body by
    /// <paramref name="build"/>). A model that cannot stop thinking rejects the switch with 400/422:
    /// then the next, weaker variant is tried, and finally none. The choice is shared by all requests,
    /// so only the first one pays for the extra round trips.
    /// </summary>
    /// <param name="handledByCaller">Rejections the caller recovers from itself, e.g. an unsupported temperature.</param>
    protected async Task<HttpResponseMessage> SendWithThinkingOffAsync(
        Func<JsonObject?, HttpRequestMessage> build, CancellationToken ct, Func<ProviderException, bool>? handledByCaller = null)
    {
        var variants = ThinkingOffVariants;
        while (true)
        {
            var index = Volatile.Read(ref _thinkingOffIndex);
            var variant = index < variants.Count ? variants[index] : null;
            using var message = build(variant);
            try
            {
                return await SendAsync(message, ct).ConfigureAwait(false);
            }
            catch (ProviderException ex) when (variant is not null &&
                                               ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity &&
                                               handledByCaller?.Invoke(ex) != true)
            {
                // Concurrent requests rejected with the same variant advance it only once.
                Interlocked.CompareExchange(ref _thinkingOffIndex, index + 1, index);
            }
        }
    }

    /// <summary>Sends the request and converts any non-2xx status into a <see cref="ProviderException"/>.</summary>
    protected async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
            return response;

        using (response)
        {
            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                body = "";
            }
            throw CreateHttpError(response.StatusCode, body);
        }
    }

    protected async Task<JsonDocument> GetJsonAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <param name="thinkingOff">The thinking-off variant to merge in, if any.</param>
    protected static HttpContent JsonBody(JsonObject body, JsonObject? thinkingOff = null)
    {
        if (thinkingOff is not null)
            JsonMerge.DeepMerge(body, thinkingOff);
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body));
        content.Headers.ContentType = JsonMediaType;
        return content;
    }

    protected static bool IsJsonResponse(HttpResponseMessage response) =>
        string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase);

    /// <summary>Appends <paramref name="path"/> unless the user already pasted a full endpoint URL.</summary>
    protected static string CombineUrl(string baseUrl, string path)
    {
        var trimmed = (baseUrl ?? "").Trim();
        var query = "";
        var q = trimmed.IndexOf('?');
        if (q >= 0)
        {
            query = trimmed[q..];
            trimmed = trimmed[..q];
        }
        trimmed = trimmed.TrimEnd('/');
        if (trimmed.EndsWith(path, StringComparison.OrdinalIgnoreCase))
            return trimmed + query;
        return trimmed + path + query;
    }

    protected static ProviderException StreamError(string message) =>
        new($"服务返回错误: {message}") { Detail = message };

    internal static ProviderException CreateHttpError(HttpStatusCode status, string body)
    {
        var detail = ExtractErrorMessage(body);
        var hint = (int)status switch
        {
            401 or 403 => "鉴权失败，请检查 API Key",
            404 => "接口地址或模型名称不正确",
            408 or 504 => "服务端超时",
            413 => "请求过大",
            429 => "请求过于频繁或额度不足",
            >= 500 => "服务端故障",
            _ => "",
        };
        var message = $"HTTP {(int)status}";
        if (detail.Length > 0)
            message += $": {detail}";
        if (hint.Length > 0)
            message += $"（{hint}）";
        return new ProviderException(message, status) { Detail = detail };
    }

    internal static string ExtractErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (FindMessage(doc.RootElement) is { Length: > 0 } message)
                return Truncate(message);
        }
        catch (JsonException)
        {
        }
        var trimmed = body.Trim();
        if (trimmed.StartsWith('<'))
        {
            // Rate-limit pages, proxies and gateways answer with HTML; markup is unreadable in a caption line.
            var title = HtmlTitle().Match(trimmed) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : "";
            return title.Length > 0 ? $"服务返回了网页而不是 API 响应：{Truncate(title)}" : "服务返回了网页而不是 API 响应";
        }
        return Truncate(trimmed);
    }

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlTitle();

    private static string? FindMessage(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Array:
                return element.GetArrayLength() > 0 ? FindMessage(element[0]) : null;
            case JsonValueKind.Object:
                foreach (var key in (ReadOnlySpan<string>)["error", "message", "detail", "msg", "error_msg", "errorMessage"])
                {
                    if (element.TryGetProperty(key, out var child) && FindMessage(child) is { Length: > 0 } found)
                        return found;
                }
                return null;
            default:
                return null;
        }
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";

    private static ProviderException Translate(Exception ex) => ex switch
    {
        OperationCanceledException => new ProviderException("请求超时，请检查网络或换用更快的服务/模型", inner: ex),
        HttpRequestException h => new ProviderException($"网络错误: {h.Message}", h.StatusCode, ex),
        IOException => new ProviderException($"连接中断: {ex.Message}", inner: ex),
        JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException =>
            new ProviderException($"无法解析服务响应: {ex.Message}", inner: ex),
        _ => new ProviderException(ex.Message, inner: ex),
    };
}
