using System.Text.RegularExpressions;

namespace DriveSync.Core.Indexing;

/// <summary>
/// A search box query split into name terms and the ext:, in: and type: operators.
/// "lecture ext:pdf in:semester" gives terms [lecture], Ext "pdf", In "semester".
/// </summary>
public sealed partial class ParsedQuery
{
    public List<string> Terms { get; } = new();

    /// <summary>The words exactly as typed (minus operators), for the close-match fallback and highlighting.</summary>
    public List<string> Words { get; } = new();

    public string? Ext { get; private set; }
    public string? In { get; private set; }
    public string? Type { get; private set; }

    public bool HasOperators => Ext != null || In != null || Type != null;
    public bool IsEmpty => Terms.Count == 0 && !HasOperators;

    [GeneratedRegex(@"^(ext|in|type):(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex OperatorPattern();

    public static ParsedQuery Parse(string? query)
    {
        var parsed = new ParsedQuery();
        if (string.IsNullOrWhiteSpace(query))
            return parsed;

        foreach (string word in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            Match m = OperatorPattern().Match(word);
            if (m.Success)
            {
                string key = m.Groups[1].Value.ToLowerInvariant();
                string value = m.Groups[2].Value.Trim().ToLowerInvariant();
                if (key == "ext")
                    parsed.Ext = value.TrimStart('*').TrimStart('.');
                else if (key == "in")
                    parsed.In = value.Replace('/', '\\');
                else
                    parsed.Type = value;
                continue;
            }

            parsed.Words.Add(word.ToLowerInvariant());
            foreach (string token in Tokenizer.Tokens(word))
                if (!parsed.Terms.Contains(token))
                    parsed.Terms.Add(token);
        }
        return parsed;
    }

    /// <summary>The query with every ext:, in: and type: word removed ("Search without operators").</summary>
    public static string StripOperators(string query) =>
        string.Join(' ', query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Where(w => !OperatorPattern().IsMatch(w)));
}
