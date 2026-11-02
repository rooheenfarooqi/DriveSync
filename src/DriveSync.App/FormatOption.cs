using DriveSync.Core;

namespace DriveSync.App;

/// <summary>One row in the "File type" drop-down, with the values already formatted for display.</summary>
public sealed class FormatOption
{
    /// <summary>The first row: no filter, search every type.</summary>
    public static readonly FormatOption All = new(null, "All file types", "Search every kind of file", "");

    private FormatOption(string? extension, string label, string description, string countText)
    {
        Extension = extension;
        Label = label;
        Description = description;
        CountText = countText;
    }

    public FormatOption(FileFormat format)
        : this(format.Extension, "." + format.Extension, format.Description,
               format.Count > 0 ? $"{format.Count:N0} files" : "none found")
    {
    }

    /// <summary>Extension without the dot, for example "pdf"; null for <see cref="All"/>.</summary>
    public string? Extension { get; }

    /// <summary>What the list and the box show, for example ".pdf".</summary>
    public string Label { get; }

    public string Description { get; }
    public string CountText { get; }
}
