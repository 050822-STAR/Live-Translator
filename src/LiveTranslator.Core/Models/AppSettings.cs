using LiveTranslator.Core.Providers;

namespace LiveTranslator.Core.Models;

public sealed class AppSettings
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public string TargetLanguage { get; set; } = "zh-CN";
    public string ActiveProfileId { get; set; } = "";

    /// <summary>Optional second provider raced against the active one (hedged request).</summary>
    public string BackupProfileId { get; set; } = "";

    /// <summary>Start the backup if the active provider has produced no token after this delay.</summary>
    public int HedgeDelayMs { get; set; } = 1200;

    /// <summary>System prompt template; <c>{lang}</c> is replaced with the target language.</summary>
    public string SystemPrompt { get; set; } = PromptBuilder.DefaultSystemPrompt;

    public List<ProviderProfile> Profiles { get; set; } = [];
    public PipelineOptions Pipeline { get; set; } = new();
    public DisplayOptions Display { get; set; } = new();
    public OverlayOptions Overlay { get; set; } = new();
    public NetworkOptions Network { get; set; } = new();

    public ProviderProfile? FindProfile(string? id) =>
        string.IsNullOrEmpty(id) ? null : Profiles.FirstOrDefault(p => p.Id == id);

    /// <summary>Upgrades a file written by an older version. Only values the user never changed are touched.</summary>
    public void Migrate()
    {
        if (Version < 2)
        {
            // v2: translations use normal body text instead of an enlarged, pure-white highlight.
            Display ??= new();
            Overlay ??= new();
            if (Display.TranslationFontSize == 19)
                Display.TranslationFontSize = DisplayOptions.DefaultTranslationFontSize;
            if (Overlay.FontSize == 26)
                Overlay.FontSize = OverlayOptions.DefaultFontSize;
            if (string.Equals(Overlay.TextColor, "#FFFFFF", StringComparison.OrdinalIgnoreCase))
                Overlay.TextColor = OverlayOptions.DefaultTextColor;
        }
        Version = CurrentVersion;
    }

    /// <summary>Repairs references and clamps values so a hand-edited file cannot break the app.</summary>
    public void Normalize()
    {
        Profiles ??= [];
        Pipeline ??= new();
        Display ??= new();
        Overlay ??= new();
        Network ??= new();
        if (string.IsNullOrWhiteSpace(SystemPrompt))
            SystemPrompt = PromptBuilder.DefaultSystemPrompt;
        TargetLanguage = Languages.Get(TargetLanguage).Code;

        if (Profiles.Count == 0)
            Profiles.Add(ProviderPresets.CreateProfile("google-free"));
        foreach (var group in Profiles.GroupBy(p => p.Id).Where(g => g.Count() > 1))
            foreach (var dup in group.Skip(1))
                dup.Id = Guid.NewGuid().ToString("N");
        if (FindProfile(ActiveProfileId) is null)
            ActiveProfileId = Profiles[0].Id;
        if (BackupProfileId == ActiveProfileId || FindProfile(BackupProfileId) is null)
            BackupProfileId = "";

        HedgeDelayMs = Math.Clamp(HedgeDelayMs, 100, 30_000);
        foreach (var p in Profiles)
        {
            p.TimeoutSeconds = Math.Clamp(p.TimeoutSeconds, 2, 300);
            p.MaxTokens = Math.Clamp(p.MaxTokens, 0, 32_000);
        }
        Pipeline.Clamp();
        Display.Clamp();
        Overlay.Clamp();
    }
}

public sealed class PipelineOptions
{
    /// <summary>Translate the unfinished sentence while it is still being spoken.</summary>
    public bool PartialTranslation { get; set; } = true;

    /// <summary>Minimum spacing between two partial requests.</summary>
    public int PartialIntervalMs { get; set; } = 350;

    /// <summary>Partial text shorter than this (CJK characters count double) is not translated.</summary>
    public int PartialMinChars { get; set; } = 6;

    /// <summary>Only the tail of very long unpunctuated speech is translated as a partial.</summary>
    public int PartialMaxChars { get; set; } = 220;

    public int ContextSentences { get; set; } = 2;
    public int MaxConcurrentRequests { get; set; } = 4;
    public bool CacheEnabled { get; set; } = true;
    public bool KeepConnectionWarm { get; set; } = true;
    public int CaptionPollMs { get; set; } = 15;
    public bool HideLiveCaptionsWindow { get; set; } = true;

    public void Clamp()
    {
        PartialIntervalMs = Math.Clamp(PartialIntervalMs, 80, 5000);
        PartialMinChars = Math.Clamp(PartialMinChars, 1, 200);
        PartialMaxChars = Math.Clamp(PartialMaxChars, 40, 2000);
        ContextSentences = Math.Clamp(ContextSentences, 0, 10);
        MaxConcurrentRequests = Math.Clamp(MaxConcurrentRequests, 1, 16);
        CaptionPollMs = Math.Clamp(CaptionPollMs, 5, 500);
    }
}

public sealed class DisplayOptions
{
    public bool Topmost { get; set; } = true;
    public double OriginalFontSize { get; set; } = 14;
    public const double DefaultTranslationFontSize = 15;

    public double TranslationFontSize { get; set; } = DefaultTranslationFontSize;
    public int HistoryCount { get; set; } = 30;
    public bool ShowLatency { get; set; } = true;
    public bool LogToFile { get; set; }

    /// <summary>"left,top,width,height" in device-independent pixels; empty = default position.</summary>
    public string MainBounds { get; set; } = "";

    public void Clamp()
    {
        OriginalFontSize = Math.Clamp(OriginalFontSize, 8, 72);
        TranslationFontSize = Math.Clamp(TranslationFontSize, 8, 96);
        HistoryCount = Math.Clamp(HistoryCount, 1, 500);
    }
}

public sealed class OverlayOptions
{
    public const double DefaultFontSize = 20;
    public const string DefaultTextColor = "#EDEDED";

    public bool Enabled { get; set; }
    public double FontSize { get; set; } = DefaultFontSize;
    public bool ShowOriginal { get; set; } = true;
    public bool ShowPrevious { get; set; } = true;
    public double BackgroundOpacity { get; set; } = 0.55;
    public string TextColor { get; set; } = DefaultTextColor;
    public bool ClickThrough { get; set; }
    public string Bounds { get; set; } = "";

    public void Clamp()
    {
        FontSize = Math.Clamp(FontSize, 10, 120);
        BackgroundOpacity = Math.Clamp(BackgroundOpacity, 0, 1);
    }
}

public sealed class NetworkOptions
{
    /// <summary>Empty = system proxy, "none" = direct connection, otherwise a proxy URL.</summary>
    public string Proxy { get; set; } = "";
}
