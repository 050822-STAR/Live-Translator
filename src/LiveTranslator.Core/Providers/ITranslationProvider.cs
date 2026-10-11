using System.Net;

using LiveTranslator.Core.Models;

namespace LiveTranslator.Core.Providers;

public sealed record ContextPair(string Source, string Translation);

/// <param name="Context">Recently finished sentence pairs, oldest first.</param>
/// <param name="SystemPrompt">Fully rendered system prompt (no placeholders left).</param>
public sealed record TranslationRequest(
    string Text,
    LanguageInfo Target,
    IReadOnlyList<ContextPair> Context,
    string SystemPrompt)
{
    /// <summary>Optional timing record the provider fills in as the response arrives.</summary>
    public RequestTrace? Trace { get; init; }
}

public interface ITranslationProvider
{
    string Name { get; }

    /// <summary>
    /// Streams translated text fragments. Implementations must yield each fragment as soon as it
    /// arrives; non-streaming services yield once.
    /// </summary>
    /// <exception cref="ProviderException">The service failed; the message is safe to show to users.</exception>
    IAsyncEnumerable<string> TranslateStreamAsync(TranslationRequest request, CancellationToken ct = default);

    /// <summary>
    /// Opens (or refreshes) pooled connections so upcoming requests skip DNS/TCP/TLS setup.
    /// <paramref name="connections"/> is how many requests are expected to overlap: on HTTP/1.1
    /// each of them needs its own connection (HTTP/2 multiplexes them over one anyway).
    /// </summary>
    Task WarmUpAsync(int connections = 1, CancellationToken ct = default);

    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default);
}

public sealed class ProviderException : Exception
{
    public ProviderException(string message, HttpStatusCode? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode? StatusCode { get; }

    /// <summary>The vendor's own error text, without the status prefix and hint.</summary>
    public string? Detail { get; init; }
}
