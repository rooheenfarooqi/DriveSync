namespace DriveSync.Core.Indexing;

public enum RootStatus { Watching, Paused, Scanning, Unreachable }

public enum ChangeKind { Added, Modified, Renamed, Removed }

/// <summary>The four pipeline stages shown on Home and in first-run setup.</summary>
public enum JobStage { Select, Index, Parse, Search }

public enum NoticeKind { Ok, Info, Warn, Err }

/// <summary>A message for a toast, optionally with one action button (Undo, Search it…).</summary>
public sealed record Notice(NoticeKind Kind, string Title, string Message = "", string? ActionLabel = null, Action? Action = null, int Milliseconds = 4600);

/// <summary>An indexed folder as the UI sees it.</summary>
public sealed class IndexedFolder
{
    public int Id { get; init; }
    public string Name { get; set; } = "";
    public string Path { get; init; } = "";
    public bool Paused { get; set; }
    public bool IncludeSubfolders { get; init; } = true;
    public IReadOnlySet<TypeGroup>? Types { get; init; }
    public DateTime Added { get; init; }
    public DateTime? LastScan { get; set; }
    public RootStatus Status { get; internal set; }
    public int FileCount { get; internal set; }

    /// <summary>"C:", "D:" — the drive the folder is on.</summary>
    public string Drive => System.IO.Path.GetPathRoot(Path)?.TrimEnd('\\') ?? "";
    public bool IsSystemDrive => Drive.Equals(System.IO.Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}

/// <summary>One change the watcher picked up.</summary>
public sealed class ActivityEvent
{
    public long Id { get; init; }
    public DateTime At { get; init; }
    public ChangeKind Kind { get; init; }
    public string Path { get; init; } = "";
    public string? OldPath { get; init; }
    public long Size { get; init; }
    public int RootId { get; init; }
    public string RootName { get; init; } = "";
    public bool IsFolder { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>The job DriveSync is busy with (scan, refresh, rebuild…).</summary>
public sealed class JobInfo
{
    public string Label { get; init; } = "";
    public int? RootId { get; init; }
    public JobStage Stage { get; internal set; }
    public double Progress { get; internal set; }
    public long FilesSeen { get; internal set; }
    public long FilesEstimate { get; internal set; }
    public int Percent => (int)Math.Round(Progress * 100);
}

/// <summary>A choice in first-run setup: a folder, or every drive.</summary>
public sealed record FolderChoice(string Path, string Name);

/// <summary>Checks a folder path typed into Add folder or first-run setup.</summary>
public static class FolderRules
{
    public sealed record Result(string? Path, string? Error, string? Warning, IReadOnlyList<IndexedFolder> Merges);

    public static Result Validate(string input, IReadOnlyList<IndexedFolder> existing, bool checkDisk = true)
    {
        string v = input.Trim().Trim('"').Replace('/', '\\');
        while (v.Length > 3 && v.EndsWith('\\')) v = v[..^1];
        var none = Array.Empty<IndexedFolder>();
        if (v.Length == 0)
            return new(null, "Enter the full path of a folder.", null, none);

        bool driveOnly = v.Length == 2 && char.IsLetter(v[0]) && v[1] == ':';
        if (driveOnly) v += "\\";
        if (!(v.Length >= 3 && char.IsLetter(v[0]) && v[1] == ':' && v[2] == '\\') ||
            v.IndexOfAny(new[] { '<', '>', '"', '|', '?', '*' }) >= 0 || v.IndexOf(':', 2) >= 0)
            return new(null, $"Use a full Windows path that starts with a drive letter, like {Example()}.", null, none);

        IndexedFolder? same = existing.FirstOrDefault(f => string.Equals(Norm(f.Path), Norm(v), StringComparison.OrdinalIgnoreCase));
        if (same != null)
            return new(null, $"This folder is already indexed as “{same.Name}”.", null, none);
        IndexedFolder? parent = existing.FirstOrDefault(f => ExclusionRules.IsSameOrInside(v, Norm(f.Path)) && !string.Equals(Norm(f.Path), Norm(v), StringComparison.OrdinalIgnoreCase));
        if (parent != null)
            return new(null, $"It's already covered by “{parent.Name}” ({parent.Path}).", null, none);

        if (checkDisk)
        {
            string root = v[..3];
            bool ready;
            try { ready = new DriveInfo(root).IsReady; }
            catch (ArgumentException) { ready = false; }
            if (!ready)
                return new(null, $"Drive {char.ToUpperInvariant(v[0])}: isn't connected. Plug it in, then add the folder.", null, none);
            if (!Directory.Exists(v))
                return new(null, "This folder doesn't exist. Check the path, or use Browse… to pick it.", null, none);
        }

        var kids = existing.Where(f => ExclusionRules.IsSameOrInside(Norm(f.Path), v)).ToList();
        string? warning = null;
        if (v.Length == 3 || v.StartsWith(Path.GetPathRoot(Environment.SystemDirectory) + "Windows", StringComparison.OrdinalIgnoreCase))
            warning = "Indexing a whole drive or the Windows folder adds many system files and takes longer.";
        else if (kids.Count > 0)
            warning = $"Includes {kids.Count} folder{(kids.Count > 1 ? "s" : "")} you already index ({string.Join(", ", kids.Select(k => k.Name))}). They'll be merged.";
        return new(v, null, warning, kids);
    }

    private static string Norm(string p) => p.Length > 3 ? p.TrimEnd('\\') : p;

    private static string Example() =>
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) is { Length: > 0 } desktop ? desktop : @"C:\Users\You\Desktop";

    /// <summary>A display name for a folder: its own name, or "Local Disk (C:)" for a drive.</summary>
    public static string DefaultName(string path)
    {
        string trimmed = Norm(path);
        if (trimmed.Length <= 3)
        {
            string letter = trimmed[..2];
            try
            {
                string label = new DriveInfo(trimmed).VolumeLabel;
                return $"{(string.IsNullOrWhiteSpace(label) ? "Local Disk" : label)} ({letter})";
            }
            catch (Exception) { return $"Local Disk ({letter})"; }
        }
        return System.IO.Path.GetFileName(trimmed);
    }
}
