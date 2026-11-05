namespace DriveSync.Core.Parsing;

/// <summary>A paragraph or heading, in reading order. <see cref="Level"/> is 0 for body text, 1–3 for headings.</summary>
public readonly record struct Block(string Text, int Level);

/// <summary>One line of the Outline tab: a heading, or a namespace / type / member for code.</summary>
public sealed record OutlineItem(int Level, string Text, string Tag);

/// <summary>What a reader extracted from a file.</summary>
public sealed class ParsedDocument
{
    public List<Block> Blocks { get; } = new();
    public List<OutlineItem> Outline { get; } = new();

    /// <summary>Sentences describing structure, used when the text has few real sentences (spreadsheets, CSV).</summary>
    public List<string> Overview { get; } = new();

    /// <summary>Spreadsheets and CSV: start the summary with the structure, then add content.</summary>
    public bool PreferOverview { get; set; }

    public bool IsCode { get; set; }
    public string? Language { get; set; }

    /// <summary>The whole text, for code and plain text files.</summary>
    public string RawText { get; set; } = "";

    /// <summary>Number of characters read (reading stops at a limit).</summary>
    public int Characters { get; set; }

    public void AddHeading(string text, int level)
    {
        text = Clean(text);
        if (text.Length == 0) return;
        level = Math.Clamp(level, 1, 3);
        Blocks.Add(new Block(text, level));
        Outline.Add(new OutlineItem(level, text, "H" + level));
        Characters += text.Length;
    }

    public void AddParagraph(string text)
    {
        text = Clean(text);
        if (text.Length == 0) return;
        Blocks.Add(new Block(text, 0));
        Characters += text.Length;
    }

    private static string Clean(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
