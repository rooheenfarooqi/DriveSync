namespace DriveSync.Core;

/// <summary>
/// One file in the index.
/// The folder path is not stored here: it lives once in <see cref="FolderNode"/>
/// and is shared by every file in that folder. With a million files this saves
/// hundreds of megabytes compared to storing the full path for each file.
/// </summary>
public sealed class FileEntry
{
    public FileEntry(string name, FolderNode folder, long size, DateTime modified)
    {
        Name = name;
        Folder = folder;
        Size = size;
        Modified = modified;
    }

    /// <summary>File name with extension, for example "report.docx".</summary>
    public string Name { get; }

    /// <summary>The folder that contains this file.</summary>
    public FolderNode Folder { get; }

    /// <summary>Size in bytes.</summary>
    public long Size { get; }

    /// <summary>Last time the file was written to (local time).</summary>
    public DateTime Modified { get; }

    /// <summary>Full path, built only when it is needed.</summary>
    public string FullPath => Path.Join(Folder.Path, Name);
}
