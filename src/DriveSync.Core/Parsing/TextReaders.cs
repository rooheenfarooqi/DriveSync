using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Outline;
using W = DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace DriveSync.Core.Parsing;

/// <summary>
/// Reads text and structure from the file types DriveSync summarizes. Every reader stops after
/// <see cref="MaxChars"/> characters so a huge file can't stall the preview.
/// </summary>
public static partial class TextReaders
{
    public const int MaxChars = 400_000;

    private static readonly HashSet<string> Unsupported = new(StringComparer.OrdinalIgnoreCase)
    {
        "doc", "dot", "xls", "xlsb", "ppt", "pps", "pages", "numbers", "key", "wpd",
    };

    /// <summary>True if DriveSync can read text from files with this extension.</summary>
    public static bool CanRead(string extension, FileKind kind)
    {
        if (Unsupported.Contains(extension)) return false;
        return kind is FileKind.Doc or FileKind.Pdf or FileKind.Sheet or FileKind.Slides or FileKind.Code or FileKind.Text;
    }

    /// <summary>Reads a file opened by the caller (so locks are detected in one place).</summary>
    public static ParsedDocument Read(Stream stream, string extension, FileKind kind, CancellationToken ct)
    {
        string ext = extension.ToLowerInvariant();
        return ext switch
        {
            "docx" or "docm" or "dotx" => ReadDocx(stream, ct),
            "pptx" or "pptm" or "ppsx" => ReadPptx(stream, ct),
            "xlsx" or "xlsm" => ReadXlsx(stream, ct),
            "pdf" => ReadPdf(stream, ct),
            "odt" or "ods" or "odp" => ReadOdf(stream, ct),
            "csv" or "tsv" => ReadCsv(stream, ext == "tsv" ? '\t' : ',', ct),
            "rtf" => ReadRtf(stream),
            "html" or "htm" => ReadHtml(stream),
            "md" or "markdown" => ReadMarkdown(stream),
            _ when kind == FileKind.Code => CodeOutline.Read(stream, ext),
            _ => ReadPlain(stream),
        };
    }

    // ------------------------------------------------------------------
    // Word
    // ------------------------------------------------------------------

    private static ParsedDocument ReadDocx(Stream stream, CancellationToken ct)
    {
        var doc = new ParsedDocument();
        using WordprocessingDocument word = WordprocessingDocument.Open(stream, false);
        if (word.MainDocumentPart == null) return doc;

        // Style id -> heading level, from the style names ("heading 1", "Title") or their outline level.
        var levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (W.Style style in word.MainDocumentPart!.StyleDefinitionsPart?.Styles?.Elements<W.Style>() ?? Enumerable.Empty<W.Style>())
        {
            string? id = style.StyleId?.Value;
            string name = style.StyleName?.Val?.Value?.ToLowerInvariant() ?? "";
            if (id == null) continue;
            int? outline = style.StyleParagraphProperties?.OutlineLevel?.Val?.Value;
            if (name == "title") levels[id] = 1;
            else if (name.StartsWith("heading ", StringComparison.Ordinal) && int.TryParse(name[8..], out int n)) levels[id] = n;
            else if (outline is int o && o < 9) levels[id] = o + 1;
        }

        // Stream paragraph by paragraph instead of loading the whole document tree: much faster for big files.
        using OpenXmlReader reader = OpenXmlReader.Create(word.MainDocumentPart!);
        while (reader.Read())
        {
            if (reader.ElementType != typeof(W.Paragraph) || !reader.IsStartElement) continue;
            var p = (W.Paragraph)reader.LoadCurrentElement()!;
            ct.ThrowIfCancellationRequested();
            string text = ParagraphText(p);
            if (string.IsNullOrWhiteSpace(text)) continue;
            int level = 0;
            string? styleId = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
            if (styleId != null && levels.TryGetValue(styleId, out int l)) level = l;
            else if (p.ParagraphProperties?.OutlineLevel?.Val?.Value is int ol && ol < 9) level = ol + 1;
            if (level > 0 && text.Length < 200) doc.AddHeading(text, level);
            else doc.AddParagraph(text);
            if (doc.Characters > MaxChars) break;
        }
        return doc;
    }

    /// <summary>Paragraph text with line breaks and tabs turned into spaces (InnerText glues them together).</summary>
    private static string ParagraphText(W.Paragraph p)
    {
        var sb = new StringBuilder();
        foreach (OpenXmlElement e in p.Descendants())
        {
            switch (e)
            {
                case W.Text t: sb.Append(t.Text); break;
                case W.TabChar or W.Break or W.CarriageReturn: sb.Append(' '); break;
                case W.NoBreakHyphen: sb.Append('-'); break;
            }
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // PowerPoint: slide titles become the outline
    // ------------------------------------------------------------------

    private static ParsedDocument ReadPptx(Stream stream, CancellationToken ct)
    {
        var doc = new ParsedDocument();
        using PresentationDocument deck = PresentationDocument.Open(stream, false);
        PresentationPart? part = deck.PresentationPart;
        var slideIds = part?.Presentation?.SlideIdList?.Elements<P.SlideId>() ?? Enumerable.Empty<P.SlideId>();
        int number = 0;
        foreach (P.SlideId id in slideIds)
        {
            ct.ThrowIfCancellationRequested();
            number++;
            if (id.RelationshipId?.Value is not string rel || part!.GetPartById(rel) is not SlidePart slide) continue;
            string? title = null;
            var bodyText = new List<string>();
            foreach (P.Shape shape in slide.Slide?.Descendants<P.Shape>() ?? Enumerable.Empty<P.Shape>())
            {
                var ph = shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape;
                bool isTitle = ph?.Type?.Value == P.PlaceholderValues.Title || ph?.Type?.Value == P.PlaceholderValues.CenteredTitle;
                foreach (A.Paragraph para in shape.TextBody?.Elements<A.Paragraph>() ?? Enumerable.Empty<A.Paragraph>())
                {
                    string text = string.Concat(para.Descendants<A.Text>().Select(t => t.Text));
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    if (isTitle && title == null) title = text;
                    else bodyText.Add(text);
                }
            }
            doc.AddHeading(title ?? $"Slide {number}", 1);
            foreach (string t in bodyText) doc.AddParagraph(t);
            if (slide.NotesSlidePart?.NotesSlide is { } notes)
                foreach (A.Paragraph para in notes.Descendants<A.Paragraph>())
                {
                    string text = string.Concat(para.Descendants<A.Text>().Select(t => t.Text));
                    if (text.Length > 20 && !Regex.IsMatch(text, @"^\d+$")) doc.AddParagraph(text);
                }
            if (doc.Characters > MaxChars) break;
        }
        if (number > 0) doc.Overview.Add($"Presentation with {number} slide{(number == 1 ? "" : "s")}.");
        return doc;
    }

    // ------------------------------------------------------------------
    // Excel: sheet names are the outline; header rows describe the content
    // ------------------------------------------------------------------

    private static ParsedDocument ReadXlsx(Stream stream, CancellationToken ct)
    {
        var doc = new ParsedDocument { PreferOverview = true };
        using SpreadsheetDocument book = SpreadsheetDocument.Open(stream, false);
        WorkbookPart? wb = book.WorkbookPart;
        if (wb?.Workbook?.Sheets == null) return doc;
        var shared = wb.SharedStringTablePart?.SharedStringTable?.Elements<S.SharedStringItem>().Select(i => i.InnerText).ToList() ?? new List<string>();

        var sheetNames = new List<string>();
        foreach (S.Sheet sheet in wb.Workbook.Sheets.Elements<S.Sheet>())
        {
            ct.ThrowIfCancellationRequested();
            string name = sheet.Name?.Value ?? "Sheet";
            sheetNames.Add(name);
            doc.AddHeading(name, 1);
            if (sheet.Id?.Value is not string rel || wb.GetPartById(rel) is not WorksheetPart ws) continue;

            int rows = 0;
            List<string>? header = null;
            using OpenXmlReader reader = OpenXmlReader.Create(ws);
            while (reader.Read())
            {
                if (reader.ElementType != typeof(S.Row) || !reader.IsStartElement) continue;
                var row = (S.Row)reader.LoadCurrentElement()!;
                var values = row.Elements<S.Cell>().Select(c => CellText(c, shared)).Where(v => v.Length > 0).ToList();
                if (values.Count == 0) continue;
                rows++;
                header ??= values;
                if (rows <= 400 && doc.Characters < MaxChars)
                    doc.AddParagraph(string.Join(" · ", values));
            }
            if (rows > 0)
                doc.Overview.Add($"{name} has {rows:N0} row{(rows == 1 ? "" : "s")}" +
                                 (header != null ? $"; the first row reads {string.Join(", ", header.Take(8))}." : "."));
        }
        doc.Overview.Insert(0, $"Workbook with {sheetNames.Count} sheet{(sheetNames.Count == 1 ? "" : "s")}: {string.Join(", ", sheetNames)}.");
        return doc;
    }

    private static string CellText(S.Cell cell, List<string> shared)
    {
        string value = cell.CellValue?.Text ?? cell.InlineString?.InnerText ?? "";
        if (cell.DataType?.Value == S.CellValues.SharedString && int.TryParse(value, out int i) && i >= 0 && i < shared.Count)
            return shared[i].Trim();
        return value.Trim();
    }

    // ------------------------------------------------------------------
    // PDF: bookmarks or large-font lines become the outline
    // ------------------------------------------------------------------

    private static ParsedDocument ReadPdf(Stream stream, CancellationToken ct)
    {
        var doc = new ParsedDocument();
        using PdfDocument pdf = PdfDocument.Open(stream);

        bool hasBookmarks = pdf.TryGetBookmarks(out Bookmarks? bookmarks) && bookmarks.GetNodes().Any();
        if (hasBookmarks)
        {
            foreach (BookmarkNode node in bookmarks!.GetNodes())
                if (node.Level < 3 && !string.IsNullOrWhiteSpace(node.Title))
                    doc.Outline.Add(new OutlineItem(node.Level + 1, node.Title.Trim(), "H" + (node.Level + 1)));
        }

        // Lines with their font size, page by page.
        var lines = new List<(string Text, double Size, double Gap)>();
        foreach (Page page in pdf.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            var words = page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
            var ordered = words.OrderByDescending(w => Math.Round(w.BoundingBox.Bottom)).ThenBy(w => w.BoundingBox.Left).ToList();
            var current = new List<Word>();
            double lastBottom = double.NaN;
            double previousBottom = double.NaN;
            foreach (Word w in ordered)
            {
                double bottom = w.BoundingBox.Bottom;
                double height = Math.Max(1, w.BoundingBox.Height);
                if (current.Count > 0 && Math.Abs(bottom - lastBottom) > height * 0.5)
                {
                    AddLine(current, previousBottom);
                    previousBottom = lastBottom;
                    current.Clear();
                }
                current.Add(w);
                lastBottom = bottom;
            }
            if (current.Count > 0) AddLine(current, previousBottom);
            lines.Add(("", 0, double.MaxValue)); // page break
            if (lines.Sum(l => l.Text.Length) > MaxChars) break;
        }

        void AddLine(List<Word> words, double previousBottom)
        {
            string text = string.Join(' ', words.Select(w => w.Text));
            double size = words.SelectMany(w => w.Letters).Select(l => l.PointSize).DefaultIfEmpty(0).Average();
            double bottom = words[0].BoundingBox.Bottom;
            double gap = double.IsNaN(previousBottom) ? 0 : Math.Abs(previousBottom - bottom);
            lines.Add((text, size, gap));
        }

        // Body size = the size most text is set in. Bigger, short lines are headings.
        var bySize = lines.Where(l => l.Text.Length > 0).GroupBy(l => Math.Round(l.Size, 1))
            .Select(g => (Size: g.Key, Chars: g.Sum(x => x.Text.Length))).OrderByDescending(g => g.Chars).ToList();
        double body = bySize.Count > 0 ? bySize[0].Size : 0;
        var headingSizes = bySize.Where(g => g.Size >= body * 1.15).Select(g => g.Size).OrderByDescending(s => s).Take(3).ToList();

        var paragraph = new StringBuilder();
        void FlushParagraph()
        {
            if (paragraph.Length > 0) doc.AddParagraph(paragraph.ToString());
            paragraph.Clear();
        }
        double bodyGap = lines.Where(l => l.Gap > 0 && l.Gap < double.MaxValue).Select(l => l.Gap).DefaultIfEmpty(12).OrderBy(g => g).ElementAt(lines.Count(l => l.Gap > 0 && l.Gap < double.MaxValue) / 2);
        foreach (var (text, size, gap) in lines)
        {
            if (text.Length == 0) { FlushParagraph(); continue; }
            int rank = headingSizes.IndexOf(Math.Round(size, 1));
            bool heading = rank >= 0 && text.Length <= 90 && text.Any(char.IsLetter) && !text.EndsWith('.');
            if (heading)
            {
                FlushParagraph();
                if (hasBookmarks) doc.Blocks.Add(new Block(text, rank + 1));
                else doc.AddHeading(text, rank + 1);
                continue;
            }
            if (gap > bodyGap * 1.6) FlushParagraph();
            if (paragraph.Length > 0)
            {
                if (paragraph[^1] == '-') paragraph.Length--;   // hyphenated line break
                else paragraph.Append(' ');
            }
            paragraph.Append(text);
        }
        FlushParagraph();
        doc.Overview.Add($"PDF with {pdf.NumberOfPages} page{(pdf.NumberOfPages == 1 ? "" : "s")}.");
        return doc;
    }

    // ------------------------------------------------------------------
    // OpenDocument (.odt, .ods, .odp)
    // ------------------------------------------------------------------

    private static ParsedDocument ReadOdf(Stream stream, CancellationToken ct)
    {
        var doc = new ParsedDocument();
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        ZipArchiveEntry? content = zip.GetEntry("content.xml");
        if (content == null) return doc;
        using var reader = XmlReader.Create(content.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.LocalName == "h")
            {
                int level = int.TryParse(reader.GetAttribute("outline-level", reader.LookupNamespace("text") ?? ""), out int l) ? l : 1;
                doc.AddHeading(reader.ReadElementContentAsString(), level);
            }
            else if (reader.LocalName == "p")
            {
                doc.AddParagraph(reader.ReadElementContentAsString());
            }
            else if (reader.LocalName == "table" && reader.GetAttribute("name", reader.LookupNamespace("table") ?? "") is { } table)
            {
                doc.AddHeading(table, 1);
            }
            if (doc.Characters > MaxChars) break;
        }
        return doc;
    }

    // ------------------------------------------------------------------
    // Plain text, Markdown, CSV, HTML, RTF
    // ------------------------------------------------------------------

    public static string ReadAllText(Stream stream, int maxChars = MaxChars)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[maxChars];
        int read = reader.ReadBlock(buffer, 0, buffer.Length);
        return new string(buffer, 0, read);
    }

    private static ParsedDocument ReadPlain(Stream stream)
    {
        var doc = new ParsedDocument();
        string text = ReadAllText(stream);
        doc.RawText = text;
        foreach (string para in Regex.Split(text, @"\r?\n\s*\r?\n"))
            doc.AddParagraph(para);
        return doc;
    }

    private static ParsedDocument ReadMarkdown(Stream stream)
    {
        var doc = new ParsedDocument();
        string text = ReadAllText(stream);
        doc.RawText = text;
        var paragraph = new StringBuilder();
        bool inCode = false;
        void Flush()
        {
            if (paragraph.Length > 0) doc.AddParagraph(paragraph.ToString());
            paragraph.Clear();
        }
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) { Flush(); inCode = !inCode; continue; }
            if (inCode) continue;
            Match h = Regex.Match(line, @"^(#{1,6})\s+(.+?)\s*#*\s*$");
            if (h.Success)
            {
                Flush();
                doc.AddHeading(StripMarkdown(h.Groups[2].Value), h.Groups[1].Value.Length);
                continue;
            }
            if (string.IsNullOrWhiteSpace(line)) { Flush(); continue; }
            if (Regex.IsMatch(line, @"^\s*([-*+]|\d+\.)\s+")) { Flush(); paragraph.Append(Regex.Replace(line, @"^\s*([-*+]|\d+\.)\s+", "")); Flush(); continue; }
            if (paragraph.Length > 0) paragraph.Append(' ');
            paragraph.Append(StripMarkdown(line));
        }
        Flush();
        return doc;
    }

    private static string StripMarkdown(string s) =>
        Regex.Replace(Regex.Replace(s, @"!?\[([^\]]*)\]\([^)]*\)", "$1"), @"[*_`]+", "");

    private static ParsedDocument ReadCsv(Stream stream, char separator, CancellationToken ct)
    {
        var doc = new ParsedDocument { PreferOverview = true };
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        string? headerLine = reader.ReadLine();
        if (headerLine == null) return doc;
        string[] header = SplitCsv(headerLine, separator);
        int rows = 0;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Length == 0) continue;
            rows++;
            if (rows <= 300 && doc.Characters < MaxChars)
                doc.AddParagraph(string.Join(" · ", SplitCsv(line, separator).Where(v => v.Length > 0)));
            if (rows > 2_000_000) break;
        }
        doc.Outline.AddRange(header.Where(h => h.Length > 0).Take(40).Select(h => new OutlineItem(1, h, "col")));
        doc.Overview.Add($"Table with {rows:N0} row{(rows == 1 ? "" : "s")} and {header.Length} column{(header.Length == 1 ? "" : "s")}.");
        doc.Overview.Add($"Columns: {string.Join(", ", header.Where(h => h.Length > 0).Take(10))}{(header.Length > 10 ? "…" : "")}.");
        return doc;
    }

    private static string[] SplitCsv(string line, char separator)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"') { if (quoted && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; } else quoted = !quoted; }
            else if (c == separator && !quoted) { values.Add(current.ToString().Trim()); current.Clear(); }
            else current.Append(c);
        }
        values.Add(current.ToString().Trim());
        return values.ToArray();
    }

    [GeneratedRegex(@"<(script|style|noscript)[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptPattern();

    private static ParsedDocument ReadHtml(Stream stream)
    {
        var doc = new ParsedDocument();
        string html = ScriptPattern().Replace(ReadAllText(stream), " ");
        Match title = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (title.Success) doc.AddHeading(WebUtility.HtmlDecode(Regex.Replace(title.Groups[1].Value, "<[^>]+>", " ")), 1);
        // Split on block elements, keeping headings as headings.
        foreach (Match m in Regex.Matches(html, @"<(h[1-3]|p|li|td|th|div|section|article|blockquote|pre)[^>]*>(.*?)</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            string tag = m.Groups[1].Value.ToLowerInvariant();
            string text = WebUtility.HtmlDecode(Regex.Replace(m.Groups[2].Value, "<[^>]+>", " "));
            if (tag.Length == 2 && tag[0] == 'h') doc.AddHeading(text, tag[1] - '0' + (title.Success ? 1 : 0));
            else doc.AddParagraph(text);
            if (doc.Characters > MaxChars) break;
        }
        if (doc.Blocks.Count <= 1)
            doc.AddParagraph(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")));
        return doc;
    }

    private static ParsedDocument ReadRtf(Stream stream)
    {
        var doc = new ParsedDocument();
        string rtf = ReadAllText(stream);
        // Drop destinations like {\*\generator …}, font and colour tables, then control words.
        string text = Regex.Replace(rtf, @"\{\\\*[^{}]*\}|\{\\(fonttbl|colortbl|stylesheet|info)[^{}]*(\{[^{}]*\}[^{}]*)*\}", "");
        text = Regex.Replace(text, @"\\par[d]?\b ?", "\n");
        text = Regex.Replace(text, @"\\'([0-9a-fA-F]{2})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
        text = Regex.Replace(text, @"\\[a-zA-Z]+-?\d* ?|[{}]", "");
        foreach (string para in text.Split('\n'))
            doc.AddParagraph(para);
        return doc;
    }
}
