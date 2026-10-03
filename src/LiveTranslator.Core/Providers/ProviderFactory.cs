using LiveTranslator.Core.Models;

namespace LiveTranslator.Core.Providers;

public static class ProviderFactory
{
    /// <exception cref="FormatException">The profile's extra JSON body or headers are malformed.</exception>
    public static ITranslationProvider Create(ProviderProfile profile, HttpClient http) => profile.Protocol switch
    {
        ProviderProtocol.OpenAI or ProviderProtocol.AzureOpenAI => new OpenAICompatibleProvider(profile, http),
        ProviderProtocol.Anthropic => new AnthropicProvider(profile, http),
        ProviderProtocol.Gemini => new GeminiProvider(profile, http),
        ProviderProtocol.Ollama => new OllamaProvider(profile, http),
        ProviderProtocol.DeepL => new DeepLProvider(profile, http),
        ProviderProtocol.GoogleFree => new GoogleFreeProvider(profile, http),
        _ => throw new NotSupportedException($"未知协议: {profile.Protocol}"),
    };

    /// <summary>Returns a user-facing problem with the profile, or null when it looks usable.</summary>
    public static string? Validate(ProviderProfile profile)
    {
        if (profile.Protocol != ProviderProtocol.GoogleFree &&
            (!Uri.TryCreate(profile.BaseUrl?.Trim(), UriKind.Absolute, out var uri) ||
             (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
            return "接口地址必须是以 http:// 或 https:// 开头的完整 URL";
        if (profile.IsLlm && string.IsNullOrWhiteSpace(profile.Model))
            return "请填写模型名称（可点击“获取模型列表”）";
        if (profile.Protocol == ProviderProtocol.DeepL && string.IsNullOrWhiteSpace(profile.ApiKey))
            return "DeepL 需要 API Key";
        try
        {
            Create(profile, Shared.Http);
        }
        catch (FormatException ex)
        {
            return ex.Message;
        }
        return null;
    }

    private static class Shared
    {
        // Validation only constructs providers (no requests), so a never-used client is fine.
        public static readonly HttpClient Http = new();
    }
}
