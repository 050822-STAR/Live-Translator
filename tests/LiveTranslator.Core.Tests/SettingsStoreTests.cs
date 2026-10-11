using LiveTranslator.Core.Models;
using LiveTranslator.Core.Providers;
using LiveTranslator.Core.Settings;

namespace LiveTranslator.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lt-tests-" + Guid.NewGuid().ToString("N"));

    private sealed class ReversingProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "enc:" + new string(plaintext.Reverse().ToArray());
        public string Unprotect(string value) => value.StartsWith("enc:")
            ? new string(value[4..].Reverse().ToArray())
            : throw new InvalidOperationException("not ours");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Round_trips_settings_and_never_writes_api_keys_in_plain_text()
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"), new ReversingProtector());
        var settings = new AppSettings { TargetLanguage = "ja-JP" };
        var profile = ProviderPresets.CreateProfile("deepseek");
        profile.ApiKey = "sk-secret-123";
        settings.Profiles.Add(profile);
        settings.ActiveProfileId = profile.Id;
        settings.Pipeline.PartialIntervalMs = 500;

        store.Save(settings);
        var raw = File.ReadAllText(store.Path);
        var loaded = store.Load();

        Assert.DoesNotContain("sk-secret-123", raw);
        Assert.Contains("\"Protocol\": \"OpenAI\"", raw); // enums stored as names, readable and stable
        Assert.Equal("sk-secret-123", loaded.FindProfile(profile.Id)!.ApiKey);
        Assert.Equal("ja-JP", loaded.TargetLanguage);
        Assert.Equal(profile.Id, loaded.ActiveProfileId);
        Assert.Equal(500, loaded.Pipeline.PartialIntervalMs);
        Assert.Equal("sk-secret-123", profile.ApiKey); // saving must not mutate the live object
    }

    [Fact]
    public void File_from_a_version_with_extra_body_and_headers_still_loads()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """
            {
              "Version": 3,
              "ActiveProfileId": "p1",
              "Profiles": [
                { "Id": "p1", "Name": "千问", "Protocol": "OpenAI", "BaseUrl": "https://dashscope.aliyuncs.com/compatible-mode/v1",
                  "Model": "qwen-turbo", "ExtraBodyJson": "{\"enable_thinking\":false}", "ExtraHeaders": "X-Custom: 1" }
              ]
            }
            """);
        var store = new SettingsStore(path, new PlainTextProtector());

        var loaded = store.Load();

        Assert.Null(store.RecoveredBackupPath); // not treated as corrupt
        Assert.Equal("qwen-turbo", loaded.FindProfile("p1")!.Model);
        store.Save(loaded);
        Assert.DoesNotContain("ExtraBodyJson", File.ReadAllText(path)); // dropped on the next save
    }

    [Fact]
    public void Corrupt_file_is_moved_aside_and_defaults_are_usable()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ this is not json");
        var store = new SettingsStore(path, new PlainTextProtector());

        var loaded = store.Load();

        Assert.NotNull(store.RecoveredBackupPath);
        Assert.True(File.Exists(store.RecoveredBackupPath));
        Assert.False(File.Exists(path));
        var active = Assert.Single(loaded.Profiles);
        Assert.Equal("google-free", active.PresetId);
        Assert.Equal(active.Id, loaded.ActiveProfileId);
    }

    [Fact]
    public void Undecryptable_key_is_dropped_and_bad_references_are_repaired()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """
            {
              "ActiveProfileId": "missing",
              "BackupProfileId": "a",
              "HedgeDelayMs": 5,
              "Pipeline": { "PartialIntervalMs": 1, "MaxConcurrentRequests": 999 },
              "Profiles": [ { "Id": "a", "Name": "A", "Protocol": "Anthropic", "ApiKey": "foreign-blob", "TimeoutSeconds": 0 } ]
            }
            """);
        var loaded = new SettingsStore(path, new ReversingProtector()).Load();

        Assert.Equal("a", loaded.ActiveProfileId);
        Assert.Equal("", loaded.BackupProfileId); // cannot back up itself
        Assert.Equal("", loaded.Profiles[0].ApiKey);
        Assert.Equal(ProviderProtocol.Anthropic, loaded.Profiles[0].Protocol);
        Assert.Equal(2, loaded.Profiles[0].TimeoutSeconds);
        Assert.Equal(100, loaded.HedgeDelayMs);
        Assert.Equal(80, loaded.Pipeline.PartialIntervalMs);
        Assert.Equal(16, loaded.Pipeline.MaxConcurrentRequests);
        // Options added after 1.0.0 fall back to their defaults for older files.
        Assert.Equal(3, loaded.Pipeline.PartialMaxInFlight);
        Assert.Equal(90, loaded.Pipeline.LongSentenceSplitChars);
    }

    [Fact]
    public void Version_1_display_defaults_are_upgraded_but_user_choices_are_kept()
    {
        Directory.CreateDirectory(_dir);
        var oldDefaults = Path.Combine(_dir, "old.json");
        File.WriteAllText(oldDefaults, """
            { "Version": 1, "Display": { "TranslationFontSize": 19 }, "Overlay": { "FontSize": 26, "TextColor": "#FFFFFF" } }
            """);
        var customized = Path.Combine(_dir, "custom.json");
        File.WriteAllText(customized, """
            { "Version": 1, "Display": { "TranslationFontSize": 22 }, "Overlay": { "FontSize": 30, "TextColor": "#FFE45C" } }
            """);

        var upgraded = new SettingsStore(oldDefaults, new PlainTextProtector()).Load();
        var kept = new SettingsStore(customized, new PlainTextProtector()).Load();

        Assert.Equal(AppSettings.CurrentVersion, upgraded.Version);
        Assert.Equal(DisplayOptions.DefaultTranslationFontSize, upgraded.Display.TranslationFontSize);
        Assert.Equal(OverlayOptions.DefaultFontSize, upgraded.Overlay.FontSize);
        Assert.Equal(OverlayOptions.DefaultTextColor, upgraded.Overlay.TextColor);
        Assert.Equal(22, kept.Display.TranslationFontSize);
        Assert.Equal(30, kept.Overlay.FontSize);
        Assert.Equal("#FFE45C", kept.Overlay.TextColor);
    }

    [Fact]
    public void Version_2_default_partial_interval_moves_to_the_new_default_but_custom_values_stay()
    {
        Directory.CreateDirectory(_dir);
        var untouched = Path.Combine(_dir, "v2-default.json");
        File.WriteAllText(untouched, """{ "Version": 2, "Pipeline": { "PartialIntervalMs": 350 } }""");
        var custom = Path.Combine(_dir, "v2-custom.json");
        File.WriteAllText(custom, """{ "Version": 2, "Pipeline": { "PartialIntervalMs": 500 } }""");

        Assert.Equal(PipelineOptions.DefaultPartialIntervalMs, new SettingsStore(untouched, new PlainTextProtector()).Load().Pipeline.PartialIntervalMs);
        Assert.Equal(500, new SettingsStore(custom, new PlainTextProtector()).Load().Pipeline.PartialIntervalMs);
    }

    [Fact]
    public void Prompt_placeholders_are_rendered()
    {
        Assert.Contains("Japanese", PromptBuilder.RenderSystemPrompt("Translate to {lang}", Languages.Get("ja-JP")));
        Assert.Equal("to Korean", PromptBuilder.RenderSystemPrompt("to {0}", Languages.Get("ko-KR")));
        Assert.Contains("Simplified Chinese", PromptBuilder.RenderSystemPrompt("", Languages.Get("zh-CN")));
    }
}
