using System.Collections.Concurrent;
using System.Diagnostics;

namespace DriveSync.Core;

/// <summary>
/// Puts the pieces together and is the only class the user interface needs:
///
///   1. Load the saved index from SQLite, so search works immediately.
///   2. Start watching the drives for changes.
///   3. Scan all drives in the background and swap in the fresh index.
///   4. Rescan every so often (and save), as a safety net for anything the watcher missed.
/// </summary>
public sealed class IndexManager : IDisposable
{
    private readonly IndexStore _store;
    private readonly IndexWatcher _watcher = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    // Changes that arrive while a full scan is running. The scan may already have
    // passed that folder, so they are applied again once the new index is in place.
    private readonly ConcurrentQueue<FileChange> _changesDuringScan = new();
    private volatile bool _isScanning;
    private volatile bool _changedSinceSave;

    private IReadOnlyList<string> _roots = Array.Empty<string>();

    public IndexManager(string? databasePath = null)
    {
        _store = new IndexStore(databasePath ?? IndexStore.DefaultPath);
        _watcher.Changed += OnFileChanged;
        _watcher.MissedChanges += () => _ = RescanAsync();
    }

    /// <summary>The index to search.</summary>
    public FileIndex Index { get; } = new();

    /// <summary>How often a full rescan runs while the app is open.</summary>
    public TimeSpan RescanInterval { get; set; } = TimeSpan.FromMinutes(30);

    public bool IsScanning => _isScanning;

    /// <summary>A short message describing what the manager is doing (raised on a background thread).</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Raised after a full scan has replaced the index, so open searches can be refreshed.</summary>
    public event Action? IndexReplaced;

    /// <summary>Starts everything. Returns once the saved index is loaded; scanning continues in the background.</summary>
    public async Task StartAsync(IReadOnlyList<string> roots)
    {
        _roots = roots;

        StatusChanged?.Invoke("Loading saved index…");
        FileIndex? saved = await Task.Run(_store.Load);
        if (saved != null)
        {
            Index.ReplaceWith(saved);
            StatusChanged?.Invoke($"Loaded {Index.FileCount:N0} files from the saved index. Refreshing in the background…");
            IndexReplaced?.Invoke();
        }

        _watcher.Start(roots);

        _ = RescanAsync();
        _ = RescanPeriodicallyAsync();
    }

    /// <summary>Scans every root again and swaps in the new index. Does nothing if a scan is already running.</summary>
    public async Task RescanAsync()
    {
        if (!await _scanLock.WaitAsync(0))
            return;

        try
        {
            _isScanning = true;
            bool firstScan = Index.FolderCount == 0;
            StatusChanged?.Invoke(firstScan ? "Building the index for the first time…" : "Refreshing the index…");

            var stopwatch = Stopwatch.StartNew();
            List<FolderNode> folders = await Task.Run(() => FileScanner.Scan(
                _roots,
                count => StatusChanged?.Invoke($"Scanning… {count:N0} folders read"),
                _shutdown.Token));

            if (_shutdown.IsCancellationRequested)
                return;

            Index.ReplaceWith(FileIndex.FromFolders(folders));
            _isScanning = false;
            while (_changesDuringScan.TryDequeue(out FileChange? change))
                Apply(change);

            stopwatch.Stop();
            StatusChanged?.Invoke($"Indexed {Index.FileCount:N0} files in {Index.FolderCount:N0} folders " +
                                  $"({stopwatch.Elapsed.TotalSeconds:0.0} s). Watching for changes.");
            IndexReplaced?.Invoke();

            await Task.Run(() => _store.Save(Index));
            _changedSinceSave = false;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke("Indexing problem: " + ex.Message);
        }
        finally
        {
            _isScanning = false;
            _scanLock.Release();
        }
    }

    private async Task RescanPeriodicallyAsync()
    {
        using var timer = new PeriodicTimer(RescanInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token))
                await RescanAsync();
        }
        catch (OperationCanceledException)
        {
            // app is closing
        }
    }

    private void OnFileChanged(FileChange change)
    {
        try
        {
            Apply(change);
            if (_isScanning)
                _changesDuringScan.Enqueue(change);
            _changedSinceSave = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file was busy or vanished again; the next rescan will sort it out.
        }
    }

    /// <summary>Updates the index for one change on disk.</summary>
    private void Apply(FileChange change)
    {
        if (change.Kind == ChangeKind.Deleted)
        {
            Index.RemovePath(change.Path);
            return;
        }

        if (change.Kind == ChangeKind.Renamed && change.OldPath != null)
            Index.RemovePath(change.OldPath);

        if (Directory.Exists(change.Path))
        {
            // A new or renamed folder may arrive with files already inside
            // (for example when it was moved here), so read the whole folder.
            if (change.Kind != ChangeKind.Changed)
                Index.AddFolders(FileScanner.Scan(new[] { change.Path }));
        }
        else
        {
            var info = new FileInfo(change.Path);
            if (info.Exists)
                Index.AddOrUpdateFile(info.FullName, info.Length, info.LastWriteTime);
        }
    }

    /// <summary>Stops watching and scanning, and saves the index if it changed.</summary>
    public void Dispose()
    {
        _shutdown.Cancel();
        _watcher.Dispose();

        // Only save if no scan is running (a running scan saves by itself when it finishes).
        if (_changedSinceSave && _scanLock.Wait(0))
        {
            try { _store.Save(Index); }
            catch (Exception) { /* not critical: the next start will rescan anyway */ }
            finally { _scanLock.Release(); }
        }
    }
}
