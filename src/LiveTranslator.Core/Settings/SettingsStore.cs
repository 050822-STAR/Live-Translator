using System.Text.Json;
using System.Text.Json.Serialization;

using LiveTranslator.Core.Models;

namespace LiveTranslator.Core.Settings;

/// <summary>Encrypts secrets at rest. The desktop app plugs in Windows DPAPI.</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);

    /// <exception cref="Exception">The value cannot be decrypted (e.g. another Windows account).</exception>
    string Unprotect(string protectedValue);
}

public sealed class PlainTextProtector : ISecretProtector
{
    public string Protect(string plaintext) => plaintext;
    public string Unprotect(string protectedValue) => protectedValue;
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ISecretProtector _protector;
    private readonly object _writeGate = new();

    public SettingsStore(string path, ISecretProtector protector)
    {
        Path = path;
        _protector = protector;
    }

    public string Path { get; }

    /// <summary>Set when the last <see cref="Load"/> found an unreadable file and moved it aside.</summary>
    public string? RecoveredBackupPath { get; private set; }

    public AppSettings Load()
    {
        RecoveredBackupPath = null;
        AppSettings settings;
        if (!File.Exists(Path))
        {
            settings = new AppSettings();
        }
        else
        {
            try
            {
                using var stream = File.Open(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                settings = JsonSerializer.Deserialize<AppSettings>(stream, Options) ?? new AppSettings();
            }
            catch (JsonException)
            {
                // Keep the broken file for the user instead of silently overwriting it.
                var backup = Path + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Move(Path, backup, overwrite: true);
                RecoveredBackupPath = backup;
                settings = new AppSettings();
            }
        }

        settings.Migrate();
        settings.Normalize();
        foreach (var profile in settings.Profiles)
        {
            if (string.IsNullOrEmpty(profile.ApiKey))
                continue;
            try
            {
                profile.ApiKey = _protector.Unprotect(profile.ApiKey);
            }
            catch (Exception)
            {
                profile.ApiKey = ""; // encrypted for another account/machine: ask the user again
            }
        }
        return settings;
    }

    /// <summary>Writes atomically (temp file + rename) so a crash never leaves a half-written file.</summary>
    public void Save(AppSettings settings)
    {
        var copy = Clone(settings);
        foreach (var profile in copy.Profiles)
        {
            if (!string.IsNullOrEmpty(profile.ApiKey))
                profile.ApiKey = _protector.Protect(profile.ApiKey);
        }

        lock (_writeGate)
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var temp = Path + ".tmp";
            using (var stream = File.Create(temp))
                JsonSerializer.Serialize(stream, copy, Options);
            File.Move(temp, Path, overwrite: true);
        }
    }

    public static AppSettings Clone(AppSettings settings) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.SerializeToUtf8Bytes(settings, Options), Options)!;
}
