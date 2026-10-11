using System.Diagnostics;

namespace LiveTranslator.Core.Providers;

/// <summary>
/// Where one request's time went, filled in by the provider as the response arrives. Separates
/// network time (until the response headers) from model time (until the first token), and shows
/// whether the answer really streamed or arrived in one burst (few chunks, first ≈ last).
/// </summary>
/// <remarks>
/// Thread-safe. With a hedged request both copies report into the same trace; the first report of
/// each stage wins, so times are measured from the first copy's start.
/// </remarks>
public sealed class RequestTrace
{
    private long _start;
    private long _headers;
    private long _firstChunk;
    private long _lastChunk;
    private long _end;
    private int _chunks;
    private string? _finishReason;

    public void MarkStart() => Interlocked.CompareExchange(ref _start, Stopwatch.GetTimestamp(), 0);

    public void MarkHeaders() => Interlocked.CompareExchange(ref _headers, Stopwatch.GetTimestamp(), 0);

    public void MarkChunk()
    {
        var now = Stopwatch.GetTimestamp();
        Interlocked.CompareExchange(ref _firstChunk, now, 0);
        Volatile.Write(ref _lastChunk, now);
        Interlocked.Increment(ref _chunks);
    }

    /// <summary>The answer stream ended normally.</summary>
    public void MarkEnd() => Interlocked.CompareExchange(ref _end, Stopwatch.GetTimestamp(), 0);

    /// <summary>The service's own reason for ending the answer (e.g. "stop", "length", "content_filter").</summary>
    public void SetFinishReason(string? reason)
    {
        if (!string.IsNullOrEmpty(reason))
            Volatile.Write(ref _finishReason, reason);
    }

    public TraceSnapshot Snapshot()
    {
        var start = Volatile.Read(ref _start);
        return new TraceSnapshot(
            Since(start, Volatile.Read(ref _headers)),
            Since(start, Volatile.Read(ref _firstChunk)),
            Since(start, Volatile.Read(ref _lastChunk)),
            Since(start, Volatile.Read(ref _end)),
            Volatile.Read(ref _chunks),
            Volatile.Read(ref _finishReason));
    }

    private static int? Since(long start, long at) =>
        start == 0 || at == 0 ? null : (int)Math.Round(Stopwatch.GetElapsedTime(start, at).TotalMilliseconds);
}

/// <param name="HeadersMs">From sending the request to its response headers: connection, upload and gateway time.</param>
/// <param name="FirstChunkMs">From sending the request to the first piece of answer text.</param>
/// <param name="LastChunkMs">From sending the request to the last piece of answer text.</param>
/// <param name="EndMs">From sending the request to the end of the answer stream.</param>
/// <param name="Chunks">How many pieces the answer arrived in.</param>
public sealed record TraceSnapshot(int? HeadersMs, int? FirstChunkMs, int? LastChunkMs, int? EndMs, int Chunks, string? FinishReason);
