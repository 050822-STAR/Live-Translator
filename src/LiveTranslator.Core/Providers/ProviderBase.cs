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
        ExtraBody = JsonMerge.ParseObject(Profile.ExtraBodyJson);
        ExtraHeaders = ParseHeaders(Profile.ExtraHeaders);
    }

    protected ProviderProfile Profile { get; }
    protected HttpClient Http { get; }
    private JsonObject ExtraBody { get; }
    private IReadOnlyList<KeyValuePair<string, string>> ExtraHeaders { get; }

    public virtual string Name => string.IsNullOrWhiteSpace(Profile.Name) ? Profile.Protocol.ToString() : Profile.Name;

    public async IAsyncEnumerable<string> TranslateStreamAsync(
        TranslationRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, Profile.TimeoutSeconds)));

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
                    yield break;
                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    protected abstract IAsyncEnumerable<string> StreamCoreAsync(TranslationRequest request, CancellationToken ct);

    public virtual async Task WarmUpAsync(CancellationToken ct = default)
    {
        if (!Uri.TryCreate(Profile.BaseUrl, UriKind.Absolute, out var uri))
            return;
        // Any response — even 404 — leaves a pooled, TLS-established connection behind.
        using var request = new HttpRequestMessage(HttpMethod.Head, uri.GetLeftPart(UriPartial.Authority) + "/");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var _ = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Best effort only; the real request reports real errors.
        }
    }

    public virtual Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>Sends the request and converts any non-2xx status into a <see cref="ProviderException"/>.</summary>
    protected async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        foreach (var (name, value) in ExtraHeaders)
        {
            request.Headers.Remove(name);
            if (!request.Headers.TryAddWithoutValidation(name, value) && request.Content is not null)
            {
                request.Content.Headers.Remove(name);
                request.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }

        var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
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

    protected HttpContent JsonBody(JsonObject body)
    {
        JsonMerge.DeepMerge(body, ExtraBody);
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

    private static List<KeyValuePair<string, string>> ParseHeaders(string? text)
    {
        var headers = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrWhiteSpace(text))
            return headers;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var colon = line.IndexOf(':');
            if (colon <= 0)
                throw new FormatException($"请求头格式应为 \"Name: value\"：{line}");
            headers.Add(new(line[..colon].Trim(), line[(colon + 1)..].Trim()));
        }
        return headers;
    }
}
