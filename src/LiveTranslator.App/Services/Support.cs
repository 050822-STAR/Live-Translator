using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

using LiveTranslator.Core.Providers;
using LiveTranslator.Core.Settings;

namespace LiveTranslator.App.Services;

/// <summary>Encrypts API keys with Windows DPAPI, readable only by the current Windows account.</summary>
public sealed class DpapiProtector : ISecretProtector
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LiveTranslator.ApiKey.v1");

    public string Protect(string plaintext) =>
        Prefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser));

    public string Unprotect(string value)
    {
        if (!value.StartsWith(Prefix, StringComparison.Ordinal))
            return value; // hand-edited plain key: accepted, encrypted on next save
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value[Prefix.Length..]), Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }
}

/// <summary>Appends finished translations to a daily TSV file without blocking the caller.</summary>
public sealed class HistoryLog : IAsyncDisposable
{
    private readonly Channel<(DateTime At, string Source, string Translation)> _queue =
        Channel.CreateUnbounded<(DateTime, string, string)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;

    public HistoryLog(string directory)
    {
        Directory = directory;
        _writer = Task.Run(WriteLoopAsync);
    }

    public string Directory { get; }

    public void Append(string source, string translation) => _queue.Writer.TryWrite((DateTime.Now, source, translation));

    private async Task WriteLoopAsync()
    {
        await foreach (var (at, source, translation) in _queue.Reader.ReadAllAsync())
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                var path = Path.Combine(Directory, at.ToString("yyyy-MM-dd") + ".tsv");
                await File.AppendAllTextAsync(path, $"{at:HH:mm:ss}\t{Clean(source)}\t{Clean(translation)}\n", Encoding.UTF8);
            }
            catch (IOException)
            {
                // Logging must never disturb translation.
            }
        }
    }

    private static string Clean(string s) => s.Replace('\t', ' ').Replace('\n', ' ').Replace("\r", "");

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _writer.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }
}

/// <summary>Stands in for a misconfigured profile so the problem is shown where translations appear.</summary>
public sealed class MisconfiguredProvider : ITranslationProvider
{
    private readonly string _problem;

    public MisconfiguredProvider(string name, string problem)
    {
        Name = name;
        _problem = problem;
    }

    public string Name { get; }

    public async IAsyncEnumerable<string> TranslateStreamAsync(TranslationRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        throw new ProviderException($"“{Name}”配置有误：{_problem}");
#pragma warning disable CS0162 // an iterator needs a yield statement
        yield break;
#pragma warning restore CS0162
    }

    public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
}
