using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using LiveTranslator.Core.Models;

namespace LiveTranslator.Core.Providers;

/// <summary>DeepL REST API v2 (Free and Pro). Non-streaming, so the result arrives in one piece.</summary>
public sealed class DeepLProvider : ProviderBase
{
    public DeepLProvider(ProviderProfile profile, HttpClient http) : base(profile, http)
    {
    }

    protected override async IAsyncEnumerable<string> StreamCoreAsync(
        TranslationRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["text"] = new JsonArray { request.Text },
            ["target_lang"] = request.Target.DeepLCode,
        };
        // DeepL accepts surrounding text that improves the translation without being translated.
        var context = string.Join(" ", request.Context.Select(c => c.Source));
        if (context.Length > 0)
            body["context"] = context;

        using var message = new HttpRequestMessage(HttpMethod.Post, CombineUrl(Profile.BaseUrl, "/v2/translate")) { Content = JsonBody(body) };
        message.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + Profile.ApiKey.Trim());
        using var doc = await GetJsonAsync(message, ct).ConfigureAwait(false);
        var text = doc.RootElement.GetProperty("translations")[0].GetProperty("text").GetString();
        if (!string.IsNullOrEmpty(text))
            yield return text;
    }
}

/// <summary>Google Translate public web endpoint. No key; best-effort availability.</summary>
/// <remarks>
/// Google flags individual connections: once one starts answering 429 ("Sorry", bot detection),
/// every request on it keeps failing while a brand-new connection to the same server succeeds.
/// Pooled, kept-alive connections would therefore stay broken for many minutes, so this provider
/// owns its connections and replaces them (and retries once) as soon as a 429 arrives. A spare,
/// already-handshaken connection is kept ready so the switch costs no TLS setup — in practice the
/// first connection of a session is often the one that gets flagged.
/// </remarks>
public sealed class GoogleFreeProvider : ProviderBase
{
    private static readonly TimeSpan RetireAfter = TimeSpan.FromSeconds(30);
    private const int MaxReconnects = 2;

    private readonly Func<HttpClient>? _newClient;
    private readonly object _swap = new();
    private HttpClient _client;
    private HttpClient? _standby;

    /// <param name="newClient">Creates a client with fresh connections (same proxy settings as <paramref name="http"/>).</param>
    public GoogleFreeProvider(ProviderProfile profile, HttpClient http, Func<HttpClient>? newClient = null) : base(profile, http)
    {
        _newClient = newClient;
        _client = newClient?.Invoke() ?? http;
        _standby = newClient?.Invoke();
    }

    protected override HttpClient Client
    {
        get { lock (_swap) return _client; }
    }

    /// <summary>
    /// One probe for the active connection and one for the spare: answers are fast enough that more
    /// pre-opened connections buy nothing, and parallel bursts are what this endpoint dislikes.
    /// </summary>
    public override Task WarmUpAsync(int connections = 1, CancellationToken ct = default)
    {
        HttpClient active;
        HttpClient? standby;
        lock (_swap)
            (active, standby) = (_client, _standby);
        return standby is null ? ProbeAsync(active, ct) : Task.WhenAll(ProbeAsync(active, ct), ProbeAsync(standby, ct));
    }

    private async Task ProbeAsync(HttpClient client, CancellationToken ct)
    {
        if (!Uri.TryCreate(string.IsNullOrWhiteSpace(Profile.BaseUrl) ? DefaultBaseUrl : Profile.BaseUrl, UriKind.Absolute, out var uri))
            return;
        using var request = new HttpRequestMessage(HttpMethod.Head, uri.GetLeftPart(UriPartial.Authority) + "/");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var _ = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Best effort: an unopened spare just costs a handshake when it is first used.
        }
    }

    private const string DefaultBaseUrl = "https://translate.googleapis.com";

    protected override async IAsyncEnumerable<string> StreamCoreAsync(
        TranslationRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var baseUrl = string.IsNullOrWhiteSpace(Profile.BaseUrl) ? DefaultBaseUrl : Profile.BaseUrl.Trim().TrimEnd('/');
        var url = $"{baseUrl}/translate_a/single?client=gtx&sl=auto&tl={Uri.EscapeDataString(request.Target.GoogleCode)}" +
                  $"&dt=t&q={Uri.EscapeDataString(request.Text)}";
        JsonDocument doc;
        for (int attempt = 0; ; attempt++)
        {
            var used = Client;
            try
            {
                doc = await GetAsync(url, ct).ConfigureAwait(false);
                break;
            }
            // First retry on the warm spare; if that one is flagged too, once more on the spare that
            // was opened just now. Which connections Google flags is not predictable, so two quick
            // switches beat one, and a third 429 is reported rather than hammering.
            catch (ProviderException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxReconnects && Reconnect(used))
            {
            }
        }
        using (doc)
        {
            var text = ParseResponse(doc.RootElement);
            if (text.Length > 0)
                yield return text;
        }
    }

    private async Task<JsonDocument> GetAsync(string url, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        return await GetJsonAsync(message, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Switches to the warm spare after a 429 and starts preparing the next spare in the background;
    /// false when no fresh client can be made.
    /// </summary>
    private bool Reconnect(HttpClient failed)
    {
        if (_newClient is null)
            return false;
        HttpClient spare;
        lock (_swap)
        {
            if (!ReferenceEquals(_client, failed))
                return true; // a concurrent request already reconnected
            _client = _standby ?? _newClient();
            _standby = spare = _newClient();
        }
        _ = ProbeAsync(spare, CancellationToken.None);
        if (!ReferenceEquals(failed, Http))
            _ = Task.Delay(RetireAfter).ContinueWith(_ => failed.Dispose(), TaskScheduler.Default); // let in-flight requests finish
        return true;
    }

    // Shape: [[["translated","original",...], ["translated2","original2",...]], ...]
    internal static string ParseResponse(JsonElement root)
    {
        var sb = new StringBuilder();
        foreach (var segment in root[0].EnumerateArray())
        {
            if (segment.ValueKind == JsonValueKind.Array && segment.GetArrayLength() > 0 &&
                segment[0].ValueKind == JsonValueKind.String)
                sb.Append(segment[0].GetString());
        }
        return sb.ToString();
    }
}
