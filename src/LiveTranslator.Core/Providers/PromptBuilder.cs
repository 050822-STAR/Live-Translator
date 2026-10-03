using System.Text.Json.Nodes;

using LiveTranslator.Core.Models;

namespace LiveTranslator.Core.Providers;

public static class PromptBuilder
{
    // Kept short on purpose: every prompt token is paid for in time-to-first-token on every request.
    public const string DefaultSystemPrompt =
        "You are a professional simultaneous interpreter. Translate the user's message into {lang}.\n" +
        "Rules:\n" +
        "- Output ONLY the translation on a single line: no explanations, notes, quotes, or the original text.\n" +
        "- The input is live speech recognition and may be an unfinished fragment or contain recognition errors. " +
        "Translate faithfully what is there; do not complete, summarize, or censor it.\n" +
        "- If the input is already in {lang}, output it unchanged.";

    public static string RenderSystemPrompt(string? template, LanguageInfo target)
    {
        var t = string.IsNullOrWhiteSpace(template) ? DefaultSystemPrompt : template;
        // "{0}" keeps prompts copied from LiveCaptions-Translator working.
        return t.Replace("{lang}", target.EnglishName, StringComparison.OrdinalIgnoreCase)
                .Replace("{0}", target.EnglishName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Context is sent as prior chat turns rather than inline text: models then copy the output
    /// format of their own previous answers instead of translating the context again.
    /// </summary>
    public static IEnumerable<(string Role, string Content)> Turns(TranslationRequest request)
    {
        foreach (var pair in request.Context)
        {
            if (string.IsNullOrWhiteSpace(pair.Source) || string.IsNullOrWhiteSpace(pair.Translation))
                continue;
            yield return ("user", pair.Source);
            yield return ("assistant", pair.Translation);
        }
        yield return ("user", request.Text);
    }

    public static JsonArray OpenAIMessages(TranslationRequest request)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt },
        };
        foreach (var (role, content) in Turns(request))
            messages.Add(new JsonObject { ["role"] = role, ["content"] = content });
        return messages;
    }
}
