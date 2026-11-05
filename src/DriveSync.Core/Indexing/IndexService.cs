using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DriveSync.Core.Storage;

namespace DriveSync.Core.Indexing;

/// <summary>What a finished scan, refresh or rebuild did.</summary>
public sealed record JobOutcome(int Files, int Folders, double Seconds, double BuildMs, int FilesBefore);

/// <summary>Lets a removal be undone.</summary>
public sealed class UndoToken
{
    internal IndexEngine? Engine;
    internal List<int> Files = new();
    internal List<int> Dirs = new();
    internal string? Pattern;
    internal RootRecord? Root;
    internal int Position;
    public int Count { get; internal set; }
    public string Name { get; internal set; } = "";
}

/// <summary>
/// Runs indexing for the whole app:
///
///   1. Loads the saved index from SQLite at start-up, so search works straight away.
///   2. Watches every indexed folder with a FileSystemWatcher and applies changes as they happen.
///   3. Runs jobs (add folder, rescan, refresh, rebuild) one at a time through the four stages
///      Select → Index → Parse → Search, with progress for the title bar, navigation and pipeline.
///   4. Refreshes on a schedule, notices drives being unplugged and plugged back in, writes
///      changes to SQLite in batches, and keeps the Live activity log.
///
/// Events are raised on background threads; the UI marshals them to its own thread.
/// </summary>
public sealed class IndexService : IDisposable
{
    private readonly IndexStore _store;
    private volatile IndexEngine _engine = IndexEngine.Empty();
    private readonly object _gate = new();
    private readonly object _applyLock = new();
    private readonly object _pendingLock = new();
    private readonly object _eventsLock = new();
    private readonly SemaphoreSlim _swapLock = new(1, 1);

    private readonly List<IndexedFolder> _folders = new();
    private List<string> _exclusions = new();
    private ExclusionRules _rules = ExclusionRules.None;
    private readonly List<string> _history = new();
    private readonly List<ActivityEvent> _events = new();
    private readonly List<long> _eventTimes = new();
    private readonly List<double> _lookups = new();
    private readonly List<IndexStore.Delta> _pendingDeltas = new();
    private readonly List<EventRecord> _pendingEvents = new();
    private readonly Queue<int> _pendingRescans = new();

    private JobInfo? _job;
    private CancellationTokenSource? _jobCts;
    private readonly Dictionary<int, FileSystemWatcher> _watchers = new();
    private readonly ConcurrentQueue<RawChange> _raw = new();
    private readonly ConcurrentQueue<RawChange> _duringScan = new();
    private readonly SemaphoreSlim _rawSignal = new(0);
    private volatile bool _scanning;
    private long _eventSeq;
    private long _lastIndexChanged;
    private long _lastStatus;
    private DateTime _fullSaveDue = DateTime.MaxValue;
    private Timer? _tick;
    private int _ticks;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _dataFolder;

    private enum RawKind { Created, Changed, Deleted, Renamed }
    private sealed record RawChange(RawKind Kind, string Path, string? OldPath, int RootId);

    public IndexService(string? databasePath = null)
    {
        _store = new IndexStore(databasePath ?? IndexStore.DefaultPath);
        _dataFolder = Path.GetDirectoryName(_store.FilePath) ?? "";
    }

    // ==================================================================
    // State for the UI
    // ==================================================================

    public AppSettings Settings { get; private set; } = new();
    public IndexEngine Engine => _engine;
    public string DatabasePath => _store.FilePath;
    public long DatabaseSize => _store.SizeOnDisk;

    public bool IsLoaded { get; private set; }
    public bool IsCleared { get; private set; }
    public bool HadOldIndex { get; private set; }
    public bool Watching { get; private set; } = true;
    public bool BatteryPaused { get; private set; }
    public DateTime LastRefresh { get; private set; } = DateTime.MinValue;
    public DateTime NextRefresh { get; private set; } = DateTime.MaxValue;
    public double LastBuildMs { get; private set; }
    public int AddedThisSession { get; private set; }
    public JobInfo? Job => _job;
    public int FileCount => _engine.FileCount;

    public IReadOnlyList<IndexedFolder> Folders { get { lock (_gate) return _folders.ToList(); } }
    public IReadOnlyList<string> Exclusions { get { lock (_gate) return _exclusions.ToList(); } }
    public IReadOnlyList<string> History { get { lock (_gate) return _history.ToList(); } }
    public IReadOnlyList<ActivityEvent> Events { get { lock (_eventsLock) return _events.ToList(); } }

    public IReadOnlyList<double> Lookups { get { lock (_lookups) return _lookups.ToList(); } }

    public double MedianLookup
    {
        get
        {
            lock (_lookups)
            {
                if (_lookups.Count == 0) return 0;
                var sorted = _lookups.OrderBy(x => x).ToList();
                int m = sorted.Count / 2;
                return sorted.Count % 2 == 1 ? sorted[m] : (sorted[m - 1] + sorted[m]) / 2;
            }
        }
    }

    public int ChangesToday
    {
        get
        {
            long since = DateTime.Now.AddDays(-1).Ticks;
            lock (_eventsLock) return _eventTimes.Count(t => t >= since);
        }
    }

    /// <summary>Changes per hour for the last 24 hours, oldest first; the last bucket is the current hour.</summary>
    public int[] HourlyChanges()
    {
        var buckets = new int[24];
        long now = DateTime.Now.Ticks;
        lock (_eventsLock)
        {
            foreach (long t in _eventTimes)
            {
                long h = (now - t) / TimeSpan.TicksPerHour;
                if (h >= 0 && h < 24) buckets[23 - h]++;
            }
        }
        return buckets;
    }

    public string StatusText
    {
        get
        {
            JobInfo? job = _job;
            if (job != null) return $"{job.Label} {job.Percent}%";
            if (!IsLoaded) return "Loading index…";
            if (IsCleared) return "Index is empty";
            if (!Watching || BatteryPaused) return $"Paused · {FileCount:N0} files";
            return $"Watching · {FileCount:N0} files";
        }
    }

    /// <summary>"busy", "warn", "err" or "live" — the colour of the status dot.</summary>
    public string StatusDot => _job != null || !IsLoaded ? "busy" : IsCleared || !Watching || BatteryPaused ? "warn" : "live";

    public event Action? StatusChanged;
    public event Action? IndexChanged;
    public event Action? FoldersChanged;
    public event Action<ActivityEvent>? ActivityAdded;
    public event Action<Notice>? Notify;

    // ==================================================================
    // Start-up
    // ==================================================================

    /// <summary>Loads everything, then starts watching. Same as <see cref="LoadConfigAsync"/> followed by <see cref="LoadIndexAsync"/>.</summary>
    public async Task StartAsync()
    {
        await LoadConfigAsync();
        await LoadIndexAsync();
    }

    private bool _configLoaded;

    /// <summary>Settings, folders, exclusions, history and recent activity. Quick; the window needs these before it opens.</summary>
    public async Task LoadConfigAsync()
    {
        if (_configLoaded) return;
        _configLoaded = true;
        HadOldIndex = await Task.Run(_store.EnsureSchema);
        Settings = _store.LoadSettings<AppSettings>("app") ?? new AppSettings();
        Settings.MaxParseMb = Math.Clamp(Settings.MaxParseMb, 1, 500);

        List<string>? exclusions = _store.LoadExclusions();
        if (exclusions == null)
        {
            exclusions = ExclusionRules.Defaults.ToList();
            _store.SaveExclusions(exclusions);
        }
        lock (_gate)
        {
            _exclusions = exclusions;
            _rules = new ExclusionRules(exclusions);
            _history.AddRange(_store.LoadHistory());
            foreach (RootRecord r in _store.LoadRoots())
                _folders.Add(ToFolder(r));
        }
        IsCleared = _store.GetMeta("cleared") == "1";
        if (long.TryParse(_store.GetMeta("last_refresh"), out long lr)) LastRefresh = new DateTime(lr);
        if (double.TryParse(_store.GetMeta("last_build_ms"), System.Globalization.CultureInfo.InvariantCulture, out double bm)) LastBuildMs = bm;
        LoadEvents();
        RaiseStatus(force: true);
    }

    /// <summary>Loads the saved index into memory and starts the watchers and timers.</summary>
    public async Task LoadIndexAsync()
    {
        await LoadConfigAsync();
        List<ScanDir>? dirs = await Task.Run(_store.LoadIndex);
        if (dirs != null)
        {
            IndexEngine engine = await Task.Run(() => IndexEngine.Build(dirs, RootNames()));
            lock (_applyLock) _engine = engine;
            if (LastBuildMs == 0) LastBuildMs = engine.BuildMs;
        }
        IsLoaded = true;
        CheckDrives(quiet: true);
        UpdateCounts();
        NextRefresh = DateTime.Now.AddMinutes(Settings.RefreshMinutes);
        CheckBattery();
        RefreshWatchers();
        _ = Task.Run(ProcessChangesLoop);
        _tick = new Timer(_ => Tick(), null, 1000, 1000);

        IndexChanged?.Invoke();
        FoldersChanged?.Invoke();
        RaiseStatus(force: true);

        // Folders chosen but never indexed (for example setup was closed during the first scan).
        if (dirs == null && !IsCleared && Settings.Onboarded && Folders.Count > 0)
            _ = TryRefresh("Building index");
    }

    private void LoadEvents()
    {
        var names = Folders.ToDictionary(f => f.Id, f => f.Name);
        var records = _store.LoadEvents(DateTime.Now.AddDays(-7), 300);
        var times = _store.LoadEventTimes(DateTime.Now.AddDays(-1));
        lock (_eventsLock)
        {
            foreach (EventRecord r in records)
            {
                _events.Add(new ActivityEvent
                {
                    Id = ++_eventSeq, At = new DateTime(r.At), Kind = Enum.TryParse(r.Kind, out ChangeKind k) ? k : ChangeKind.Modified,
                    Path = r.Path, OldPath = r.OldPath, Size = r.Size, RootId = r.RootId,
                    RootName = names.GetValueOrDefault(r.RootId, ""), IsFolder = r.Size < 0,
                });
            }
            _eventTimes.AddRange(times);
        }
    }

    private static IndexedFolder ToFolder(RootRecord r) => new()
    {
        Id = r.Id, Name = r.Name, Path = r.Path, Paused = r.Paused, IncludeSubfolders = r.IncludeSubfolders,
        Types = string.IsNullOrEmpty(r.Types) ? null : r.Types.Split(',').Select(Enum.Parse<TypeGroup>).ToHashSet(),
        Added = new DateTime(r.Added), LastScan = r.LastScan > 0 ? new DateTime(r.LastScan) : null,
    };

    private static RootRecord ToRecord(IndexedFolder f) => new()
    {
        Id = f.Id, Name = f.Name, Path = f.Path, Paused = f.Paused, IncludeSubfolders = f.IncludeSubfolders,
        Types = f.Types == null ? "" : string.Join(',', f.Types), Added = f.Added.Ticks, LastScan = f.LastScan?.Ticks ?? 0,
    };

    private void SaveRoots()
    {
        List<RootRecord> records;
        lock (_gate) records = _folders.Select(ToRecord).ToList();
        _store.SaveRoots(records);
    }

    private Dictionary<int, string> RootNames()
    {
        lock (_gate) return _folders.ToDictionary(f => f.Id, f => f.Name);
    }

    private int NextFolderId()
    {
        lock (_gate) return _folders.Count == 0 ? 1 : _folders.Max(f => f.Id) + 1;
    }

    // ==================================================================
    // Searching
    // ==================================================================

    public SearchResult Search(SearchRequest request, bool recordLookup = true)
    {
        SearchResult result = _engine.Search(request);
        if (recordLookup && result.Ran && !string.IsNullOrWhiteSpace(request.Query))
        {
            lock (_lookups)
            {
                _lookups.Add(result.ElapsedMs);
                if (_lookups.Count > 60) _lookups.RemoveAt(0);
            }
        }
        return result;
    }

    public IndexedFolder? FolderOf(int rootId)
    {
        lock (_gate) return _folders.FirstOrDefault(f => f.Id == rootId);
    }

    /// <summary>The last benchmark run (saved so the Benchmarks page doesn't re-run on every visit).</summary>
    public Benchmarking.BenchmarkResult? LoadBenchmark() => _store.LoadSettings<Benchmarking.BenchmarkResult>("benchmark");

    public void SaveBenchmark(Benchmarking.BenchmarkResult result) => _store.SaveSettings("benchmark", result);

    public void AddHistory(string query)
    {
        query = query.Trim();
        if (!Settings.RememberHistory || query.Length == 0) return;
        List<string> copy;
        lock (_gate)
        {
            _history.RemoveAll(h => h.Equals(query, StringComparison.OrdinalIgnoreCase));
            _history.Insert(0, query);
            if (_history.Count > 12) _history.RemoveRange(12, _history.Count - 12);
            copy = _history.ToList();
        }
        Task.Run(() => _store.SaveHistory(copy));
    }

    public void ClearHistory()
    {
        lock (_gate) _history.Clear();
        Task.Run(() => _store.SaveHistory(Array.Empty<string>()));
    }

    // ==================================================================
    // Settings
    // ==================================================================

    public void UpdateSettings(Action<AppSettings> change)
    {
        AppSettings before = Settings.Clone();
        change(Settings);
        Settings.MaxParseMb = Math.Clamp(Settings.MaxParseMb, 1, 500);
        _store.SaveSettings("app", Settings);
        if (before.RefreshMinutes != Settings.RefreshMinutes)
            NextRefresh = DateTime.Now.AddMinutes(Settings.RefreshMinutes);
        if (before.DetectChangesInstantly != Settings.DetectChangesInstantly || before.PauseOnBatterySaver != Settings.PauseOnBatterySaver)
        {
            CheckBattery();
            RefreshWatchers();
        }
        RaiseStatus(force: true);
    }

    public void ReplaceSettings(AppSettings settings) => UpdateSettings(s =>
    {
        foreach (var p in typeof(AppSettings).GetProperties())
            if (p.CanWrite) p.SetValue(s, p.GetValue(settings));
    });

    /// <summary>
    /// "Include hidden and system files" changed. Turning it off removes them at once and returns the
    /// number removed (negative); turning it on rescans and the task returns the number added.
    /// </summary>
    public Task<int>? ApplyHiddenSetting(bool include)
    {
        if (!include)
        {
            int removed = _engine.RemoveHidden();
            ScheduleFullSave();
            UpdateCounts();
            IndexChanged?.Invoke();
            return Task.FromResult(-removed);
        }
        int before = FileCount;
        Task<JobOutcome>? job = TryRefresh("Adding hidden files", includePaused: true);
        return job?.ContinueWith(t => t.IsCompletedSuccessfully ? FileCount - before : 0);
    }

    // ==================================================================
    // Jobs
    // ==================================================================

    private Task<JobOutcome>? TryRunJob(string label, int? rootId, Func<JobInfo, CancellationToken, Task<JobOutcome>> body, bool quietWhenBusy = false)
    {
        JobInfo job;
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_job != null)
            {
                if (!quietWhenBusy)
                    Notify?.Invoke(new Notice(NoticeKind.Warn, "DriveSync is busy", $"Wait for “{_job.Label}” to finish, then try again."));
                return null;
            }
            _job = job = new JobInfo { Label = label, RootId = rootId };
            _jobCts = cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        }
        UpdateStatuses();
        RaiseStatus(force: true);
        FoldersChanged?.Invoke();

        return Task.Run(async () =>
        {
            try
            {
                await _swapLock.WaitAsync(cts.Token);
                try { return await body(job, cts.Token); }
                finally { _swapLock.Release(); }
            }
            catch (OperationCanceledException)
            {
                return new JobOutcome(FileCount, 0, 0, 0, FileCount);
            }
            catch (Exception ex)
            {
                Notify?.Invoke(new Notice(NoticeKind.Err, "Indexing problem", ex.Message));
                throw;
            }
            finally
            {
                // If the job stopped before swapping engines, its queued changes are already in the current one.
                lock (_applyLock) { _duringScan.Clear(); _scanning = false; }
                lock (_gate) { _job = null; _jobCts = null; }
                UpdateStatuses();
                UpdateCounts();
                RaiseStatus(force: true);
                FoldersChanged?.Invoke();
                IndexChanged?.Invoke();
            }
        });
    }

    private void Report(JobInfo job, JobStage stage, double stageFraction)
    {
        (double from, double to) = stage switch
        {
            JobStage.Select => (0.0, 0.6),
            JobStage.Index => (0.6, 0.8),
            JobStage.Parse => (0.8, 0.95),
            _ => (0.95, 1.0),
        };
        job.Stage = stage;
        job.Progress = Math.Max(job.Progress, from + (to - from) * Math.Clamp(stageFraction, 0, 1));
        RaiseStatus();
    }

    /// <summary>
    /// Scans <paramref name="targets"/> (or only <paramref name="subtree"/> inside them), merges the result with
    /// everything else in the index, swaps the new engine in and saves it.
    /// </summary>
    private async Task<JobOutcome> ScanAsync(JobInfo job, CancellationToken ct, IReadOnlyList<IndexedFolder> targets,
                                            string? subtree = null, IReadOnlyCollection<int>? dropRoots = null)
    {
        var clock = Stopwatch.StartNew();
        int before = FileCount;
        Report(job, JobStage.Select, 0);

        var reachable = targets.Where(f => Directory.Exists(subtree ?? f.Path)).ToList();
        var roots = reachable.Select(f => new ScanRoot
        {
            RootId = f.Id,
            Path = subtree ?? f.Path,
            IncludeSubfolders = f.IncludeSubfolders,
            Types = f.Types,
        }).ToList();

        int estimate = subtree == null ? reachable.Sum(f => _engine.FolderCountForRoot(f.Id)) : 0;
        lock (_applyLock) _scanning = true;
        ExclusionRules rules;
        lock (_gate) rules = _rules;
        bool includeHidden = Settings.IncludeHidden;

        List<ScanDir> scanned = await Task.Run(() => FileScanner.Scan(roots, rules, includeHidden, p =>
        {
            double fraction = estimate > 0
                ? Math.Min(0.99, p.FoldersRead / (double)estimate)
                : p.FoldersRead / (double)(p.FoldersRead + p.FoldersWaiting + 50);
            job.FilesSeen = p.FilesFound;
            Report(job, JobStage.Select, fraction);
        }, ct, IsOwnData), ct);
        ct.ThrowIfCancellationRequested();
        job.FilesEstimate = job.FilesSeen;

        // Index: merge with what stays and build the token maps.
        Report(job, JobStage.Index, 0);
        var targetIds = reachable.Select(f => f.Id).ToHashSet();
        var drop = dropRoots?.ToHashSet() ?? new HashSet<int>();
        var newPaths = new List<string>();
        List<ScanDir> all = _engine.Export((root, path) =>
            !drop.Contains(root) &&
            !(targetIds.Contains(root) && (subtree == null || ExclusionRules.IsSameOrInside(path, subtree))), newPaths);
        all.AddRange(scanned);
        IndexEngine built = await Task.Run(() => IndexEngine.Build(all, RootNames(), p => Report(job, JobStage.Index, p)), ct);
        built.MarkNew(newPaths);
        lock (_applyLock)
        {
            _engine = built;
            ReplayDuringScan(built);
            _scanning = false;
        }
        LastBuildMs = built.BuildMs;
        IsCleared = false;
        UpdateCounts();
        IndexChanged?.Invoke();

        // Parse: save paths and metadata to the local database.
        Report(job, JobStage.Parse, 0);
        List<ScanDir> snapshot = built.Export();
        await Task.Run(() => _store.SaveIndex(snapshot, p => Report(job, JobStage.Parse, p)), CancellationToken.None);
        _fullSaveDue = DateTime.MaxValue;

        // Search: warm up the lookup engine.
        Report(job, JobStage.Search, 0);
        foreach (string q in new[] { "a", "report", "ext:pdf" })
            built.Search(new SearchRequest { Query = q, Take = 20 });
        Report(job, JobStage.Search, 1);

        DateTime now = DateTime.Now;
        lock (_gate)
            foreach (IndexedFolder f in _folders)
                if (targetIds.Contains(f.Id) && subtree == null) f.LastScan = now;
        SaveRoots();
        if (subtree == null)
        {
            LastRefresh = now;
            NextRefresh = now.AddMinutes(Settings.RefreshMinutes);
            _store.SetMeta("last_refresh", now.Ticks.ToString());
        }
        _store.SetMeta("last_build_ms", LastBuildMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
        RefreshWatchers();
        return new JobOutcome(built.FileCount, reachable.Count, clock.Elapsed.TotalSeconds, built.BuildMs, before);
    }

    /// <summary>Re-applies watcher changes that arrived while a new engine was being built.</summary>
    private void ReplayDuringScan(IndexEngine engine)
    {
        while (_duringScan.TryDequeue(out RawChange? change))
            Apply(change, engine, replay: true);
    }

    public Task<JobOutcome>? TryRefresh(string label = "Refreshing index", bool includePaused = false, bool quietWhenBusy = false)
    {
        var targets = Folders.Where(f => f.Status != RootStatus.Unreachable && (includePaused || !f.Paused)).ToList();
        if (targets.Count == 0)
        {
            if (!quietWhenBusy)
                Notify?.Invoke(new Notice(NoticeKind.Info, "Nothing to refresh", "Add a folder or resume a paused one first."));
            return null;
        }
        return TryRunJob(label, null, (job, ct) => ScanAsync(job, ct, targets), quietWhenBusy);
    }

    public Task<JobOutcome>? TryRebuild() => TryRefresh("Rebuilding index", includePaused: true);

    public Task<JobOutcome>? TryRescanFolder(int id, bool quietWhenBusy = false)
    {
        IndexedFolder? folder = FolderOf(id);
        if (folder == null) return null;
        return TryRunJob($"Scanning {folder.Name}", id, (job, ct) => ScanAsync(job, ct, new[] { folder }), quietWhenBusy);
    }

    /// <summary>Adds an indexed folder and scans it. Folders inside it (<paramref name="merges"/>) are merged into it.</summary>
    public Task<JobOutcome>? TryAddFolder(string path, string? name, bool includeSubfolders, IReadOnlySet<TypeGroup>? types, IReadOnlyList<IndexedFolder>? merges = null)
    {
        lock (_gate)
        {
            if (_job != null)
            {
                Notify?.Invoke(new Notice(NoticeKind.Warn, "DriveSync is busy", $"Wait for “{_job.Label}” to finish."));
                return null;
            }
        }
        var folder = new IndexedFolder
        {
            Id = NextFolderId(),
            Name = string.IsNullOrWhiteSpace(name) ? FolderRules.DefaultName(path) : name.Trim(),
            Path = path,
            IncludeSubfolders = includeSubfolders,
            Types = types == null || types.Count == FileKinds.AllGroups.Length - 1 ? null : types,
            Added = DateTime.Now,
        };
        var mergeIds = merges?.Select(m => m.Id).ToList() ?? new List<int>();
        lock (_gate)
        {
            _folders.RemoveAll(f => mergeIds.Contains(f.Id));
            _folders.Add(folder);
        }
        foreach (int id in mergeIds) StopWatcher(id);
        SaveRoots();
        FoldersChanged?.Invoke();
        return TryRunJob($"Scanning {folder.Name}", folder.Id, (job, ct) => ScanAsync(job, ct, new[] { folder }, dropRoots: mergeIds));
    }

    /// <summary>First-run setup: index exactly these folders.</summary>
    public Task<JobOutcome>? TryRunSetup(IReadOnlyList<FolderChoice> picks)
    {
        var keep = new List<IndexedFolder>();
        var drop = new List<int>();
        lock (_gate)
        {
            if (_job != null) return null;
            foreach (IndexedFolder f in _folders)
            {
                if (picks.Any(p => p.Path.Equals(f.Path, StringComparison.OrdinalIgnoreCase))) keep.Add(f);
                else drop.Add(f.Id);
            }
            int next = _folders.Count == 0 ? 1 : _folders.Max(f => f.Id) + 1;
            foreach (FolderChoice p in picks)
            {
                if (keep.Any(f => f.Path.Equals(p.Path, StringComparison.OrdinalIgnoreCase))) continue;
                // A pick inside another pick is covered by it.
                if (picks.Any(o => o != p && ExclusionRules.IsSameOrInside(p.Path, o.Path) && !o.Path.Equals(p.Path, StringComparison.OrdinalIgnoreCase))) continue;
                keep.Add(new IndexedFolder { Id = next++, Name = p.Name, Path = p.Path, Added = DateTime.Now });
            }
            // Folders now covered by a bigger pick are merged into it.
            var covered = keep.Where(f => keep.Any(o => o != f && ExclusionRules.IsSameOrInside(f.Path, o.Path) &&
                                                        !o.Path.Equals(f.Path, StringComparison.OrdinalIgnoreCase))).ToList();
            foreach (IndexedFolder f in covered)
            {
                keep.Remove(f);
                drop.Add(f.Id);
            }
            _folders.Clear();
            _folders.AddRange(keep);
        }
        foreach (int id in drop) StopWatcher(id);
        SaveRoots();
        FoldersChanged?.Invoke();
        var targets = keep.Where(f => Directory.Exists(f.Path)).ToList();
        return TryRunJob("Building index", null, (job, ct) => ScanAsync(job, ct, targets, dropRoots: drop));
    }

    // ==================================================================
    // Folder actions
    // ==================================================================

    public void SetFolderPaused(int id, bool paused)
    {
        IndexedFolder? folder = FolderOf(id);
        if (folder == null) return;
        folder.Paused = paused;
        SaveRoots();
        UpdateStatuses();
        RefreshWatchers();
        FoldersChanged?.Invoke();
    }

    /// <summary>Stops indexing a folder. The returned token undoes it.</summary>
    public UndoToken RemoveFolder(int id)
    {
        var token = new UndoToken();
        IndexedFolder? folder = FolderOf(id);
        if (folder == null) return token;
        lock (_gate)
        {
            token.Position = _folders.IndexOf(folder);
            _folders.Remove(folder);
        }
        StopWatcher(id);
        token.Root = ToRecord(folder);
        token.Name = folder.Name;
        token.Engine = _engine;
        token.Count = _engine.RemoveFolder(folder.Path, token.Files, token.Dirs);
        QueueDelta(new IndexStore.DeleteFolder(folder.Path));
        SaveRoots();
        UpdateCounts();
        FoldersChanged?.Invoke();
        IndexChanged?.Invoke();
        RaiseStatus(force: true);
        return token;
    }

    public void UndoRemoveFolder(UndoToken token)
    {
        if (token.Root == null) return;
        IndexedFolder folder = ToFolder(token.Root);
        lock (_gate) _folders.Insert(Math.Min(token.Position, _folders.Count), folder);
        SaveRoots();
        if (token.Engine == _engine)
        {
            _engine.Revive(token.Files, token.Dirs);
            ScheduleFullSave();
        }
        else
        {
            TryRescanFolder(folder.Id, quietWhenBusy: true);
        }
        UpdateStatuses();
        RefreshWatchers();
        UpdateCounts();
        FoldersChanged?.Invoke();
        IndexChanged?.Invoke();
        RaiseStatus(force: true);
    }

    public void SetWatching(bool on)
    {
        Watching = on;
        RefreshWatchers();
        RaiseStatus(force: true);
        FoldersChanged?.Invoke();
    }

    /// <summary>Clears the index and the summary cache. Files on disk are not touched.</summary>
    public async Task ClearIndexAsync()
    {
        _jobCts?.Cancel();
        await _swapLock.WaitAsync();
        try
        {
            lock (_applyLock) _engine = IndexEngine.Empty();
            lock (_pendingLock) _pendingDeltas.Clear();
            IsCleared = true;
            await Task.Run(_store.ClearIndex);
        }
        finally { _swapLock.Release(); }
        RefreshWatchers();
        UpdateCounts();
        IndexChanged?.Invoke();
        FoldersChanged?.Invoke();
        RaiseStatus(force: true);
    }

    /// <summary>Checks whether disconnected drives are back. Returns the folders still missing.</summary>
    public List<IndexedFolder> CheckDrivesNow()
    {
        CheckDrives(quiet: false);
        return Folders.Where(f => f.Status == RootStatus.Unreachable).ToList();
    }

    private void CheckDrives(bool quiet)
    {
        bool changed = false;
        foreach (IndexedFolder f in Folders)
        {
            bool present;
            try { present = Directory.Exists(f.Path); }
            catch (Exception) { present = false; }

            if (!present && f.Status != RootStatus.Unreachable)
            {
                f.Status = RootStatus.Unreachable;
                StopWatcher(f.Id);
                changed = true;
                if (!quiet)
                    Notify?.Invoke(new Notice(NoticeKind.Warn, $"Drive {f.Drive} isn't connected", $"Files from {f.Name} stay searchable from the last scan but can't be opened."));
            }
            else if (present && f.Status == RootStatus.Unreachable)
            {
                f.Status = f.Paused ? RootStatus.Paused : RootStatus.Watching;
                changed = true;
                if (!quiet)
                    Notify?.Invoke(new Notice(NoticeKind.Ok, $"Drive {f.Drive} is back", $"DriveSync is catching up on changes in {f.Name}."));
                lock (_pendingRescans) _pendingRescans.Enqueue(f.Id);
            }
        }
        if (changed)
        {
            UpdateStatuses();
            RefreshWatchers();
            FoldersChanged?.Invoke();
        }
    }

    private void UpdateStatuses()
    {
        JobInfo? job = _job;
        foreach (IndexedFolder f in Folders)
        {
            bool present;
            try { present = f.Status != RootStatus.Unreachable || Directory.Exists(f.Path); }
            catch (Exception) { present = false; }
            f.Status = !present ? RootStatus.Unreachable
                : job != null && (job.RootId == f.Id || job.RootId == null && !f.Paused) ? RootStatus.Scanning
                : f.Paused ? RootStatus.Paused
                : RootStatus.Watching;
        }
    }

    private void UpdateCounts()
    {
        IndexEngine engine = _engine;
        foreach (IndexedFolder f in Folders)
            f.FileCount = engine.CountForRoot(f.Id);
    }

    // ==================================================================
    // Exclusions
    // ==================================================================

    /// <summary>Adds a rule and removes what it covers from the index at once. The token undoes it.</summary>
    public UndoToken AddExclusion(string pattern)
    {
        lock (_gate)
        {
            _exclusions.Add(pattern);
            _rules = new ExclusionRules(_exclusions);
        }
        _store.SaveExclusions(Exclusions);
        var token = new UndoToken { Pattern = pattern, Engine = _engine, Name = pattern };
        token.Count = _engine.ApplyExclusion(pattern, token.Files, token.Dirs);
        if (ExclusionRules.Classify(pattern) == ExclusionRules.RuleKind.Path) QueueDelta(new IndexStore.DeleteFolder(pattern));
        else ScheduleFullSave();
        UpdateCounts();
        IndexChanged?.Invoke();
        FoldersChanged?.Invoke();
        RaiseStatus(force: true);
        return token;
    }

    public void UndoExclusion(UndoToken token)
    {
        if (token.Pattern == null) return;
        lock (_gate)
        {
            _exclusions.RemoveAll(x => x.Equals(token.Pattern, StringComparison.OrdinalIgnoreCase));
            _rules = new ExclusionRules(_exclusions);
        }
        _store.SaveExclusions(Exclusions);
        if (token.Engine == _engine)
        {
            _engine.Revive(token.Files, token.Dirs);
            ScheduleFullSave();
            UpdateCounts();
            IndexChanged?.Invoke();
            FoldersChanged?.Invoke();
            RaiseStatus(force: true);
        }
        else
        {
            RescanForRemovedRule(token.Pattern);
        }
    }

    /// <summary>Removes a rule. Files it was hiding come back after a rescan; the task returns how many.</summary>
    public Task<int>? RemoveExclusion(string pattern)
    {
        lock (_gate)
        {
            _exclusions.RemoveAll(x => x.Equals(pattern, StringComparison.OrdinalIgnoreCase));
            _rules = new ExclusionRules(_exclusions);
        }
        _store.SaveExclusions(Exclusions);
        FoldersChanged?.Invoke();
        return RescanForRemovedRule(pattern);
    }

    private Task<int>? RescanForRemovedRule(string pattern)
    {
        int before = FileCount;
        Task<JobOutcome>? job;
        if (ExclusionRules.Classify(pattern) == ExclusionRules.RuleKind.Path)
        {
            IndexedFolder? owner = Folders.FirstOrDefault(f => ExclusionRules.IsSameOrInside(pattern, f.Path));
            if (owner == null) return Task.FromResult(0);
            job = TryRunJob($"Updating {owner.Name}", owner.Id, (j, ct) => ScanAsync(j, ct, new[] { owner }, subtree: pattern));
        }
        else
        {
            job = TryRefresh("Updating index", includePaused: true);
        }
        return job?.ContinueWith(t => t.IsCompletedSuccessfully ? Math.Max(0, FileCount - before) : 0);
    }

    // ==================================================================
    // Watching
    // ==================================================================

    private bool IsOwnData(string path) =>
        _dataFolder.Length > 0 && ExclusionRules.IsSameOrInside(path, _dataFolder);

    private void RefreshWatchers()
    {
        bool allowed = Watching && Settings.DetectChangesInstantly && !BatteryPaused && !IsCleared && IsLoaded;
        foreach (IndexedFolder f in Folders)
        {
            bool want = allowed && !f.Paused && f.Status != RootStatus.Unreachable;
            bool has;
            lock (_watchers) has = _watchers.ContainsKey(f.Id);
            if (want && !has) StartWatcher(f);
            else if (!want && has) StopWatcher(f.Id);
        }
        lock (_watchers)
        {
            var known = Folders.Select(f => f.Id).ToHashSet();
            foreach (int id in _watchers.Keys.Where(id => !known.Contains(id)).ToList())
            {
                _watchers[id].Dispose();
                _watchers.Remove(id);
            }
        }
    }

    private void StartWatcher(IndexedFolder f)
    {
        try
        {
            var w = new FileSystemWatcher(f.Path)
            {
                IncludeSubdirectories = f.IncludeSubfolders,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            int id = f.Id;
            w.Created += (_, e) => Enqueue(new RawChange(RawKind.Created, e.FullPath, null, id));
            w.Changed += (_, e) => Enqueue(new RawChange(RawKind.Changed, e.FullPath, null, id));
            w.Deleted += (_, e) => Enqueue(new RawChange(RawKind.Deleted, e.FullPath, null, id));
            w.Renamed += (_, e) => Enqueue(new RawChange(RawKind.Renamed, e.FullPath, e.OldFullPath, id));
            w.Error += (_, e) => OnWatcherError(id, e.GetException());
            w.EnableRaisingEvents = true;
            lock (_watchers) _watchers[f.Id] = w;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Some drives can't be watched; the scheduled refresh still keeps them current.
        }
    }

    private void StopWatcher(int id)
    {
        lock (_watchers)
        {
            if (_watchers.Remove(id, out FileSystemWatcher? w))
                w.Dispose();
        }
    }

    private void Enqueue(RawChange change)
    {
        if (IsOwnData(change.Path)) return;
        _raw.Enqueue(change);
        _rawSignal.Release();
    }

    /// <summary>
    /// Windows dropped notifications (too many changes at once). Rescan only the folder whose
    /// watcher overflowed, so nothing is missed.
    /// </summary>
    private void OnWatcherError(int rootId, Exception error)
    {
        IndexedFolder? folder = FolderOf(rootId);
        if (folder == null) return;
        if (error is InternalBufferOverflowException)
        {
            lock (_pendingRescans) if (!_pendingRescans.Contains(rootId)) _pendingRescans.Enqueue(rootId);
            Notify?.Invoke(new Notice(NoticeKind.Info, $"Catching up on {folder.Name}", "Lots of files changed at once, so DriveSync is rescanning that folder."));
        }
        else
        {
            StopWatcher(rootId);
            CheckDrives(quiet: false);
        }
    }

    private async Task ProcessChangesLoop()
    {
        var batch = new List<RawChange>();
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await _rawSignal.WaitAsync(_shutdown.Token);
                await Task.Delay(250, _shutdown.Token); // let bursts (save = several events) arrive together
            }
            catch (OperationCanceledException) { return; }

            batch.Clear();
            while (_raw.TryDequeue(out RawChange? c)) batch.Add(c);
            while (_rawSignal.CurrentCount > 0) _rawSignal.Wait(0);
            if (batch.Count == 0) continue;

            // Keep only the last "changed" per path, and drop "changed" right after "created".
            var lastChanged = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < batch.Count; i++)
            {
                if (batch[i].Kind == RawKind.Changed) lastChanged[batch[i].Path] = i;
                if (batch[i].Kind == RawKind.Created) created.Add(batch[i].Path);
            }
            for (int i = 0; i < batch.Count; i++)
            {
                RawChange c = batch[i];
                if (c.Kind == RawKind.Changed && (lastChanged[c.Path] != i || created.Contains(c.Path))) continue;
                try
                {
                    lock (_applyLock)
                    {
                        Apply(c, _engine, replay: false);
                        if (_scanning) _duringScan.Enqueue(c);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The file was busy or vanished again; the next refresh sorts it out.
                }
            }
            UpdateCounts();
            RaiseIndexChanged();
            RaiseStatus();
        }
    }

    private void Apply(RawChange c, IndexEngine engine, bool replay)
    {
        IndexedFolder? folder = FolderOf(c.RootId);
        if (folder == null || IsCleared) return;
        if (folder.Paused && !replay) return;

        switch (c.Kind)
        {
            case RawKind.Created:
            case RawKind.Changed:
                if (Directory.Exists(c.Path))
                {
                    if (c.Kind == RawKind.Created) AddTree(folder, c.Path, engine, replay, null);
                }
                else
                {
                    ApplyFile(folder, c.Path, engine, replay, created: c.Kind == RawKind.Created, renamedFrom: null);
                }
                break;

            case RawKind.Deleted:
            {
                FileView? removed = engine.RemoveFile(c.Path);
                if (removed != null)
                {
                    if (!replay)
                    {
                        QueueDelta(new IndexStore.DeleteFile(removed.Value.Folder, removed.Value.Name));
                        Log(ChangeKind.Removed, c.Path, null, removed.Value.Size, folder);
                    }
                }
                else if (engine.ContainsFolder(c.Path))
                {
                    engine.RemoveFolder(c.Path);
                    if (!replay)
                    {
                        QueueDelta(new IndexStore.DeleteFolder(c.Path));
                        Log(ChangeKind.Removed, c.Path, null, -1, folder, isFolder: true);
                    }
                }
                break;
            }

            case RawKind.Renamed:
            {
                string old = c.OldPath ?? "";
                if (Directory.Exists(c.Path))
                {
                    bool had = engine.ContainsFolder(old);
                    if (had)
                    {
                        engine.RemoveFolder(old);
                        if (!replay) QueueDelta(new IndexStore.DeleteFolder(old));
                    }
                    AddTree(folder, c.Path, engine, replay, had ? old : null);
                }
                else
                {
                    FileView? removed = engine.RemoveFile(old);
                    if (removed != null && !replay)
                        QueueDelta(new IndexStore.DeleteFile(removed.Value.Folder, removed.Value.Name));
                    ApplyFile(folder, c.Path, engine, replay, created: removed == null, renamedFrom: removed != null ? old : null);
                }
                break;
            }
        }
    }

    private void ApplyFile(IndexedFolder folder, string path, IndexEngine engine, bool replay, bool created, string? renamedFrom)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return;
        string name = info.Name;
        ExclusionRules rules;
        lock (_gate) rules = _rules;
        if (rules.IsExcludedPath(path, isFolder: false)) return;
        FileAttributes attributes = info.Attributes;
        bool hidden = (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 || name.StartsWith("~$", StringComparison.Ordinal);
        if (hidden && !Settings.IncludeHidden) return;
        if (folder.Types != null && !folder.Types.Contains(FileKinds.GroupOf(FileKinds.FromName(name)))) return;
        if (!folder.IncludeSubfolders && !string.Equals(Path.GetDirectoryName(path), folder.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;

        var file = new ScanFile
        {
            Name = name,
            Size = info.Length,
            Modified = info.LastWriteTime.Ticks,
            Created = info.CreationTime.Ticks,
            Indexed = DateTime.Now.Ticks,
            Flags = (attributes.HasFlag(FileAttributes.Hidden) ? FileFlags.Hidden : 0) | (attributes.HasFlag(FileAttributes.System) ? FileFlags.System : 0),
        };
        int id = engine.AddOrUpdateFile(path, file, markNew: renamedFrom == null, out bool added);
        if (id < 0 || replay) return;   // its folder isn't indexed (for example a hidden folder)

        QueueDelta(new IndexStore.UpsertFile(Path.GetDirectoryName(path)!, folder.Id, file));
        ChangeKind kind = renamedFrom != null ? ChangeKind.Renamed : added ? ChangeKind.Added : ChangeKind.Modified;
        if (added && renamedFrom == null) AddedThisSession++;
        Log(kind, path, renamedFrom, file.Size, folder);
    }

    private void AddTree(IndexedFolder folder, string path, IndexEngine engine, bool replay, string? renamedFrom)
    {
        if (!folder.IncludeSubfolders) return;
        ExclusionRules rules;
        lock (_gate) rules = _rules;
        if (rules.IsExcludedPath(path, isFolder: true)) return;
        if (!Settings.IncludeHidden)
        {
            try
            {
                if ((File.GetAttributes(path) & (FileAttributes.Hidden | FileAttributes.System)) != 0) return;
            }
            catch (IOException) { return; }
        }
        var dirs = FileScanner.Scan(new[] { new ScanRoot { RootId = folder.Id, Path = path, IncludeSubfolders = true, Types = folder.Types } },
                                    rules, Settings.IncludeHidden, skipFolder: IsOwnData);
        int added = engine.AddTree(dirs, markNew: renamedFrom == null);
        if (replay) return;

        foreach (ScanDir dir in dirs)
            foreach (ScanFile f in dir.Files)
                QueueDelta(new IndexStore.UpsertFile(dir.Path, folder.Id, f));
        if (renamedFrom != null)
        {
            Log(ChangeKind.Renamed, path, renamedFrom, -1, folder, isFolder: true);
            return;
        }
        AddedThisSession += added;
        int logged = 0;
        foreach (ScanDir dir in dirs)
            foreach (ScanFile f in dir.Files)
                if (logged++ < 100) Log(ChangeKind.Added, Path.Join(dir.Path, f.Name), null, f.Size, folder);
    }

    private void Log(ChangeKind kind, string path, string? oldPath, long size, IndexedFolder folder, bool isFolder = false)
    {
        var ev = new ActivityEvent
        {
            Id = Interlocked.Increment(ref _eventSeq), At = DateTime.Now, Kind = kind, Path = path, OldPath = oldPath,
            Size = isFolder ? -1 : size, RootId = folder.Id, RootName = folder.Name, IsFolder = isFolder,
        };
        lock (_eventsLock)
        {
            _events.Insert(0, ev);
            if (_events.Count > 300) _events.RemoveRange(300, _events.Count - 300);
            _eventTimes.Add(ev.At.Ticks);
        }
        lock (_pendingLock) _pendingEvents.Add(new EventRecord(ev.At.Ticks, kind.ToString(), path, oldPath, ev.Size, folder.Id));
        ActivityAdded?.Invoke(ev);
    }

    private void QueueDelta(IndexStore.Delta delta)
    {
        lock (_pendingLock) _pendingDeltas.Add(delta);
    }

    private void ScheduleFullSave() => _fullSaveDue = DateTime.Now.AddSeconds(4);

    // ==================================================================
    // Timer: scheduled refresh, batched writes, drives, battery
    // ==================================================================

    private void Tick()
    {
        if (_shutdown.IsCancellationRequested) return;
        try
        {
            _ticks++;
            DateTime now = DateTime.Now;

            // Scheduled refresh.
            if (now >= NextRefresh)
            {
                bool canRun = _job == null && IsLoaded && !IsCleared && Watching && !BatteryPaused && Folders.Any(f => !f.Paused && f.Status != RootStatus.Unreachable);
                if (canRun) TryRefresh("Scheduled refresh", quietWhenBusy: true);
                else if (_job == null) NextRefresh = now.AddMinutes(Settings.RefreshMinutes);
            }

            // Folders waiting for a catch-up rescan (drive back, watcher overflow).
            if (_job == null)
            {
                int? next = null;
                lock (_pendingRescans) if (_pendingRescans.Count > 0) next = _pendingRescans.Dequeue();
                if (next is int id && TryRescanFolder(id, quietWhenBusy: true) == null)
                    lock (_pendingRescans) _pendingRescans.Enqueue(id);
            }

            if (_ticks % 3 == 0) Flush();
            if (_ticks % 10 == 0) CheckDrives(quiet: false);
            if (_ticks % 30 == 0) CheckBattery();

            // Keep the "changes in the last 24 hours" list short.
            if (_ticks % 60 == 0)
            {
                long cutoff = now.AddDays(-1).Ticks;
                lock (_eventsLock) _eventTimes.RemoveAll(t => t < cutoff);
            }
            RaiseStatus();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("DriveSync tick: " + ex);
        }
    }

    private void Flush()
    {
        List<IndexStore.Delta> deltas;
        List<EventRecord> events;
        lock (_pendingLock)
        {
            deltas = _pendingDeltas.ToList();
            events = _pendingEvents.ToList();
            _pendingDeltas.Clear();
            _pendingEvents.Clear();
        }
        try
        {
            _store.ApplyDelta(deltas);
            _store.AddEvents(events);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("DriveSync flush: " + ex);
        }

        // Full save after bulk edits (exclusions, removals), and compaction when many rows changed.
        bool compact = _engine.DeltaSize > Math.Max(50_000, _engine.FileCount / 10);
        if ((DateTime.Now >= _fullSaveDue || compact) && _job == null && !IsCleared && _swapLock.Wait(0))
        {
            try
            {
                _fullSaveDue = DateTime.MaxValue;
                if (compact)
                {
                    lock (_applyLock) _scanning = true;
                    var newPaths = new List<string>();
                    List<ScanDir> all = _engine.Export(null, newPaths);
                    IndexEngine built = IndexEngine.Build(all, RootNames());
                    built.MarkNew(newPaths);
                    lock (_applyLock)
                    {
                        _engine = built;
                        ReplayDuringScan(built);
                        _scanning = false;
                    }
                    LastBuildMs = built.BuildMs;
                }
                _store.SaveIndex(_engine.Export());
            }
            finally { _swapLock.Release(); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    private void CheckBattery()
    {
        bool saver = false;
        try
        {
            if (OperatingSystem.IsWindows() && GetSystemPowerStatus(out SystemPowerStatus s))
                saver = s.SystemStatusFlag == 1;
        }
        catch (Exception) { saver = false; }
        bool paused = saver && Settings.PauseOnBatterySaver;
        if (paused != BatteryPaused)
        {
            BatteryPaused = paused;
            RefreshWatchers();
            RaiseStatus(force: true);
            Notify?.Invoke(paused
                ? new Notice(NoticeKind.Warn, "Indexing paused", "Battery saver is on. Search still works; DriveSync catches up when it turns off.")
                : new Notice(NoticeKind.Ok, "Indexing resumed", "Battery saver is off."));
        }
    }

    private void RaiseStatus(bool force = false)
    {
        long now = Environment.TickCount64;
        if (!force && now - Interlocked.Read(ref _lastStatus) < 100) return;
        Interlocked.Exchange(ref _lastStatus, now);
        StatusChanged?.Invoke();
    }

    private void RaiseIndexChanged()
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastIndexChanged) < 400) return;
        Interlocked.Exchange(ref _lastIndexChanged, now);
        IndexChanged?.Invoke();
    }

    /// <summary>Writes pending changes and stops watching. Called when DriveSync quits.</summary>
    public void Dispose()
    {
        _shutdown.Cancel();
        _tick?.Dispose();
        lock (_watchers)
        {
            foreach (FileSystemWatcher w in _watchers.Values) w.Dispose();
            _watchers.Clear();
        }
        try
        {
            if (_swapLock.Wait(3000))
            {
                Flush();
                if (_fullSaveDue != DateTime.MaxValue && !IsCleared)
                    _store.SaveIndex(_engine.Export());
                _swapLock.Release();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("DriveSync shutdown save: " + ex);
        }
    }
}
