using System.Text.RegularExpressions;

namespace DriveSync.Core;

/// <summary>Everything the preview panel shows for one file.</summary>
public sealed record FileSummary(
    string Name,
    string FullPath,
    string Details,
    IReadOnlyList<string> Keywords,
    string Summary,
    string Preview,
    bool IsImage);

/// <summary>
/// Builds a short keyword-based summary of a file:
///
///   Keywords: the words that appear most often, ignoring common words like "the" and "and".
///   Summary:  the 2–3 sentences that contain the most of those keywords
///             (a simple "frequency-based extractive summary"), shown in their original order.
///   Preview:  the first part of the text.
/// </summary>
public static class SummaryService
{
    private const int KeywordCount = 8;
    private const int SummarySentences = 3;
    private const int PreviewCharacters = 3000;

    private static readonly Regex WordPattern = new(@"\p{L}[\p{L}\p{N}']+", RegexOptions.Compiled);
    private static readonly Regex LineContinuation = new(@"\r?\n(?=[ \t]*\p{Ll})", RegexOptions.Compiled);
    private static readonly Regex SentenceSplit = new(@"(?<=[.!?])\s+|\r?\n", RegexOptions.Compiled);

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".java", ".py", ".js", ".ts", ".c", ".cpp", ".h", ".css", ".sql", ".ps1", ".sh", ".bat", ".json", ".xml",
    };

    // Very common English words that say nothing about what a file is about.
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "are", "but", "not", "you", "all", "any", "can", "had", "her", "was", "one",
        "our", "out", "has", "have", "him", "his", "how", "its", "may", "new", "now", "old", "see", "two",
        "who", "did", "get", "got", "let", "say", "she", "too", "use", "used", "using", "this", "that",
        "with", "from", "they", "them", "then", "than", "there", "their", "these", "those", "what", "when",
        "where", "which", "while", "will", "would", "could", "should", "been", "being", "into", "onto",
        "also", "such", "some", "more", "most", "very", "each", "other", "only", "over", "about", "after",
        "before", "between", "both", "same", "just", "your", "yours", "were", "does", "done", "doing",
        "here", "upon", "within", "without", "through", "because", "make", "made", "many", "much", "well",
        "like", "it's", "don't", "can't", "per", "via", "etc",
    };

    public static FileSummary Summarize(string path)
    {
        var info = new FileInfo(path);
        string details = $"{DescribeType(info.Extension)} · {FormatSize(info.Exists ? info.Length : 0)} · " +
                         $"modified {info.LastWriteTime:dd MMM yyyy HH:mm}";

        if (TextExtractor.ImageExtensions.Contains(info.Extension))
            return new FileSummary(info.Name, path, details, Array.Empty<string>(), "This is an image; see the preview below.", "", IsImage: true);

        string? text = TextExtractor.TryExtract(path, out string? note);
        if (string.IsNullOrWhiteSpace(text))
            return new FileSummary(info.Name, path, details, Array.Empty<string>(), note ?? "This file has no readable text.", "", IsImage: false);

        // Count how often each meaningful word appears.
        var counts = new Dictionary<string, int>();
        foreach (Match match in WordPattern.Matches(text))
        {
            string word = match.Value.ToLowerInvariant();
            if (word.Length < 3 || StopWords.Contains(word))
                continue;
            counts[word] = counts.GetValueOrDefault(word) + 1;
        }

        List<string> keywords = counts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key)
            .Take(KeywordCount)
            .Select(pair => pair.Key)
            .ToList();

        // Code has no real sentences, so describe its size instead.
        string summary = CodeExtensions.Contains(info.Extension)
            ? $"Source code, {text.Split('\n').Length:N0} lines. Most used names are shown as keywords."
            : PickSummarySentences(text, counts);
        string preview = text.Length > PreviewCharacters ? text[..PreviewCharacters] + "…" : text;

        return new FileSummary(info.Name, path, details, keywords, summary, preview.Trim(), IsImage: false);
    }

    /// <summary>Scores every sentence by how many frequent words it contains and keeps the best few.</summary>
    private static string PickSummarySentences(string text, Dictionary<string, int> counts)
    {
        // Many text files break long sentences over several lines. A line break followed
        // by a lowercase letter is almost always such a continuation, so join those first.
        string joined = LineContinuation.Replace(text, " ");

        var sentences = SentenceSplit.Split(joined)
            .Select(s => Regex.Replace(s, @"\s+", " ").Trim())
            .Select((sentence, position) => (sentence, position))
            .Where(s => s.sentence.Length > 0)
            .ToList();

        var scored = new List<(string Sentence, int Position, double Score)>();
        foreach (var (sentence, position) in sentences)
        {
            var words = WordPattern.Matches(sentence).Select(m => m.Value.ToLowerInvariant()).ToList();
            if (words.Count < 4 || words.Count > 60)
                continue; // too short to say much, or not really a sentence (a table, a code block)

            double score = words.Sum(w => counts.GetValueOrDefault(w)) / Math.Sqrt(words.Count);
            scored.Add((sentence, position, score));
        }

        if (scored.Count == 0)
        {
            // No proper sentences (a list, a spreadsheet…): show the beginning instead.
            string start = Regex.Replace(text, @"\s+", " ").Trim();
            return start.Length > 300 ? start[..300] + "…" : start;
        }

        return string.Join(" ", scored
            .OrderByDescending(s => s.Score)
            .Take(SummarySentences)
            .OrderBy(s => s.Position)
            .Select(s => s.Sentence.Length > 300 ? s.Sentence[..300] + "…" : s.Sentence));
    }

    public static string FormatSize(long bytes)
    {
        string[] units = { "bytes", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} bytes" : $"{size:0.#} {units[unit]}";
    }

    private static string DescribeType(string extension) => extension.ToLowerInvariant() switch
    {
        ".docx" or ".doc" => "Word document",
        ".pptx" or ".ppt" => "PowerPoint presentation",
        ".xlsx" or ".xls" => "Excel workbook",
        ".pdf" => "PDF document",
        ".txt" => "Text file",
        ".md" => "Markdown file",
        ".cs" => "C# source file",
        "" => "File",
        _ when TextExtractor.ImageExtensions.Contains(extension) => "Image",
        _ => extension.TrimStart('.').ToUpperInvariant() + " file",
    };
}
