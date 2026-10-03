using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

using LiveTranslator.Core.Http;
using LiveTranslator.Core.Models;

namespace LiveTranslator.Core.Providers;

/// <summary>Ollama native chat API. Keeps the model resident so requests never pay a reload.</summary>
public sealed class OllamaProvider : ProviderBase
{
    public OllamaProvider(ProviderProfile profile, HttpClient http) : base(profile, http)
    {
    }

    protected override async IAsyncEnumerable<string> StreamCoreAsync(
        TranslationRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var options = new JsonObject();
        if (Profile.Temperature >= 0)
            options["temperature"] = Profile.Temperature;
        if (Profile.MaxTokens > 0)
            options["num_predict"] = Profile.MaxTokens;

        var body = new JsonObject
        {
            ["model"] = Profile.Model,
            ["messages"] = PromptBuilder.OpenAIMessages(request),
            ["stream"] = Profile.Stream,
            ["keep_alive"] = "30m",
            ["options"] = options,
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, CombineUrl(Profile.BaseUrl, "/api/chat")) { Content = JsonBody(body) };
        Authorize(message);
        using var response = await SendAsync(message, ct).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        await foreach (var line in StreamReaders.ReadLinesAsync(stream, ct).ConfigureAwait(false))
        {
            var (text, done) = ParseLine(line);
            if (!string.IsNullOrEmpty(text))
                yield return text;
            if (done)
                yield break;
        }
    }

    internal static (string? Text, bool Done) ParseLine(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var error))
            throw StreamError(error.ValueKind == JsonValueKind.String ? error.GetString() ?? "" : error.GetRawText());
        string? text = null;
        if (root.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content))
            text = content.GetString();
        var done = root.TryGetProperty("done", out var d) && d.ValueKind == JsonValueKind.True;
        return (text, done);
    }

    private void Authorize(HttpRequestMessage message)
    {
        // Plain Ollama has no auth; reverse proxies in front of it often use a bearer token.
        if (!string.IsNullOrWhiteSpace(Profile.ApiKey))
            message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Profile.ApiKey.Trim());
    }

    public override async Task WarmUpAsync(CancellationToken ct = default)
    {
        // Loading weights is the dominant cold-start cost for local models: an empty chat
        // request makes Ollama load the model into memory without generating anything.
        if (string.IsNullOrWhiteSpace(Profile.Model))
            return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(120));
            var body = new JsonObject { ["model"] = Profile.Model, ["messages"] = new JsonArray(), ["keep_alive"] = "30m" };
            using var message = new HttpRequestMessage(HttpMethod.Post, CombineUrl(Profile.BaseUrl, "/api/chat")) { Content = JsonBody(body) };
            Authorize(message);
            using var _ = await SendAsync(message, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
        }
    }

    public override async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, CombineUrl(Profile.BaseUrl, "/api/tags"));
        Authorize(message);
        using var doc = await GetJsonAsync(message, ct).ConfigureAwait(false);
        return doc.RootElement.GetProperty("models").EnumerateArray()
            .Select(m => m.GetProperty("name").GetString())
            .OfType<string>()
            .ToList();
    }
}
