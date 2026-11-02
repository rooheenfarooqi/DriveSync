using DriveSync.Core;

namespace DriveSync.App;

/// <summary>One row in the "Folder" drop-down, with the values already formatted for display.</summary>
public sealed class FolderOption
{
    /// <summary>The first row: no folder chosen, search every drive.</summary>
    public static readonly FolderOption All = new(null, "All drives", "Search everywhere on this PC", "");

    private FolderOption(string? path, string label, string detail, string countText)
    {
        Path = path;
        Label = label;
        Detail = detail;
        CountText = countText;
    }

    /// <summary>A folder found in the index.</summary>
    public FolderOption(FolderMatch match)
        : this(match.Path, NameOf(match.Path), match.Path,
               match.FileCount == 1 ? "1 file" : $"{match.FileCount:N0} files")
    {
    }

    /// <summary>A folder given by its path, for example from "Browse…" or a shortcut like Documents.</summary>
    public static FolderOption FromPath(string path, string? label = null) =>
        new(System.IO.Path.TrimEndingDirectorySeparator(path), label ?? NameOf(path), path, "");

    /// <summary>Full folder path; null for <see cref="All"/>.</summary>
    public string? Path { get; }

    /// <summary>The folder's own name, for example "Documents" (or "C:\" for a drive).</summary>
    public string Label { get; }

    /// <summary>The full path, or a description for <see cref="All"/>.</summary>
    public string Detail { get; }

    /// <summary>How many files are directly inside, if known.</summary>
    public string CountText { get; }

    private static string NameOf(string path)
    {
        string name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path));
        return name.Length > 0 ? name : path;
    }
}
