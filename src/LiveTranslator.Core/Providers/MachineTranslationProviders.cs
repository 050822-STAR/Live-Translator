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
public sealed class GoogleFreeProvider : ProviderBase
{
    public GoogleFreeProvider(ProviderProfile profile, HttpClient http) : base(profile, http)
    {
    }

    protected override async IAsyncEnumerable<string> StreamCoreAsync(
        TranslationRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var baseUrl = string.IsNullOrWhiteSpace(Profile.BaseUrl) ? "https://translate.googleapis.com" : Profile.BaseUrl.Trim().TrimEnd('/');
        var url = $"{baseUrl}/translate_a/single?client=gtx&sl=auto&tl={Uri.EscapeDataString(request.Target.GoogleCode)}" +
                  $"&dt=t&q={Uri.EscapeDataString(request.Text)}";
        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        using var doc = await GetJsonAsync(message, ct).ConfigureAwait(false);
        var text = ParseResponse(doc.RootElement);
        if (text.Length > 0)
            yield return text;
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
