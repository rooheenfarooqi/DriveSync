using System.Text.RegularExpressions;

namespace DriveSync.Core.Parsing;

/// <summary>The Summary tab's content.</summary>
public sealed class Summary
{
    public List<string> Sentences { get; init; } = new();
    public List<string> Keywords { get; init; } = new();
    public int Words { get; init; }
    public bool IsCode { get; init; }
    public string? Language { get; init; }
}

/// <summary>
/// Picks the key sentences of a document.
///
/// Every word that isn't a common word ("the", "and"…) is counted. Each sentence is scored by the
/// counts of its words, divided by its length raised to 0.55 (so long sentences don't win just by
/// being long), and boosted by its position: the first sentence of a section gets +35% and the first
/// two sentences of the document +30%. The best ones are shown in their original order.
/// </summary>
public static partial class ExtractiveSummarizer
{
    private static readonly HashSet<string> Stop = new(
        ("a an the and or of to in on for with by is are was were be been this that these those it its as at from into than then so such " +
         "can will would should may might must not no do does did has have had which who whom what when where while also each every one two " +
         "three more most other some any only own same very just over under after before between both during through up down out off about " +
         "their they them there here our we you your file files").Split(' '), StringComparer.Ordinal);

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceBreak();

    [GeneratedRegex(@"\p{L}[\p{L}\-]{2,}")]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"\S+")]
    private static partial Regex AnyWord();

    public static Summary Summarize(ParsedDocument doc, int sentenceCount)
    {
        if (doc.IsCode)
            return SummarizeCode(doc);

        var sentences = new List<(string Text, int Index, bool FirstInSection)>();
        bool first = true;
        int totalWords = 0;
        var tf = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (Block block in doc.Blocks)
        {
            totalWords += AnyWord().Count(block.Text);
            foreach (Match w in WordPattern().Matches(block.Text.ToLowerInvariant()))
                if (!Stop.Contains(w.Value)) tf[w.Value] = tf.GetValueOrDefault(w.Value) + 1;

            if (block.Level > 0) { first = true; continue; }
            foreach (string s in SentenceBreak().Split(block.Text))
            {
                string t = s.Trim();
                if (t.Length <= 20 || !t.Any(char.IsLetter)) continue;
                if (t.Length > 600) t = t[..600].TrimEnd() + "…";
                sentences.Add((t, sentences.Count, first));
                first = false;
            }
        }

        var scored = sentences.Select(s =>
        {
            var words = WordPattern().Matches(s.Text.ToLowerInvariant()).Select(m => m.Value).Where(w => !Stop.Contains(w)).ToList();
            double score = words.Sum(w => tf.GetValueOrDefault(w)) / Math.Pow(Math.Max(words.Count, 1), 0.55);
            score *= 1 + (s.FirstInSection ? 0.35 : 0) + (s.Index < 2 ? 0.3 : 0);
            return (s.Text, s.Index, Score: score);
        }).ToList();

        var top = new List<string>();
        if (doc.PreferOverview)
            top.AddRange(doc.Overview.Take(Math.Max(1, sentenceCount - 1)));
        int n = Math.Min(sentenceCount - top.Count, scored.Count);
        top.AddRange(scored.OrderByDescending(s => s.Score).Take(n).OrderBy(s => s.Index).Select(s => s.Text));

        // Spreadsheets, CSV and slides often have no real sentences: describe the structure instead.
        if (top.Count < Math.Min(2, sentenceCount))
        {
            foreach (string o in doc.Overview)
                if (top.Count < sentenceCount && !top.Contains(o)) top.Add(o);
            foreach (Block b in doc.Blocks.Where(b => b.Level == 0 && AnyWord().Count(b.Text) >= 3))
            {
                if (top.Count >= sentenceCount) break;
                string t = b.Text.Length > 240 ? b.Text[..240].TrimEnd() + "…" : b.Text;
                if (!top.Contains(t)) top.Add(t);
            }
        }

        var keywords = tf.Where(kv => kv.Key.Length > 3).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                         .Take(7).Select(kv => kv.Key).ToList();
        return new Summary { Sentences = top, Keywords = keywords, Words = totalWords };
    }

    private static Summary SummarizeCode(ParsedDocument doc)
    {
        string text = doc.RawText;
        string lang = doc.Language ?? "Code";
        var types = doc.Outline.Where(o => o.Tag == "type").Select(o => LastWordBeforeBrace(o.Text)).Where(t => t.Length > 0).Distinct().ToList();
        var members = doc.Outline.Where(o => o.Tag == "fn").Select(o => MemberName(o.Text)).Where(t => t.Length > 0).Distinct().ToList();
        int lines = text.Length == 0 ? 0 : text.Count(c => c == '\n') + 1;

        var sentences = new List<string>();
        string? comment = CodeOutline.DocComment(text);
        if (!string.IsNullOrWhiteSpace(comment)) sentences.Add(comment);
        sentences.Add(types.Count > 0
            ? $"Declares {(types.Count == 1 ? "the type" : types.Count + " types:")} {string.Join(", ", types.Take(8))}{(types.Count > 8 ? "…" : "")}."
            : $"{lang} file with {lines:N0} line{(lines == 1 ? "" : "s")}.");
        if (members.Count > 0)
            sentences.Add($"Members: {string.Join(", ", members.Take(8))}{(members.Count > 8 ? "…" : "")}.");

        var keywords = new List<string> { lang };
        keywords.AddRange(types.Take(3));
        keywords.AddRange(members.Take(2).Where(m => !keywords.Contains(m)));
        return new Summary
        {
            Sentences = sentences,
            Keywords = keywords,
            Words = AnyWord().Count(text),
            IsCode = true,
            Language = lang,
        };
    }

    private static string LastWordBeforeBrace(string declaration)
    {
        Match m = Regex.Match(declaration, @"\b(?:class|interface|struct|record|enum|trait|impl|object|protocol|extension|mixin|type)\s+([A-Za-z_]\w*)");
        return m.Success ? m.Groups[1].Value : "";
    }

    private static string MemberName(string signature)
    {
        int paren = signature.IndexOf('(');
        string head = paren > 0 ? signature[..paren] : signature;
        Match m = Regex.Match(head, @"([A-Za-z_$][\w$]*)\s*(?:<[^>]*>)?\s*(?:=\s*(?:async\s*)?)?$");
        return m.Success ? m.Groups[1].Value : "";
    }
}
