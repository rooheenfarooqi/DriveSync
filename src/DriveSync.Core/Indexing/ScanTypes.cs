namespace DriveSync.Core.Indexing;

/// <summary>One file as read from disk or from the saved index.</summary>
public struct ScanFile
{
    public string Name;
    public long Size;
    public long Modified;   // ticks, local time
    public long Created;    // ticks, local time
    public long Indexed;    // ticks, when DriveSync last saw it
    public FileFlags Flags;
}

[Flags]
public enum FileFlags : byte
{
    None = 0,
    Hidden = 1,
    System = 2,
}

/// <summary>One folder and the files directly in it.</summary>
public sealed class ScanDir
{
    public ScanDir(string path, int rootId)
    {
        Path = path;
        RootId = rootId;
    }

    public string Path { get; }
    public int RootId { get; }
    public List<ScanFile> Files { get; } = new();
}

/// <summary>A folder to scan together with the indexing options of the indexed folder it belongs to.</summary>
public sealed class ScanRoot
{
    public required int RootId { get; init; }

    /// <summary>Where to start. Usually the indexed folder itself, or a folder inside it for a targeted rescan.</summary>
    public required string Path { get; init; }

    public bool IncludeSubfolders { get; init; } = true;

    /// <summary>File type groups to keep; null keeps every type.</summary>
    public IReadOnlySet<TypeGroup>? Types { get; init; }
}

/// <summary>Live numbers from a running scan.</summary>
public readonly record struct ScanProgress(int FoldersRead, long FilesFound, int FoldersWaiting);
