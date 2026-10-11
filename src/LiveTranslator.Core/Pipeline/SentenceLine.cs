using System.Text;

namespace LiveTranslator.Core.Pipeline;

/// <summary>
/// One line of the transcript: a sentence that may have been translated fragment by fragment, plus
/// the clause still being spoken while it continues this sentence. Fragments are joined in order, so
/// the translation grows along the line instead of each clause starting a line of its own.
/// </summary>
public sealed class SentenceLine
{
    private readonly SortedList<long, EntrySnapshot> _fragments = [];
    private string _liveSource = "";
    private string _liveTranslation = "";

    public SentenceLine(long group) => Group = group;

    public long Group { get; }

    public void Apply(EntrySnapshot snapshot) => _fragments[snapshot.Id] = snapshot;

    /// <summary>Shows the unfinished clause after the finished fragments; empty text removes it.</summary>
    public void SetLive(string source, string translation)
    {
        _liveSource = source;
        _liveTranslation = source.Length > 0 ? translation : "";
    }

    public bool HasLive => _liveSource.Length > 0;

    public string Source => Join(_fragments.Values.Select(f => f.Source).Append(_liveSource));

    public string Translation => Join(_fragments.Values.Select(Shown).Append(_liveTranslation));

    public string? Error => _fragments.Values.Select(f => f.Error).FirstOrDefault(e => e is not null);

    public bool IsWorking => HasLive || _fragments.Values.Any(f => f.Status is EntryStatus.Pending or EntryStatus.Streaming);

    public bool IsProvisional => _fragments.Values.Any(f => f.IsProvisional || f.Status == EntryStatus.Pending);

    /// <summary>The most recent fragment, whose timing the line shows.</summary>
    public EntrySnapshot? Latest => _fragments.Count > 0 ? _fragments.Values[^1] : null;

    private static string Shown(EntrySnapshot f) =>
        f.Translation.Length > 0 ? f.Translation : f.Status == EntryStatus.Pending ? "…" : "";

    /// <summary>
    /// Joins pieces of one sentence: directly when either side is CJK text or punctuation, with a
    /// space between words of space-delimited languages.
    /// </summary>
    public static string Join(IEnumerable<string> pieces)
    {
        var sb = new StringBuilder();
        foreach (var raw in pieces)
        {
            var piece = raw.Trim();
            if (piece.Length == 0)
                continue;
            if (sb.Length > 0 && !TextNormalizer.IsCjk(sb[^1]) && !TextNormalizer.IsCjk(piece[0]))
                sb.Append(' ');
            sb.Append(piece);
        }
        return sb.ToString();
    }
}
