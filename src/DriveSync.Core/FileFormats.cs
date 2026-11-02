namespace DriveSync.Core;

/// <summary>One file type the user can pick before searching, for example "pdf".</summary>
/// <param name="Extension">Lower-case extension without the dot, for example "pdf".</param>
/// <param name="Description">What kind of file it is, for example "PDF document". Empty if unknown.</param>
/// <param name="Count">How many files of this type are in the index.</param>
public sealed record FileFormat(string Extension, string Description, int Count);

/// <summary>
/// The list of file types shown in the "File type" drop-down.
///
/// It combines a built-in catalogue of well-known formats (so the list is useful
/// even before the first scan has finished) with every extension actually found
/// in the index, so it covers all the file types on this PC.
/// </summary>
public static class FileFormats
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        // Documents
        ["pdf"] = "PDF document",
        ["doc"] = "Word document (old)",
        ["docx"] = "Word document",
        ["odt"] = "OpenDocument text",
        ["rtf"] = "Rich text",
        ["txt"] = "Plain text",
        ["md"] = "Markdown",
        ["tex"] = "LaTeX document",
        ["epub"] = "E-book",
        // Spreadsheets and data
        ["xls"] = "Excel workbook (old)",
        ["xlsx"] = "Excel workbook",
        ["ods"] = "OpenDocument spreadsheet",
        ["csv"] = "Comma-separated values",
        ["tsv"] = "Tab-separated values",
        ["json"] = "JSON data",
        ["xml"] = "XML data",
        ["yaml"] = "YAML data",
        ["yml"] = "YAML data",
        ["sql"] = "SQL script",
        ["db"] = "Database",
        ["sqlite"] = "SQLite database",
        ["accdb"] = "Access database",
        // Presentations
        ["ppt"] = "PowerPoint (old)",
        ["pptx"] = "PowerPoint presentation",
        ["odp"] = "OpenDocument presentation",
        // Images
        ["png"] = "PNG image",
        ["jpg"] = "JPEG image",
        ["jpeg"] = "JPEG image",
        ["gif"] = "GIF image",
        ["bmp"] = "Bitmap image",
        ["tif"] = "TIFF image",
        ["tiff"] = "TIFF image",
        ["webp"] = "WebP image",
        ["heic"] = "HEIC photo",
        ["svg"] = "SVG vector image",
        ["ico"] = "Icon",
        ["psd"] = "Photoshop image",
        ["ai"] = "Illustrator file",
        ["raw"] = "Camera raw photo",
        // Audio
        ["mp3"] = "MP3 audio",
        ["wav"] = "WAV audio",
        ["flac"] = "FLAC audio",
        ["aac"] = "AAC audio",
        ["m4a"] = "M4A audio",
        ["ogg"] = "Ogg audio",
        ["wma"] = "Windows Media audio",
        // Video
        ["mp4"] = "MP4 video",
        ["mkv"] = "MKV video",
        ["avi"] = "AVI video",
        ["mov"] = "QuickTime video",
        ["wmv"] = "Windows Media video",
        ["webm"] = "WebM video",
        ["flv"] = "Flash video",
        // Code
        ["py"] = "Python source",
        ["ipynb"] = "Jupyter notebook",
        ["cs"] = "C# source",
        ["java"] = "Java source",
        ["js"] = "JavaScript",
        ["ts"] = "TypeScript",
        ["jsx"] = "React JavaScript",
        ["tsx"] = "React TypeScript",
        ["c"] = "C source",
        ["cpp"] = "C++ source",
        ["h"] = "C/C++ header",
        ["hpp"] = "C++ header",
        ["go"] = "Go source",
        ["rs"] = "Rust source",
        ["php"] = "PHP script",
        ["rb"] = "Ruby script",
        ["kt"] = "Kotlin source",
        ["swift"] = "Swift source",
        ["dart"] = "Dart source",
        ["r"] = "R script",
        ["m"] = "MATLAB / Objective-C",
        ["html"] = "Web page",
        ["htm"] = "Web page",
        ["css"] = "Stylesheet",
        ["scss"] = "Sass stylesheet",
        ["xaml"] = "XAML layout",
        ["sh"] = "Shell script",
        ["bat"] = "Batch file",
        ["cmd"] = "Command script",
        ["ps1"] = "PowerShell script",
        ["sln"] = "Visual Studio solution",
        ["csproj"] = "C# project",
        ["ini"] = "Settings file",
        ["cfg"] = "Configuration file",
        ["log"] = "Log file",
        // Archives
        ["zip"] = "ZIP archive",
        ["rar"] = "RAR archive",
        ["7z"] = "7-Zip archive",
        ["tar"] = "TAR archive",
        ["gz"] = "Gzip archive",
        ["iso"] = "Disc image",
        // Programs
        ["exe"] = "Program",
        ["msi"] = "Windows installer",
        ["dll"] = "Program library",
        ["apk"] = "Android app",
        ["jar"] = "Java archive",
        ["lnk"] = "Shortcut",
        // Fonts
        ["ttf"] = "TrueType font",
        ["otf"] = "OpenType font",
        ["woff"] = "Web font",
    };

    /// <summary>
    /// Builds the drop-down list: types found on this PC first (most files first),
    /// then the remaining well-known types alphabetically, with a count of 0.
    /// </summary>
    public static List<FileFormat> Build(IReadOnlyDictionary<string, int> countsInIndex)
    {
        var formats = new Dictionary<string, FileFormat>(StringComparer.OrdinalIgnoreCase);

        foreach (var (extension, count) in countsInIndex)
        {
            if (!LooksLikeRealExtension(extension))
                continue;
            string ext = extension.ToLowerInvariant();
            formats[ext] = new FileFormat(ext, Known.GetValueOrDefault(ext, ""), count);
        }

        foreach (var (ext, description) in Known)
            formats.TryAdd(ext, new FileFormat(ext, description, 0));

        return formats.Values
            .OrderByDescending(f => f.Count)
            .ThenBy(f => f.Extension, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Turns what the user typed (".pdf", "*.pdf", " PDF ") into a plain extension ("pdf").</summary>
    public static string Normalize(string typed) => typed.Trim().TrimStart('*').TrimStart('.').Trim().ToLowerInvariant();

    /// <summary>
    /// Skips "extensions" that are really part of a name, such as the "1234-abc" in
    /// "backup.1234-abc", so the list stays readable.
    /// </summary>
    private static bool LooksLikeRealExtension(string ext) =>
        ext.Length is > 0 and <= 10 &&
        ext.Any(char.IsLetter) &&
        ext.All(char.IsLetterOrDigit);
}
