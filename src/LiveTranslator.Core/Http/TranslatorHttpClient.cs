using System.Net;

namespace LiveTranslator.Core.Http;

public static class TranslatorHttpClient
{
    /// <summary>
    /// One long-lived client shared by every provider so TCP/TLS connections are pooled and reused.
    /// Per-request headers are always set on the <see cref="HttpRequestMessage"/>, never on the client,
    /// so concurrent requests to different vendors cannot clobber each other's credentials.
    /// </summary>
    /// <param name="proxy">Empty = system proxy, "none"/"direct" = no proxy, otherwise a proxy URL.</param>
    public static HttpClient Create(string? proxy = null)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(8),
            AutomaticDecompression = DecompressionMethods.All,
            EnableMultipleHttp2Connections = true,
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            MaxConnectionsPerServer = 16,
        };

        var p = proxy?.Trim() ?? "";
        if (p.Equals("none", StringComparison.OrdinalIgnoreCase) || p.Equals("direct", StringComparison.OrdinalIgnoreCase))
        {
            handler.UseProxy = false;
        }
        else if (p.Length > 0)
        {
            if (!Uri.TryCreate(p, UriKind.Absolute, out var proxyUri))
                throw new FormatException($"代理地址无效: {p}");
            handler.Proxy = new WebProxy(proxyUri);
            handler.UseProxy = true;
        }

        return new HttpClient(handler)
        {
            // Each provider enforces its own deadline; a global timeout would cut long streams short.
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
    }
}
