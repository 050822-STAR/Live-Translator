using System.Runtime.CompilerServices;
using System.Text;

namespace LiveTranslator.Core.Http;

public static class StreamReaders
{
    /// <summary>
    /// Yields the <c>data</c> payload of each Server-Sent Event as soon as it is readable.
    /// </summary>
    /// <remarks>
    /// Some OpenAI-compatible gateways omit the blank line that terminates an event. A single-line
    /// payload that is already a complete JSON object (or <c>[DONE]</c>) is therefore emitted
    /// immediately instead of waiting for a terminator that may never come — otherwise streaming
    /// would silently degrade into "everything at the end".
    /// </remarks>
    public static async IAsyncEnumerable<string> ReadSseDataAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096);
        var data = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                if (data.Length > 0)
                    yield return data.ToString();
                yield break;
            }

            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return data.ToString();
                    data.Clear();
                }
                continue;
            }

            if (line[0] == ':' || !line.StartsWith("data:", StringComparison.Ordinal))
                continue; // comments, event:, id:, retry: — every payload we consume carries its own type

            var value = line.AsSpan(5);
            if (value.Length > 0 && value[0] == ' ')
                value = value[1..];

            if (data.Length == 0 && IsSelfContained(value))
            {
                yield return value.ToString();
                continue;
            }

            if (data.Length > 0)
                data.Append('\n');
            data.Append(value);
        }
    }

    /// <summary>Yields each non-empty line of a newline-delimited JSON stream.</summary>
    public static async IAsyncEnumerable<string> ReadLinesAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
                yield return line;
        }
    }

    private static bool IsSelfContained(ReadOnlySpan<char> value)
    {
        var trimmed = value.Trim();
        if (trimmed.SequenceEqual("[DONE]"))
            return true;
        return trimmed.Length >= 2 && trimmed[0] == '{' && trimmed[^1] == '}';
    }
}
