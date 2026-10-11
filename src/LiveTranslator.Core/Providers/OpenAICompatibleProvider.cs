using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

using LiveTranslator.Core.Http;
using LiveTranslator.Core.Models;

namespace LiveTranslator.Core.Providers;

/// <summary>
/// OpenAI Chat Completions. Also covers Azure OpenAI and every vendor exposing an OpenAI-compatible
/// endpoint (DeepSeek, Qwen, GLM, Kimi, Doubao, Groq, OpenRouter, vLLM, LM Studio, ...).
/// </summary>
public sealed class OpenAICompatibleProvider : ProviderBase
{
    // Set once a server rejects optional sampling parameters (e.g. reasoning models that only accept
    // the default temperature); later requests skip them instead of paying for a failed round trip.
    private volatile bool _minimalParameters;

    public OpenAICompatibleProvider(ProviderProfile profile, HttpClient http) : base(profile, http)
    {
        ThinkingOffVariants = ThinkingOff.ForOpenAICompatible(Profile);
    }

    private string Endpoint => CombineUrl(Profile.BaseUrl, "/chat/completions");

    private bool UsesMaxCompletionTokens =>
        Profile.Protocol == ProviderProtocol.AzureOpenAI ||
        (Uri.TryCreate(Profile.BaseUrl, UriKind.Absolute, out var uri) &&
         uri.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase));

    protected override async IAsyncEnumerable<string> StreamCoreAsync(
        TranslationRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var response = await SendWithFallbacksAsync(request, ct).ConfigureAwait(false);
        request.Trace?.MarkHeaders();

        using (response)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            if (!Profile.Stream || IsJsonResponse(response))
            {
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                var text = ParseMessage(doc.RootElement);
                request.Trace?.SetFinishReason(FinishReason(doc.RootElement));
                if (!string.IsNullOrEmpty(text))
                    yield return text;
                yield break;
            }

            await foreach (var data in StreamReaders.ReadSseDataAsync(stream, ct).ConfigureAwait(false))
            {
                if (data == "[DONE]")
                    yield break;
                var delta = ParseDelta(data, out var finishReason);
                request.Trace?.SetFinishReason(finishReason);
                if (!string.IsNullOrEmpty(delta))
                    yield return delta;
            }
        }
    }

    /// <summary>
    /// Drops parameters the server rejects and retries: sampling parameters here, the thinking switch
    /// in <see cref="ProviderBase.SendWithThinkingOffAsync"/>. Both are remembered, so only the first
    /// request pays for the extra round trips.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithFallbacksAsync(TranslationRequest request, CancellationToken ct)
    {
        while (true)
        {
            try
            {
                return await SendWithThinkingOffAsync(thinkingOff => BuildChat(request, thinkingOff), ct,
                    handledByCaller: ex => !_minimalParameters && IsUnsupportedParameterError(ex)).ConfigureAwait(false);
            }
            catch (ProviderException ex) when (!_minimalParameters && IsUnsupportedParameterError(ex))
            {
                _minimalParameters = true;
            }
        }
    }

    private HttpRequestMessage BuildChat(TranslationRequest request, JsonObject? thinkingOff)
    {
        var body = new JsonObject
        {
            ["model"] = Profile.Model,
            ["messages"] = PromptBuilder.OpenAIMessages(request),
            ["stream"] = Profile.Stream,
        };
        if (!_minimalParameters)
        {
            if (Profile.Temperature >= 0)
                body["temperature"] = Profile.Temperature;
            if (Profile.MaxTokens > 0)
                body[UsesMaxCompletionTokens ? "max_completion_tokens" : "max_tokens"] = Profile.MaxTokens;
        }

        var message = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = JsonBody(body, thinkingOff) };
        if (Profile.Stream)
            message.Headers.Accept.ParseAdd("text/event-stream"); // tells gateways not to buffer the response
        Authorize(message);
        return message;
    }

    private void Authorize(HttpRequestMessage message)
    {
        if (string.IsNullOrWhiteSpace(Profile.ApiKey))
            return;
        if (Profile.Protocol == ProviderProtocol.AzureOpenAI)
            message.Headers.TryAddWithoutValidation("api-key", Profile.ApiKey.Trim());
        else
            message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Profile.ApiKey.Trim());
    }

    public override async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, CombineUrl(ReplaceEndpointSuffix(Profile.BaseUrl), "/models"));
        Authorize(message);
        using var doc = await GetJsonAsync(message, ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];
        return data.EnumerateArray()
            .Select(m => m.TryGetProperty("id", out var id) ? id.GetString() : null)
            .OfType<string>()
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ReplaceEndpointSuffix(string baseUrl)
    {
        var url = baseUrl.Trim().TrimEnd('/');
        const string suffix = "/chat/completions";
        return url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? url[..^suffix.Length] : url;
    }

    internal static string? ParseDelta(string data) => ParseDelta(data, out _);

    internal static string? ParseDelta(string data, out string? finishReason)
    {
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        ThrowIfError(root);
        finishReason = FinishReason(root);
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            return null;
        var choice = choices[0];
        // Reasoning models stream "reasoning_content" alongside; only "content" is the answer.
        if (choice.TryGetProperty("delta", out var delta) &&
            delta.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.String)
            return content.GetString();
        // A few gateways stream full "message" objects instead of deltas.
        if (choice.TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var full) &&
            full.ValueKind == JsonValueKind.String)
            return full.GetString();
        return null;
    }

    private static string? FinishReason(JsonElement root) =>
        root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 &&
        choices[0].TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String
            ? reason.GetString()
            : null;

    internal static string? ParseMessage(JsonElement root)
    {
        ThrowIfError(root);
        return root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
    }

    private static void ThrowIfError(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            throw StreamError(ExtractErrorMessage(error.GetRawText()));
    }

    private static bool IsUnsupportedParameterError(ProviderException ex)
    {
        if (ex.StatusCode is not (HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity))
            return false;
        // Only errors naming a sampling parameter: a generic "unsupported" may be about the thinking
        // switch, which has its own fallback that keeps temperature and the token cap.
        var detail = ex.Detail ?? ex.Message;
        return detail.Contains("temperature", StringComparison.OrdinalIgnoreCase) ||
               detail.Contains("max_tokens", StringComparison.OrdinalIgnoreCase) ||
               detail.Contains("max_completion_tokens", StringComparison.OrdinalIgnoreCase) ||
               detail.Contains("top_p", StringComparison.OrdinalIgnoreCase);
    }
}
