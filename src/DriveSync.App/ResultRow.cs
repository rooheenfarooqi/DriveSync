using DriveSync.Core;

namespace DriveSync.App;

/// <summary>One row in the results list, with the values already formatted for display.</summary>
public sealed class ResultRow
{
    public ResultRow(FileEntry file)
    {
        Name = file.Name;
        Folder = file.Folder.Path;
        FullPath = file.FullPath;
        SizeText = SummaryService.FormatSize(file.Size);
        ModifiedText = file.Modified.ToString("dd MMM yyyy HH:mm");
    }

    public string Name { get; }
    public string Folder { get; }
    public string FullPath { get; }
    public string SizeText { get; }
    public string ModifiedText { get; }
}
