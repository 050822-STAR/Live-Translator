using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using LiveTranslator.Core.Http;
using LiveTranslator.Core.Models;

namespace LiveTranslator.Core.Providers;

/// <summary>Google Gemini API (generateContent / streamGenerateContent).</summary>
public sealed class GeminiProvider : ProviderBase
{
    public GeminiProvider(ProviderProfile profile, HttpClient http) : base(profile, http)
    {
    }

    internal string Endpoint
    {
        get
        {
            var model = Profile.Model.Trim();
            if (model.StartsWith("models/", StringComparison.Ordinal))
                model = model["models/".Length..];
            var method = Profile.Stream ? ":streamGenerateContent?alt=sse" : ":generateContent";
            return Profile.BaseUrl.Trim().TrimEnd('/') + "/models/" + Uri.EscapeDataString(model) + method;
        }
    }

    protected override async IAsyncEnumerable<string> StreamCoreAsync(
        TranslationRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var contents = new JsonArray();
        foreach (var (role, content) in PromptBuilder.Turns(request))
        {
            contents.Add(new JsonObject
            {
                ["role"] = role == "assistant" ? "model" : "user",
                ["parts"] = new JsonArray { new JsonObject { ["text"] = content } },
            });
        }

        var generationConfig = new JsonObject();
        if (Profile.Temperature >= 0)
            generationConfig["temperature"] = Profile.Temperature;
        if (Profile.MaxTokens > 0)
            generationConfig["maxOutputTokens"] = Profile.MaxTokens;

        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray { new JsonObject { ["text"] = request.SystemPrompt } },
            },
            ["contents"] = contents,
            ["generationConfig"] = generationConfig,
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = JsonBody(body) };
        Authorize(message);

        using var response = await SendAsync(message, ct).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        if (!Profile.Stream || IsJsonResponse(response))
        {
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var text = ParseChunk(doc.RootElement);
            if (!string.IsNullOrEmpty(text))
                yield return text;
            yield break;
        }

        await foreach (var data in StreamReaders.ReadSseDataAsync(stream, ct).ConfigureAwait(false))
        {
            using var doc = JsonDocument.Parse(data);
            var text = ParseChunk(doc.RootElement);
            if (!string.IsNullOrEmpty(text))
                yield return text;
        }
    }

    internal static string ParseChunk(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error))
            throw StreamError(ExtractErrorMessage(error.GetRawText()));
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
            return "";
        var candidate = candidates[0];
        if (!candidate.TryGetProperty("content", out var content) || !content.TryGetProperty("parts", out var parts))
            return "";

        var sb = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            // Thought summaries are flagged with "thought": true and are not the answer.
            if (part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True)
                continue;
            if (part.TryGetProperty("text", out var text))
                sb.Append(text.GetString());
        }
        return sb.ToString();
    }

    private void Authorize(HttpRequestMessage message)
    {
        if (!string.IsNullOrWhiteSpace(Profile.ApiKey))
            message.Headers.TryAddWithoutValidation("x-goog-api-key", Profile.ApiKey.Trim());
    }

    public override async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, Profile.BaseUrl.Trim().TrimEnd('/') + "/models?pageSize=1000");
        Authorize(message);
        using var doc = await GetJsonAsync(message, ct).ConfigureAwait(false);
        var models = new List<string>();
        foreach (var m in doc.RootElement.GetProperty("models").EnumerateArray())
        {
            var supportsGenerate = m.TryGetProperty("supportedGenerationMethods", out var methods) &&
                                   methods.EnumerateArray().Any(x => x.GetString() == "generateContent");
            if (supportsGenerate && m.GetProperty("name").GetString() is { } name)
                models.Add(name.StartsWith("models/", StringComparison.Ordinal) ? name["models/".Length..] : name);
        }
        return models;
    }
}
