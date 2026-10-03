namespace LiveTranslator.Core.Pipeline;

public sealed record Segmentation(IReadOnlyList<string> Complete, string Partial);

public static class Segmenter
{
    private const string SentenceEnds = ".?!。？！…";
    private const string Closers = "\"'”’)]）」』";

    // "Mr. Smith" must not be split after "Mr.". Words that also end spoken sentences ("No.", "etc.")
    // are deliberately absent: a wrong split costs one short fragment, but a wrong merge can leave a
    // finished sentence waiting for an end that never comes.
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "vs", "fig", "approx",
    };

    public static bool IsSentenceEnd(char c) => SentenceEnds.Contains(c);

    /// <summary>Splits text into finished sentences (punctuation kept) and the trailing unfinished part.</summary>
    public static Segmentation Split(string text)
    {
        var complete = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!IsSentenceEnd(c))
                continue;
            if (c == '.' && (IsDecimalPoint(text, i) || IsAbbreviation(text, start, i)))
                continue;

            int end = i + 1;
            while (end < text.Length && (IsSentenceEnd(text[end]) || Closers.Contains(text[end])))
                end++;
            var sentence = text[start..end].Trim();
            if (sentence.Length > 0 && sentence.Any(char.IsLetterOrDigit))
                complete.Add(sentence);
            start = end;
            i = end - 1;
        }
        return new Segmentation(complete, text[start..].Trim());
    }

    private static bool IsDecimalPoint(string text, int i) =>
        i > 0 && i + 1 < text.Length && char.IsDigit(text[i - 1]) && char.IsDigit(text[i + 1]);

    private static bool IsAbbreviation(string text, int sentenceStart, int dot)
    {
        int wordStart = dot;
        while (wordStart > sentenceStart && (char.IsLetter(text[wordStart - 1]) || text[wordStart - 1] == '.'))
            wordStart--;
        return dot > wordStart && Abbreviations.Contains(text[wordStart..dot]);
    }
}
