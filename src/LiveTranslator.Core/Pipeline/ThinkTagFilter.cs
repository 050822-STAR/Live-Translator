using System.Text;

namespace LiveTranslator.Core.Pipeline;

/// <summary>
/// Removes <c>&lt;think&gt;…&lt;/think&gt;</c> blocks from a token stream, including tags split
/// across chunks, without delaying ordinary text by more than a possible partial tag.
/// </summary>
public sealed class ThinkTagFilter
{
    private const string Open = "<think>";
    private const string Close = "</think>";

    private readonly StringBuilder _pending = new();
    private bool _inside;

    public string Push(string chunk)
    {
        _pending.Append(chunk);
        var output = new StringBuilder();
        while (true)
        {
            var buffer = _pending.ToString();
            var tag = _inside ? Close : Open;
            var index = buffer.IndexOf(tag, StringComparison.Ordinal);
            if (index >= 0)
            {
                if (!_inside)
                    output.Append(buffer, 0, index);
                _pending.Clear().Append(buffer, index + tag.Length, buffer.Length - index - tag.Length);
                _inside = !_inside;
                continue;
            }

            int keep = PartialTagSuffix(buffer, tag);
            if (!_inside)
                output.Append(buffer, 0, buffer.Length - keep);
            _pending.Clear().Append(buffer, buffer.Length - keep, keep);
            return output.ToString();
        }
    }

    /// <summary>Returns text held back at end of stream (an unterminated think block is dropped).</summary>
    public string Flush()
    {
        var rest = _inside ? "" : _pending.ToString();
        _pending.Clear();
        return rest;
    }

    private static int PartialTagSuffix(string buffer, string tag)
    {
        for (int len = Math.Min(tag.Length - 1, buffer.Length); len > 0; len--)
        {
            if (buffer.AsSpan(buffer.Length - len).SequenceEqual(tag.AsSpan(0, len)))
                return len;
        }
        return 0;
    }
}
