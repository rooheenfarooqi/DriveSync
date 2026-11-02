namespace DriveSync.Core;

/// <summary>A change on disk reported by <see cref="IndexWatcher"/>.</summary>
public enum ChangeKind { Created, Changed, Deleted, Renamed }

/// <param name="OldPath">Only set for <see cref="ChangeKind.Renamed"/>.</param>
public sealed record FileChange(ChangeKind Kind, string Path, string? OldPath = null);

/// <summary>
/// Listens to Windows for files being created, changed, deleted or renamed,
/// so the index stays correct without scanning the drives again.
/// Uses one FileSystemWatcher per drive, watching all sub-folders.
/// </summary>
public sealed class IndexWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new();

    /// <summary>Raised (on a background thread) for every change.</summary>
    public event Action<FileChange>? Changed;

    /// <summary>
    /// Raised when Windows reports too many changes at once and some were lost.
    /// The index may then be out of date, so the caller should rescan.
    /// </summary>
    public event Action? MissedChanges;

    public void Start(IEnumerable<string> roots)
    {
        foreach (string root in roots)
        {
            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                   NotifyFilters.Size | NotifyFilters.LastWrite,
                    InternalBufferSize = 64 * 1024, // the maximum; fewer lost events when many files change
                };

                watcher.Created += (_, e) => Changed?.Invoke(new FileChange(ChangeKind.Created, e.FullPath));
                watcher.Changed += (_, e) => Changed?.Invoke(new FileChange(ChangeKind.Changed, e.FullPath));
                watcher.Deleted += (_, e) => Changed?.Invoke(new FileChange(ChangeKind.Deleted, e.FullPath));
                watcher.Renamed += (_, e) => Changed?.Invoke(new FileChange(ChangeKind.Renamed, e.FullPath, e.OldFullPath));
                watcher.Error += (_, _) => MissedChanges?.Invoke();

                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                // Some drives (for example certain USB sticks) can't be watched. Skip them.
            }
        }
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
            watcher.Dispose();
        _watchers.Clear();
    }
}
