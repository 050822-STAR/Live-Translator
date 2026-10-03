using System.Text;

namespace LiveTranslator.Core.Pipeline;

/// <summary>
/// Comparison helpers that ignore case, spacing and punctuation — the parts of a caption that
/// speech recognition keeps rewriting while the words stay the same.
/// </summary>
public static class TextKey
{
    public static string Of(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>True when one key extends the other or they differ by a few recognition fixes.</summary>
    public static bool IsSameUtterance(string a, string b, double threshold = 0.75)
    {
        if (a.Length == 0 || b.Length == 0)
            return false;
        if (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal))
            return true;
        return Math.Min(a.Length, b.Length) >= 8 && Similarity(a, b) >= threshold;
    }

    public static double Similarity(string a, string b)
    {
        int max = Math.Max(a.Length, b.Length);
        return max == 0 ? 1.0 : 1.0 - (double)Levenshtein(a, b) / max;
    }

    public static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        if (a.Length > b.Length)
            (a, b) = (b, a);

        Span<int> previous = a.Length < 512 ? stackalloc int[a.Length + 1] : new int[a.Length + 1];
        Span<int> current = a.Length < 512 ? stackalloc int[a.Length + 1] : new int[a.Length + 1];
        for (int i = 0; i <= a.Length; i++)
            previous[i] = i;
        for (int j = 1; j <= b.Length; j++)
        {
            current[0] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[i] = Math.Min(Math.Min(current[i - 1] + 1, previous[i] + 1), previous[i - 1] + cost);
            }
            var tmp = previous;
            previous = current;
            current = tmp;
        }
        return previous[a.Length];
    }

    /// <summary>Approximate visual length: CJK characters count double (they carry a word each).</summary>
    public static int WeightedLength(string text)
    {
        int n = 0;
        foreach (var c in text)
            n += TextNormalizer.IsCjk(c) ? 2 : char.IsWhiteSpace(c) ? 0 : 1;
        return n;
    }
}
