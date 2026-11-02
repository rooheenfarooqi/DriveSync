namespace DriveSync.Core;

/// <summary>
/// One folder in the index and the files directly inside it
/// (files in sub-folders belong to their own FolderNode).
/// </summary>
public sealed class FolderNode
{
    public FolderNode(string path)
    {
        Path = path;
    }

    /// <summary>Full folder path, for example "C:\Users\Ayesha\Documents".</summary>
    public string Path { get; }

    /// <summary>Files directly inside this folder.</summary>
    public List<FileEntry> Files { get; } = new();
}
