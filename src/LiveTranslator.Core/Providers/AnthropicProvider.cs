using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

using LiveTranslator.Core.Http;
using LiveTranslator.Core.Models;

namespace LiveTranslator.Core.Providers;

/// <summary>Anthropic Messages API (Claude).</summary>
public sealed class AnthropicProvider : ProviderBase
{
    private const string ApiVersion = "2023-06-01";

    public AnthropicProvider(ProviderProfile profile, HttpClient http) : base(profile, http)
    {
    }

    protected override async IAsyncEnumerable<string> StreamCoreAsync(
        TranslationRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var messages = new JsonArray();
        foreach (var (role, content) in PromptBuilder.Turns(request))
            messages.Add(new JsonObject { ["role"] = role, ["content"] = content });

        var body = new JsonObject
        {
            ["model"] = Profile.Model,
            // Required by this API, so fall back to a sane cap when the user disabled it.
            ["max_tokens"] = Profile.MaxTokens > 0 ? Profile.MaxTokens : 1024,
            ["system"] = request.SystemPrompt,
            ["messages"] = messages,
            ["stream"] = Profile.Stream,
        };
        if (Profile.Temperature >= 0)
            body["temperature"] = Math.Min(Profile.Temperature, 1.0);

        using var message = new HttpRequestMessage(HttpMethod.Post, CombineUrl(Profile.BaseUrl, "/messages")) { Content = JsonBody(body) };
        Authorize(message);

        using var response = await SendAsync(message, ct).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        if (!Profile.Stream || IsJsonResponse(response))
        {
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var text = string.Concat(doc.RootElement.GetProperty("content").EnumerateArray()
                .Where(b => b.TryGetProperty("type", out var t) && t.GetString() == "text")
                .Select(b => b.GetProperty("text").GetString()));
            if (text.Length > 0)
                yield return text;
            yield break;
        }

        await foreach (var data in StreamReaders.ReadSseDataAsync(stream, ct).ConfigureAwait(false))
        {
            var (text, done) = ParseEvent(data);
            if (!string.IsNullOrEmpty(text))
                yield return text;
            if (done)
                yield break;
        }
    }

    internal static (string? Text, bool Done) ParseEvent(string data)
    {
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "content_block_delta":
                var delta = root.GetProperty("delta");
                return delta.TryGetProperty("type", out var dt) && dt.GetString() == "text_delta"
                    ? (delta.GetProperty("text").GetString(), false)
                    : (null, false); // thinking_delta / signature_delta are not part of the answer
            case "message_stop":
                return (null, true);
            case "error":
                throw StreamError(ExtractErrorMessage(root.GetRawText()));
            default:
                return (null, false);
        }
    }

    private void Authorize(HttpRequestMessage message)
    {
        message.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        if (!string.IsNullOrWhiteSpace(Profile.ApiKey))
            message.Headers.TryAddWithoutValidation("x-api-key", Profile.ApiKey.Trim());
    }

    public override async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, CombineUrl(Profile.BaseUrl, "/models") + "?limit=1000");
        Authorize(message);
        using var doc = await GetJsonAsync(message, ct).ConfigureAwait(false);
        return doc.RootElement.GetProperty("data").EnumerateArray()
            .Select(m => m.GetProperty("id").GetString())
            .OfType<string>()
            .ToList();
    }
}
