using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace DriveSync.Core;

/// <summary>
/// Reads the text inside a file so it can be summarised and previewed.
///
/// Module 1 supports plain text and source code, and the Office formats
/// .docx, .pptx and .xlsx (these are zip files full of XML, so no extra library is needed).
/// PDF is planned for the parser module.
/// </summary>
public static class TextExtractor
{
    /// <summary>Only the start of very large files is read, to keep the preview fast.</summary>
    public const int MaxCharacters = 200_000;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".log", ".json", ".xml", ".yaml", ".yml", ".ini", ".config",
        ".html", ".htm", ".css", ".js", ".ts", ".cs", ".java", ".py", ".c", ".cpp", ".h",
        ".sql", ".bat", ".ps1", ".sh", ".tex", ".rtf",
    };

    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".ico",
    };

    /// <summary>
    /// Returns the file's text, or null when the type isn't supported.
    /// <paramref name="note"/> explains why there is no text, when there is none.
    /// </summary>
    public static string? TryExtract(string path, out string? note)
    {
        note = null;
        string extension = Path.GetExtension(path);

        try
        {
            switch (extension.ToLowerInvariant())
            {
                case ".docx":
                    return FromZipXml(path, name => name == "word/document.xml", paragraphTag: "p");
                case ".pptx":
                    return FromZipXml(path, name => name.StartsWith("ppt/slides/slide") && name.EndsWith(".xml"), paragraphTag: "p");
                case ".xlsx":
                    return FromZipXml(path, name => name == "xl/sharedStrings.xml", paragraphTag: "si");
                case ".pdf":
                    note = "PDF text preview is planned for the parser module.";
                    return null;
                case ".html" or ".htm":
                    return StripHtml(ReadTextStart(path));
            }

            if (TextExtensions.Contains(extension) || LooksLikeText(path))
                return ReadTextStart(path);

            note = "No text preview for this type of file.";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or XmlException)
        {
            note = "Could not read the file: " + ex.Message;
            return null;
        }
    }

    /// <summary>Reads up to <see cref="MaxCharacters"/> characters, detecting the encoding (UTF-8, UTF-16…).</summary>
    private static string ReadTextStart(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        char[] buffer = new char[MaxCharacters];
        int read = reader.ReadBlock(buffer, 0, buffer.Length);
        return new string(buffer, 0, read);
    }

    /// <summary>A file with no zero bytes in its first 4 KB is very likely text.</summary>
    private static bool LooksLikeText(string path)
    {
        using var stream = File.OpenRead(path);
        byte[] buffer = new byte[4096];
        int read = stream.Read(buffer, 0, buffer.Length);
        return read > 0 && Array.IndexOf(buffer, (byte)0, 0, read) < 0;
    }

    /// <summary>
    /// Office files are zip archives. The text sits inside &lt;t&gt; elements of some XML parts;
    /// we collect it and start a new line at the end of each paragraph.
    /// </summary>
    private static string FromZipXml(string path, Func<string, bool> isWantedPart, string paragraphTag)
    {
        using var zip = ZipFile.OpenRead(path);
        var parts = zip.Entries
            .Where(e => isWantedPart(e.FullName))
            .OrderBy(e => NumberInName(e.FullName)) // slide2 before slide10
            .ToList();

        var text = new StringBuilder();
        foreach (ZipArchiveEntry part in parts)
        {
            using var reader = XmlReader.Create(part.Open());
            while (reader.Read() && text.Length < MaxCharacters)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t")
                    text.Append(reader.ReadElementContentAsString());
                if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == paragraphTag)
                    text.AppendLine();
            }
            text.AppendLine();
        }
        return text.ToString();
    }

    private static int NumberInName(string name)
    {
        Match match = Regex.Match(name, @"(\d+)\.xml$");
        return match.Success ? int.Parse(match.Groups[1].Value) : 0;
    }

    private static string StripHtml(string html)
    {
        html = Regex.Replace(html, @"<(script|style)[^>]*>.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<[^>]+>", " ");
        return System.Net.WebUtility.HtmlDecode(html);
    }
}
