namespace LiveTranslator.Core.Models;

/// <param name="Code">Stable id stored in settings.</param>
/// <param name="EnglishName">Used inside LLM prompts; models follow English instructions most reliably.</param>
public sealed record LanguageInfo(string Code, string DisplayName, string EnglishName, string GoogleCode, string DeepLCode)
{
    public override string ToString() => DisplayName;
}

public static class Languages
{
    public static IReadOnlyList<LanguageInfo> All { get; } =
    [
        new("zh-CN", "简体中文", "Simplified Chinese", "zh-CN", "ZH-HANS"),
        new("zh-TW", "繁體中文", "Traditional Chinese", "zh-TW", "ZH-HANT"),
        new("en-US", "English", "English", "en", "EN-US"),
        new("ja-JP", "日本語", "Japanese", "ja", "JA"),
        new("ko-KR", "한국어", "Korean", "ko", "KO"),
        new("fr-FR", "Français", "French", "fr", "FR"),
        new("de-DE", "Deutsch", "German", "de", "DE"),
        new("es-ES", "Español", "Spanish", "es", "ES"),
        new("pt-BR", "Português", "Portuguese", "pt", "PT-BR"),
        new("it-IT", "Italiano", "Italian", "it", "IT"),
        new("ru-RU", "Русский", "Russian", "ru", "RU"),
        new("ar-SA", "العربية", "Arabic", "ar", "AR"),
        new("th-TH", "ไทย", "Thai", "th", "TH"),
        new("vi-VN", "Tiếng Việt", "Vietnamese", "vi", "VI"),
        new("id-ID", "Bahasa Indonesia", "Indonesian", "id", "ID"),
        new("tr-TR", "Türkçe", "Turkish", "tr", "TR"),
    ];

    public static LanguageInfo Get(string? code) =>
        All.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase)) ?? All[0];
}
