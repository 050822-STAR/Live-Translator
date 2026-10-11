using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using LiveTranslator.Core.Models;

namespace LiveTranslator.Core.Providers;

/// <summary>
/// Request-body fields that switch a model's thinking off, per service. Thinking only delays the
/// first token of a translation, and since it shares the output budget it can use all of it up
/// (an empty answer with finish_reason "length"). Every service spells the switch differently and a
/// model that cannot stop thinking rejects it, so each service lists variants from "off" down to
/// "low", the lowest effort every reasoning API understands; <see cref="ProviderBase"/> keeps the
/// first one the service accepts, and sends none if it rejects them all.
/// </summary>
internal static partial class ThinkingOff
{
    public static IReadOnlyList<JsonObject> ForOpenAICompatible(ProviderProfile profile)
    {
        var model = profile.Model.Trim().ToLowerInvariant();
        if (profile.Protocol == ProviderProtocol.AzureOpenAI)
            return OpenAIReasoning(model);
        if (!Uri.TryCreate(profile.BaseUrl?.Trim(), UriKind.Absolute, out var uri))
            return [Low()];
        var host = uri.Host.ToLowerInvariant();

        if (Is(host, "api.openai.com"))
            return OpenAIReasoning(model);
        if (Is(host, "openrouter.ai"))
        {
            return [Json("""{"reasoning":{"effort":"none"}}"""), Json("""{"reasoning":{"effort":"minimal"}}"""),
                    Json("""{"reasoning":{"effort":"low"}}""")];
        }
        if (Is(host, "aliyuncs.com") || Is(host, "siliconflow.cn") || Is(host, "siliconflow.com"))
            return [Json("""{"enable_thinking":false}"""), Low()];
        if (Is(host, "volces.com") || Is(host, "bigmodel.cn") || Is(host, "z.ai"))
            return [Json("""{"thinking":{"type":"disabled"}}"""), Low()];
        if (Is(host, "groq.com") && model.Contains("qwen", StringComparison.Ordinal))
            return [Json("""{"reasoning_effort":"none"}"""), Low()]; // the only model there that switches off
        if (Is(host, "generativelanguage.googleapis.com"))
            return [Json("""{"reasoning_effort":"none"}"""), Low()];
        if (Is(host, "deepseek.com"))
            return [Json("""{"thinking":{"type":"disabled"}}"""), Low()]; // V4 models think by default, at "high"
        if (WithoutSwitch.Any(known => Is(host, known)))
            return [Low()];

        // Everything else: a vendor whose switch may be new (thinking-by-default models keep appearing),
        // or a local server / self-hosted gateway (vLLM, SGLang, llama.cpp, LM Studio, One API, ...)
        // that can be serving any vendor's model. The first try carries every vendor's switch at once
        // (after LiveCaptions-Translator): most servers ignore fields they do not know, and the one that
        // applies takes effect. A strict server rejects unknown fields, so then the switches without the
        // effort level, then each common spelling on its own, then "low".
        return
        [
            Json(Switches).Also(Low()),
            Json(Switches),
            Json("""{"thinking":{"type":"disabled"}}"""),
            Json("""{"enable_thinking":false}"""),
            Low(),
        ];
    }

    public static IReadOnlyList<JsonObject> ForGemini(ProviderProfile profile)
    {
        var match = GeminiVersion().Match(profile.Model);
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var version))
        {
            // An alias such as "gemini-flash-latest" hides the generation: try the 2.5 switch, then 3's "low".
            return [GeminiThinking("""{"thinkingBudget":0}"""), GeminiThinking("""{"thinkingLevel":"low"}""")];
        }
        if (version >= 3)
        {
            // Gemini 3 cannot switch thinking off; "minimal" comes closest (Pro only goes down to "low").
            return [GeminiThinking("""{"thinkingLevel":"minimal"}"""), GeminiThinking("""{"thinkingLevel":"low"}""")];
        }
        if (version >= 2.5)
        {
            // 2.5 Flash and Flash-Lite switch off with 0; 2.5 Pro only goes down to 128.
            return [GeminiThinking("""{"thinkingBudget":0}"""), GeminiThinking("""{"thinkingBudget":128}""")];
        }
        return []; // older models do not think
    }

    /// <summary>GPT-OSS ignores <c>think: false</c> and keeps thinking; it only takes an effort level.</summary>
    public static IReadOnlyList<JsonObject> ForOllama(ProviderProfile profile) =>
        profile.Model.Contains("gpt-oss", StringComparison.OrdinalIgnoreCase)
            ? [Json("""{"think":"low"}""")]
            : [Json("""{"think":false}"""), Json("""{"think":"low"}""")];

    /// <summary>
    /// Strict APIs confirmed to have no off switch (unknown fields are rejected): only the generic
    /// "low" is tried. Anything not known for certain belongs in the bundle path instead.
    /// </summary>
    private static readonly string[] WithoutSwitch = ["x.ai", "mistral.ai", "cohere.ai", "cohere.com", "anthropic.com", "groq.com"];

    /// <summary>OpenAI's reasoning models: 5.1 and later switch off with "none", gpt-5 goes down to "minimal", o-series to "low".</summary>
    private static IReadOnlyList<JsonObject> OpenAIReasoning(string model) =>
        OpenAIReasoningModel().IsMatch(model)
            ? [Json("""{"reasoning_effort":"none"}"""), Json("""{"reasoning_effort":"minimal"}"""), Low()]
            : [Low()];

    /// <summary>The lowest effort level shared by every OpenAI-style reasoning API.</summary>
    private static JsonObject Low() => Json("""{"reasoning_effort":"low"}""");

    /// <summary>Every common spelling of "thinking off" at once.</summary>
    private const string Switches = """
        {
          "enable_thinking": false,
          "thinking": { "type": "disabled" },
          "chat_template_kwargs": { "enable_thinking": false },
          "think": false
        }
        """;

    private static JsonObject Also(this JsonObject target, JsonObject more)
    {
        foreach (var (key, value) in more)
            target[key] = value?.DeepClone();
        return target;
    }

    private static bool Is(string host, string domain) =>
        host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);

    private static JsonObject GeminiThinking(string thinkingConfig) =>
        Json("""{"generationConfig":{"thinkingConfig":""" + thinkingConfig + "}}");

    private static JsonObject Json(string json) => (JsonObject)JsonNode.Parse(json)!;

    [GeneratedRegex(@"^(o\d|gpt-5)")]
    private static partial Regex OpenAIReasoningModel();

    [GeneratedRegex(@"gemini-(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex GeminiVersion();
}
