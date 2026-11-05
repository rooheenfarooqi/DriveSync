namespace DriveSync.Core;

/// <summary>What kind of file something is. Decides the glyph colour, the Type filter group and how it is parsed.</summary>
public enum FileKind : byte { Doc, Pdf, Sheet, Slides, Code, Text, Image, Db, Zip, Other }

/// <summary>The groups in the Search page's Type filter and the Add folder dialog's file type checkboxes.</summary>
public enum TypeGroup : byte { All, Doc, Pdf, Sheet, Slides, Code, Text, Image, Other }

public static class FileKinds
{
    private static readonly Dictionary<string, FileKind> ByExtension = Build();

    private static Dictionary<string, FileKind> Build()
    {
        var map = new Dictionary<string, FileKind>(StringComparer.OrdinalIgnoreCase);
        void Add(FileKind kind, string list)
        {
            foreach (string ext in list.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                map[ext] = kind;
        }

        Add(FileKind.Doc, "docx doc docm dotx odt rtf wpd pages");
        Add(FileKind.Pdf, "pdf");
        Add(FileKind.Sheet, "xlsx xls xlsm xlsb ods csv tsv numbers");
        Add(FileKind.Slides, "pptx ppt pptm ppsx odp key");
        Add(FileKind.Code, "cs dart py pyw js mjs cjs ts jsx tsx json jsonc yaml yml xml sql csproj sln vbproj fsproj props targets " +
                           "java kt kts scala groovy gradle c h cc cpp cxx hpp hh go rs php rb swift m mm r ipynb " +
                           "html htm css scss sass less vue svelte xaml axaml razor cshtml sh bash zsh bat cmd ps1 psm1 " +
                           "lua pl pm vb fs fsx toml dockerfile makefile cmake proto graphql tf hcl asm");
        Add(FileKind.Text, "md markdown txt log ini cfg conf tex rst nfo env properties text srt vtt");
        Add(FileKind.Image, "png jpg jpeg jfif gif bmp tif tiff webp heic heif svg ico psd raw cr2 nef arw dng avif");
        Add(FileKind.Db, "db sqlite sqlite3 db3 accdb mdb dbf parquet sdf");
        Add(FileKind.Zip, "zip rar 7z tar gz tgz bz2 xz zst iso cab dll exe msi msix apk jar war nupkg dmg img vhd vhdx");
        return map;
    }

    /// <summary>Kind from an extension without the dot, for example "pdf".</summary>
    public static FileKind FromExtension(ReadOnlySpan<char> extension)
    {
        if (extension.Length == 0 || extension.Length > 12)
            return FileKind.Other;
        return ByExtension.TryGetValue(extension.ToString(), out FileKind kind) ? kind : FileKind.Other;
    }

    public static FileKind FromName(string name) => FromExtension(ExtensionOf(name));

    /// <summary>The extension without the dot ("Report.PDF" gives "PDF"), or empty.</summary>
    public static ReadOnlySpan<char> ExtensionOf(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1 ? ReadOnlySpan<char>.Empty : name.AsSpan(dot + 1);
    }

    /// <summary>Long label, used in the Details tab: "PDF document".</summary>
    public static string Label(FileKind kind) => kind switch
    {
        FileKind.Doc => "Word document",
        FileKind.Pdf => "PDF document",
        FileKind.Sheet => "Spreadsheet",
        FileKind.Slides => "Presentation",
        FileKind.Code => "Source code",
        FileKind.Text => "Text / Markdown",
        FileKind.Image => "Image",
        FileKind.Db => "Database",
        FileKind.Zip => "Archive",
        _ => "File",
    };

    /// <summary>Short label, used in the preview's type pill: "PDF".</summary>
    public static string Short(FileKind kind) => kind switch
    {
        FileKind.Doc => "Document",
        FileKind.Pdf => "PDF",
        FileKind.Sheet => "Spreadsheet",
        FileKind.Slides => "Presentation",
        FileKind.Code => "Code",
        FileKind.Text => "Text",
        FileKind.Image => "Image",
        FileKind.Db => "Database",
        FileKind.Zip => "Archive",
        _ => "File",
    };

    /// <summary>The key used by the type: operator ("type:code", "type:sheet").</summary>
    public static string Key(FileKind kind) => kind switch
    {
        FileKind.Doc => "doc",
        FileKind.Pdf => "pdf",
        FileKind.Sheet => "sheet",
        FileKind.Slides => "slides",
        FileKind.Code => "code",
        FileKind.Text => "text",
        FileKind.Image => "image",
        FileKind.Db => "db",
        FileKind.Zip => "zip",
        _ => "other",
    };

    public static bool InGroup(FileKind kind, TypeGroup group) => group switch
    {
        TypeGroup.All => true,
        TypeGroup.Doc => kind == FileKind.Doc,
        TypeGroup.Pdf => kind == FileKind.Pdf,
        TypeGroup.Sheet => kind == FileKind.Sheet,
        TypeGroup.Slides => kind == FileKind.Slides,
        TypeGroup.Code => kind == FileKind.Code,
        TypeGroup.Text => kind == FileKind.Text,
        TypeGroup.Image => kind == FileKind.Image,
        _ => kind is FileKind.Db or FileKind.Zip or FileKind.Other,
    };

    public static TypeGroup GroupOf(FileKind kind)
    {
        foreach (TypeGroup group in AllGroups)
            if (group != TypeGroup.All && InGroup(kind, group))
                return group;
        return TypeGroup.Other;
    }

    public static readonly TypeGroup[] AllGroups =
        { TypeGroup.All, TypeGroup.Doc, TypeGroup.Pdf, TypeGroup.Sheet, TypeGroup.Slides, TypeGroup.Code, TypeGroup.Text, TypeGroup.Image, TypeGroup.Other };

    /// <summary>Menu and checkbox labels, exactly as in the design.</summary>
    public static string GroupLabel(TypeGroup group) => group switch
    {
        TypeGroup.All => "All types",
        TypeGroup.Doc => "Documents",
        TypeGroup.Pdf => "PDFs",
        TypeGroup.Sheet => "Spreadsheets",
        TypeGroup.Slides => "Presentations",
        TypeGroup.Code => "Code",
        TypeGroup.Text => "Text & Markdown",
        TypeGroup.Image => "Images",
        _ => "Archives & data",
    };

    /// <summary>Kinds matching a type: operator value: "code", "sheet", "spread", "pdf", "img"…</summary>
    public static List<FileKind> MatchTypeOperator(string value)
    {
        var kinds = new List<FileKind>();
        foreach (FileKind kind in Enum.GetValues<FileKind>())
        {
            if (Key(kind).StartsWith(value, StringComparison.OrdinalIgnoreCase) ||
                Short(kind).StartsWith(value, StringComparison.OrdinalIgnoreCase))
                kinds.Add(kind);
        }
        return kinds;
    }
}
