using System.Text;
using System.Text.RegularExpressions;

namespace LiveTranslator.Core.Pipeline;

/// <summary>
/// Cleans raw LiveCaptions text. The regex rules are adapted from LiveCaptions-Translator
/// (Apache-2.0, https://github.com/SakiRinn/LiveCaptions-Translator).
/// </summary>
public static partial class TextNormalizer
{
    // Remove the "." inside acronyms ("U.S." -> "US.") so they are not mistaken for sentence ends.
    [GeneratedRegex(@"([A-Z])\s*\.\s*([A-Z])(?![A-Za-z]+)")]
    private static partial Regex Acronym();

    // ...but keep a space when the acronym is followed by a word.
    [GeneratedRegex(@"([A-Z])\s*\.\s*([A-Z])(?=[A-Za-z]+)")]
    private static partial Regex AcronymWithWords();

    // "US. economy": an acronym's final dot followed by a lowercase word does not end the sentence.
    [GeneratedRegex(@"\b([A-Z]{2,})\.\s+(?=[a-z])")]
    private static partial Regex AcronymTrailingDot();

    [GeneratedRegex(@"\s*([.!?,])\s*")]
    private static partial Regex PunctuationSpace();

    [GeneratedRegex(@"\s*([。！？，、])\s*")]
    private static partial Regex CjkPunctuationSpace();

    // Never strip the dot in decimals ("3.5"), which PunctuationSpace would otherwise pad.
    [GeneratedRegex(@"(\d)\. (\d)")]
    private static partial Regex DecimalPoint();

    /// <summary>Lines at least this many UTF-8 bytes long are treated as finished sentences at a line break.</summary>
    private const int ParagraphBreakBytes = 40;

    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";
        var text = JoinLines(raw);
        text = Acronym().Replace(text, "$1$2");
        text = AcronymWithWords().Replace(text, "$1 $2");
        text = AcronymTrailingDot().Replace(text, "$1 ");
        text = PunctuationSpace().Replace(text, "$1 ");
        text = CjkPunctuationSpace().Replace(text, "$1");
        text = DecimalPoint().Replace(text, "$1.$2");
        return text.Trim();
    }

    /// <summary>
    /// LiveCaptions starts a new line after a pause (very often for Japanese). A long line that
    /// ends without punctuation is a finished utterance, so it gets a period; short ones are joined.
    /// </summary>
    private static string JoinLines(string raw)
    {
        if (raw.IndexOf('\n') < 0)
            return raw.Replace('\r', ' ');
        var lines = raw.Split('\n');
        var sb = new StringBuilder(raw.Length + 8);
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
                continue;
            if (sb.Length > 0 && !IsCjk(sb[^1]) && !IsCjk(line[0]))
                sb.Append(' ');
            sb.Append(line);
            if (i == lines.Length - 1)
                continue;

            var last = line[^1];
            if (Segmenter.IsSentenceEnd(last) || last is ',' or '，' or '、')
                continue;
            var cjk = IsCjk(last);
            if (Encoding.UTF8.GetByteCount(line) >= ParagraphBreakBytes)
                sb.Append(cjk ? "。" : ".");
            else
                sb.Append(cjk ? "，" : ",");
        }
        return sb.ToString();
    }

    public static bool IsCjk(char ch) =>
        (ch >= '一' && ch <= '鿿') ||   // CJK Unified Ideographs
        (ch >= '㐀' && ch <= '䶿') ||   // Extension A
        (ch >= '　' && ch <= '〿') ||   // CJK Symbols and Punctuation
        (ch >= '぀' && ch <= 'ヿ') ||   // Hiragana + Katakana
        (ch >= 'ㇰ' && ch <= 'ㇿ') ||   // Katakana Phonetic Extensions
        (ch >= '가' && ch <= '힯') ||   // Hangul Syllables
        (ch >= '＀' && ch <= '￯');     // Full-width forms
}
