using System.Diagnostics;
using System.Numerics;

namespace DriveSync.Core.Indexing;

public enum SortMode { Best, Name, Modified, Size }

/// <summary>Everything the Search page asks for.</summary>
public sealed class SearchRequest
{
    public string Query { get; init; } = "";
    public TypeGroup Type { get; init; } = TypeGroup.All;

    /// <summary>Only this indexed folder; -1 for all.</summary>
    public int RootId { get; init; } = -1;

    /// <summary>Only this folder and below (the Folder filter's Browse… choice); null for all.</summary>
    public string? ScopePath { get; init; }

    /// <summary>Only files modified at or after this time (ticks); 0 for any time.</summary>
    public long ModifiedSince { get; init; }

    public SortMode Sort { get; init; } = SortMode.Best;

    /// <summary>How many of the best matches to return in order.</summary>
    public int Take { get; init; } = 100;

    public bool HasFilters => Type != TypeGroup.All || RootId >= 0 || ScopePath != null || ModifiedSince > 0;
}

public sealed class SearchResult
{
    /// <summary>The best matches, best first.</summary>
    public int[] Ids { get; init; } = Array.Empty<int>();

    /// <summary>How many files matched in total.</summary>
    public int Total { get; init; }

    /// <summary>Time spent in the index lookup, in milliseconds.</summary>
    public double ElapsedMs { get; init; }

    /// <summary>True when nothing matched exactly and these are close matches (letters in order).</summary>
    public bool Fuzzy { get; init; }

    /// <summary>False when there was nothing to search for (empty box, no filters).</summary>
    public bool Ran { get; init; }

    public ParsedQuery Query { get; init; } = ParsedQuery.Parse("");
}

/// <summary>A copy of one file's index entry.</summary>
public readonly record struct FileView(
    int Id, string Name, string Folder, long Size, DateTime Modified, DateTime Created, DateTime Indexed,
    FileKind Kind, string Extension, int RootId, bool IsNew, bool IsDead, FileFlags Flags)
{
    public string FullPath => Path.Join(Folder, Name);
}

/// <summary>
/// The in-memory index: every folder and file, plus token maps for prefix search.
///
/// Folders are numbered in depth-first order and files are stored folder by folder, so a folder and
/// everything below it is one contiguous range of folder ids and one contiguous range of file ids.
/// That turns "this folder and below" (folder filters, in:, path terms) into range operations.
///
/// Name tokens and folder-name tokens live in sorted arrays with posting lists, so each query term is
/// a binary search plus a walk over the tokens that start with it. Terms are combined with bitset ANDs
/// and only the best K results are ordered, with a heap.
///
/// Changes from the file watcher go into a small delta (appended rows, tombstones) under a
/// reader/writer lock; full scans build a new engine off-thread which then replaces this one.
/// </summary>
public sealed class IndexEngine
{
    // ---- folders ----
    private string[] _dirPath;
    private int[] _dirParent, _dirRoot, _dirFileStart, _dirSubEnd, _dirSubFileEnd;
    private byte[] _dirDepth;
    private bool[] _dirDead;
    private int _dirCount;
    private readonly int _baseDirs;
    private readonly Dictionary<string, int> _dirByPath;

    // ---- files ----
    private string[] _name;
    private int[] _fileDir;
    private long[] _size, _modified, _created, _indexed;
    private FileKind[] _kind;
    private ushort[] _ext;
    private FileFlags[] _flags;
    private ulong[] _dead, _new;
    private int _fileCount, _deadCount;
    private readonly int _baseFiles;

    // ---- extensions ----
    private readonly List<string> _extNames;
    private readonly Dictionary<string, ushort> _extIds;
    private readonly List<FileKind> _extKinds;

    // ---- tokens of the built part ----
    private readonly string[] _nameTok;
    private readonly int[] _nameTokStart, _namePost;
    private readonly string[] _dirTok;
    private readonly int[] _dirTokStart, _dirPost;

    // ---- tokens of rows added since the build ----
    private readonly Dictionary<string, List<int>> _deltaNameTok = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<int>> _deltaDirTok = new(StringComparer.Ordinal);
    private readonly Dictionary<int, List<int>> _deltaFilesByDir = new();

    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly IReadOnlyDictionary<int, string> _rootNames;
    private readonly Dictionary<int, int> _rootDirs = new();

    /// <summary>How long the last build took, in milliseconds.</summary>
    public double BuildMs { get; private set; }

    public int DistinctTokens => _nameTok.Length + _dirTok.Length + _deltaNameTok.Count;

    public static IndexEngine Empty() => Build(new List<ScanDir>(), new Dictionary<int, string>());

    private IndexEngine(int dirCount, int fileCount, IReadOnlyDictionary<int, string> rootNames,
                        string[] nameTok, int[] nameTokStart, int[] namePost,
                        string[] dirTok, int[] dirTokStart, int[] dirPost,
                        List<string> extNames, Dictionary<string, ushort> extIds, List<FileKind> extKinds,
                        Dictionary<string, int> dirByPath)
    {
        _rootNames = rootNames;
        int dirCap = dirCount + 256, fileCap = fileCount + Math.Max(1024, fileCount / 50);
        _dirPath = new string[dirCap];
        _dirParent = new int[dirCap];
        _dirRoot = new int[dirCap];
        _dirFileStart = new int[dirCap];
        _dirSubEnd = new int[dirCap];
        _dirSubFileEnd = new int[dirCap];
        _dirDepth = new byte[dirCap];
        _dirDead = new bool[dirCap];
        _name = new string[fileCap];
        _fileDir = new int[fileCap];
        _size = new long[fileCap];
        _modified = new long[fileCap];
        _created = new long[fileCap];
        _indexed = new long[fileCap];
        _kind = new FileKind[fileCap];
        _ext = new ushort[fileCap];
        _flags = new FileFlags[fileCap];
        _dead = new ulong[Bits.WordsFor(fileCap)];
        _new = new ulong[Bits.WordsFor(fileCap)];
        _dirCount = _baseDirs = dirCount;
        _fileCount = _baseFiles = fileCount;
        _nameTok = nameTok;
        _nameTokStart = nameTokStart;
        _namePost = namePost;
        _dirTok = dirTok;
        _dirTokStart = dirTokStart;
        _dirPost = dirPost;
        _extNames = extNames;
        _extIds = extIds;
        _extKinds = extKinds;
        _dirByPath = dirByPath;
    }

    // ==================================================================
    // Building
    // ==================================================================

    /// <summary>
    /// Builds an engine from scanned or loaded folders. Runs on a background thread; the folders
    /// may be in any order and are sorted depth-first here (loads from the database are already sorted).
    /// </summary>
    /// <param name="rootNames">Display names of indexed folders; their words become searchable too.</param>
    /// <param name="progress">Called with 0..1 as the build advances.</param>
    public static IndexEngine Build(List<ScanDir> dirs, IReadOnlyDictionary<int, string> rootNames, Action<double>? progress = null)
    {
        var clock = Stopwatch.StartNew();

        // 1. Depth-first order: sorting by path with '\' ranked below every other character puts
        //    each folder directly before everything inside it. Loads from the database are already
        //    in this order, so check first (without allocating) and only sort when needed.
        ScanDir[] order = dirs.ToArray();
        bool sorted = true;
        for (int i = 1; i < order.Length && sorted; i++)
            sorted = CompareDirPaths(order[i - 1].Path, order[i].Path) <= 0;
        if (!sorted)
        {
            var keys = new string[order.Length];
            for (int i = 0; i < order.Length; i++) keys[i] = SortKey(order[i].Path);
            Array.Sort(keys, order, StringComparer.Ordinal);
        }
        progress?.Invoke(0.1);

        // Merge accidental duplicates (the same folder reached twice).
        var unique = new List<ScanDir>(order.Length);
        for (int i = 0; i < order.Length; i++)
        {
            if (i > 0 && CompareDirPaths(order[i - 1].Path, order[i].Path) == 0)
                unique[^1].Files.AddRange(order[i].Files);
            else
                unique.Add(order[i]);
        }

        int dirCount = unique.Count;
        int fileCount = 0;
        foreach (ScanDir d in unique)
            fileCount += d.Files.Count;

        var dirByPath = new Dictionary<string, int>(dirCount, StringComparer.OrdinalIgnoreCase);
        var extKinds = new List<FileKind>();
        var engine = new IndexEngine(dirCount, fileCount, rootNames,
            Array.Empty<string>(), new[] { 0 }, Array.Empty<int>(),
            Array.Empty<string>(), new[] { 0 }, Array.Empty<int>(),
            new List<string>(), new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase), extKinds, dirByPath);

        // 2. Folders: parents, depth, subtree ends, where each folder's files start.
        var stack = new Stack<int>();
        int fileCursor = 0;
        for (int d = 0; d < dirCount; d++)
        {
            string path = unique[d].Path;
            engine._dirPath[d] = path;
            engine._dirRoot[d] = unique[d].RootId;
            dirByPath[path] = d;
            string? parentPath = Path.GetDirectoryName(path);
            int parent = parentPath != null && dirByPath.TryGetValue(parentPath, out int p) ? p : -1;
            engine._dirParent[d] = parent;
            engine._dirDepth[d] = parent < 0 ? (byte)0 : (byte)Math.Min(255, engine._dirDepth[parent] + 1);
            while (stack.Count > 0 && stack.Peek() != parent)
                engine._dirSubEnd[stack.Pop()] = d;
            stack.Push(d);
            engine._dirFileStart[d] = fileCursor;
            fileCursor += unique[d].Files.Count;
        }
        while (stack.Count > 0)
            engine._dirSubEnd[stack.Pop()] = dirCount;
        for (int d = 0; d < dirCount; d++)
        {
            int end = engine._dirSubEnd[d];
            engine._dirSubFileEnd[d] = end < dirCount ? engine._dirFileStart[end] : fileCount;
        }
        progress?.Invoke(0.2);

        // 3. Folder-name tokens on another thread while the files are processed here.
        var dirTokens = Task.Run(() =>
        {
            var pool = new SpanStringPool(Math.Max(1024, dirCount / 2));
            var ids = new List<int>(dirCount * 2);
            var ends = new int[dirCount + 1];
            var counts = new List<int>();
            int rowStart = 0;
            void OnToken(ReadOnlySpan<char> token)
            {
                int id = pool.GetOrAdd(token);
                for (int k = rowStart; k < ids.Count; k++)
                    if (ids[k] == id) return;
                ids.Add(id);
                if (id == counts.Count) counts.Add(0);
                counts[id]++;
            }
            TokenHandler handler = OnToken;
            Span<char> buf = stackalloc char[128];
            for (int d = 0; d < dirCount; d++)
            {
                rowStart = ids.Count;
                Tokenizer.Tokenize(SegmentOf(engine._dirPath[d]), buf, handler);
                if (engine._dirParent[d] < 0 && rootNames.TryGetValue(engine._dirRoot[d], out string? display))
                    Tokenizer.Tokenize(display, buf, handler);
                ends[d + 1] = ids.Count;
            }
            return BuildPostings(pool, counts, ids, ends, dirCount);
        });

        // 4. Files: row arrays, extensions and name tokens.
        var extPool = new SpanStringPool(256);
        var tokenPool = new SpanStringPool(Math.Max(1024, fileCount / 2));
        var fileTokIds = new List<int>(fileCount * 4);
        var fileTokEnd = new int[fileCount + 1];
        var tokCounts = new List<int>();
        int fileTokStart = 0;
        void OnNameToken(ReadOnlySpan<char> token)
        {
            int id = tokenPool.GetOrAdd(token);
            for (int k = fileTokStart; k < fileTokIds.Count; k++)
                if (fileTokIds[k] == id)
                    return; // same token twice in one name
            fileTokIds.Add(id);
            if (id == tokCounts.Count) tokCounts.Add(0);
            tokCounts[id]++;
        }
        TokenHandler nameHandler = OnNameToken;

        Span<char> buffer = stackalloc char[128];
        int f = 0;
        for (int d = 0; d < dirCount; d++)
        {
            foreach (ScanFile file in unique[d].Files)
            {
                engine._name[f] = file.Name;
                engine._fileDir[f] = d;
                engine._size[f] = file.Size;
                engine._modified[f] = file.Modified;
                engine._created[f] = file.Created;
                engine._indexed[f] = file.Indexed;
                engine._flags[f] = file.Flags;

                ReadOnlySpan<char> ext = FileKinds.ExtensionOf(file.Name);
                Span<char> lowerExt = buffer[..Math.Min(ext.Length, buffer.Length)];
                ext[..lowerExt.Length].ToLowerInvariant(lowerExt);
                int extId = extPool.GetOrAdd(lowerExt);
                if (extId == extKinds.Count)
                    extKinds.Add(FileKinds.FromExtension(lowerExt));
                engine._ext[f] = (ushort)Math.Min(extId, ushort.MaxValue);
                engine._kind[f] = extKinds[extId];

                fileTokStart = fileTokIds.Count;
                Tokenizer.Tokenize(file.Name, buffer, nameHandler);
                fileTokEnd[f + 1] = fileTokIds.Count;
                f++;
            }
            if ((d & 0x3FFF) == 0)
                progress?.Invoke(0.2 + 0.5 * d / Math.Max(1, dirCount));
        }
        progress?.Invoke(0.7);

        var (nameTok, nameTokStart, namePost) = BuildPostings(tokenPool, tokCounts, fileTokIds, fileTokEnd, fileCount);
        progress?.Invoke(0.9);
        var (dirTok, dirTokStartArr, dirPost) = dirTokens.GetAwaiter().GetResult();

        var extNames = new List<string>(extPool.Count);
        var extIds = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < extPool.Count; i++)
        {
            extNames.Add(extPool[i]);
            extIds[extPool[i]] = (ushort)i;
        }

        var built = new IndexEngine(engine, nameTok, nameTokStart, namePost, dirTok, dirTokStartArr, dirPost, extNames, extIds);
        built.BuildMs = clock.Elapsed.TotalMilliseconds;
        progress?.Invoke(1);
        return built;
    }

    /// <summary>Compares folder paths in depth-first order: case-insensitive, with '\' before every other character.</summary>
    public static int CompareDirPaths(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            char x = a[i], y = b[i];
            if (x == y) continue;
            x = x == '\\' ? '\u0001' : char.ToUpperInvariant(x);
            y = y == '\\' ? '\u0001' : char.ToUpperInvariant(y);
            if (x != y) return x < y ? -1 : 1;
        }
        return a.Length.CompareTo(b.Length);
    }

    /// <summary>Takes the row arrays of a half-built engine and adds the token maps.</summary>
    private IndexEngine(IndexEngine shell, string[] nameTok, int[] nameTokStart, int[] namePost,
                        string[] dirTok, int[] dirTokStart, int[] dirPost, List<string> extNames, Dictionary<string, ushort> extIds)
    {
        _rootNames = shell._rootNames;
        _dirPath = shell._dirPath; _dirParent = shell._dirParent; _dirRoot = shell._dirRoot;
        _dirFileStart = shell._dirFileStart; _dirSubEnd = shell._dirSubEnd; _dirSubFileEnd = shell._dirSubFileEnd;
        _dirDepth = shell._dirDepth; _dirDead = shell._dirDead; _dirCount = shell._dirCount; _baseDirs = shell._baseDirs;
        _dirByPath = shell._dirByPath;
        _name = shell._name; _fileDir = shell._fileDir; _size = shell._size; _modified = shell._modified;
        _created = shell._created; _indexed = shell._indexed; _kind = shell._kind; _ext = shell._ext; _flags = shell._flags;
        _dead = shell._dead; _new = shell._new; _fileCount = shell._fileCount; _baseFiles = shell._baseFiles;
        _extKinds = shell._extKinds;
        _nameTok = nameTok; _nameTokStart = nameTokStart; _namePost = namePost;
        _dirTok = dirTok; _dirTokStart = dirTokStart; _dirPost = dirPost;
        _extNames = extNames; _extIds = extIds;
        for (int d = 0; d < _baseDirs; d++)
            if (_dirParent[d] < 0 && !_rootDirs.ContainsKey(_dirRoot[d]))
                _rootDirs[_dirRoot[d]] = d;
    }

    private static (string[] Tokens, int[] Start, int[] Postings) BuildPostings(
        SpanStringPool pool, List<int> counts, List<int> rowTokIds, int[] rowTokEnd, int rows)
    {
        int tokens = pool.Count;
        var start = new int[tokens + 1];
        for (int t = 0; t < tokens; t++)
            start[t + 1] = start[t] + counts[t];
        var fill = (int[])start.Clone();
        var postings = new int[start[tokens]];
        for (int r = 0; r < rows; r++)
            for (int k = rowTokEnd[r]; k < rowTokEnd[r + 1]; k++)
                postings[fill[rowTokIds[k]]++] = r;

        // Sort tokens and carry their posting ranges along.
        var names = new string[tokens];
        var ids = new int[tokens];
        for (int t = 0; t < tokens; t++) { names[t] = pool[t]; ids[t] = t; }
        Array.Sort(names, ids, StringComparer.Ordinal);

        var sortedStart = new int[tokens + 1];
        var sortedPost = new int[postings.Length];
        int pos = 0;
        for (int i = 0; i < tokens; i++)
        {
            int t = ids[i];
            sortedStart[i] = pos;
            int len = start[t + 1] - start[t];
            Array.Copy(postings, start[t], sortedPost, pos, len);
            pos += len;
        }
        sortedStart[tokens] = pos;
        return (names, sortedStart, sortedPost);
    }

    private static string SortKey(string path) => path.ToUpperInvariant().Replace('\\', '\u0001');

    private static ReadOnlySpan<char> SegmentOf(string path)
    {
        string trimmed = path.Length > 3 ? path.TrimEnd('\\') : path;
        int slash = trimmed.LastIndexOf('\\');
        return slash >= 0 && slash < trimmed.Length - 1 ? trimmed.AsSpan(slash + 1) : trimmed.AsSpan();
    }

    // ==================================================================
    // Counts and lookups
    // ==================================================================

    public int FileCount
    {
        get { _lock.EnterReadLock(); try { return _fileCount - _deadCount; } finally { _lock.ExitReadLock(); } }
    }

    public int FolderCount
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                int n = 0;
                for (int d = 0; d < _dirCount; d++) if (!_dirDead[d]) n++;
                return n;
            }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>Number of rows changed since the build (used to decide when to compact).</summary>
    public int DeltaSize
    {
        get { _lock.EnterReadLock(); try { return _fileCount - _baseFiles + _deadCount; } finally { _lock.ExitReadLock(); } }
    }

    /// <summary>Files currently in the index for one indexed folder.</summary>
    public int CountForRoot(int rootId)
    {
        _lock.EnterReadLock();
        try
        {
            int n = 0;
            if (_rootDirs.TryGetValue(rootId, out int d))
            {
                int s = _dirFileStart[d], e = _dirSubFileEnd[d];
                n += e - s - Bits.CountRange(_dead, s, e);
            }
            for (int f = _baseFiles; f < _fileCount; f++)
                if (_dirRoot[_fileDir[f]] == rootId && !Bits.Get(_dead, f))
                    n++;
            return n;
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Folders currently in the index for one indexed folder (to estimate scan progress).</summary>
    public int FolderCountForRoot(int rootId)
    {
        _lock.EnterReadLock();
        try
        {
            int n = 0;
            for (int d = 0; d < _dirCount; d++)
                if (_dirRoot[d] == rootId && !_dirDead[d]) n++;
            return n;
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Total size of the files of one indexed folder (or all, with -1).</summary>
    public long TotalSize(int rootId = -1)
    {
        _lock.EnterReadLock();
        try
        {
            long total = 0;
            for (int f = 0; f < _fileCount; f++)
                if (!Bits.Get(_dead, f) && (rootId < 0 || _dirRoot[_fileDir[f]] == rootId))
                    total += _size[f];
            return total;
        }
        finally { _lock.ExitReadLock(); }
    }

    public FileView GetFile(int id)
    {
        _lock.EnterReadLock();
        try { return ViewOf(id); }
        finally { _lock.ExitReadLock(); }
    }

    private FileView ViewOf(int id)
    {
        int dir = _fileDir[id];
        return new FileView(id, _name[id], _dirPath[dir], _size[id],
            new DateTime(_modified[id]), new DateTime(_created[id]), new DateTime(_indexed[id]),
            _kind[id], _extNames.Count > _ext[id] ? _extNames[_ext[id]] : "", _dirRoot[dir],
            Bits.Get(_new, id), Bits.Get(_dead, id), _flags[id]);
    }

    /// <summary>The file at this full path, or -1.</summary>
    public int FindFile(string fullPath)
    {
        _lock.EnterReadLock();
        try
        {
            string? dirPath = Path.GetDirectoryName(fullPath);
            if (dirPath == null || !_dirByPath.TryGetValue(dirPath, out int dir) || _dirDead[dir])
                return -1;
            return FindInDir(dir, Path.GetFileName(fullPath));
        }
        finally { _lock.ExitReadLock(); }
    }

    public bool ContainsFolder(string path)
    {
        _lock.EnterReadLock();
        try { return _dirByPath.TryGetValue(Path.TrimEndingDirectorySeparator(path), out int d) && !_dirDead[d] || _dirByPath.TryGetValue(path, out d) && !_dirDead[d]; }
        finally { _lock.ExitReadLock(); }
    }

    private int FindInDir(int dir, string name)
    {
        if (dir < _baseDirs)
        {
            int end = dir + 1 < _baseDirs ? _dirFileStart[dir + 1] : _baseFiles;
            for (int f = _dirFileStart[dir]; f < end; f++)
                if (!Bits.Get(_dead, f) && string.Equals(_name[f], name, StringComparison.OrdinalIgnoreCase))
                    return f;
        }
        if (_deltaFilesByDir.TryGetValue(dir, out List<int>? extra))
            foreach (int f in extra)
                if (!Bits.Get(_dead, f) && string.Equals(_name[f], name, StringComparison.OrdinalIgnoreCase))
                    return f;
        return -1;
    }

    // ==================================================================
    // Searching
    // ==================================================================

    public SearchResult Search(SearchRequest request)
    {
        var clock = Stopwatch.StartNew();
        ParsedQuery query = ParsedQuery.Parse(request.Query);
        if (query.IsEmpty && !request.HasFilters)
            return new SearchResult { Query = query, Ran = false };

        var termNamed = new List<ulong[]>();
        var termExact = new List<ulong[]>();
        ulong[]? candidates = null;

        _lock.EnterReadLock();
        try
        {
            int n = _fileCount;
            int words = Bits.WordsFor(n);

            // --- scope: indexed folder or Browse… folder ---
            int scopeDir = -1, scopeRoot = request.RootId;
            int lo = 0, hi = _baseFiles;
            if (request.ScopePath != null)
            {
                string scopePath = Path.TrimEndingDirectorySeparator(request.ScopePath);
                if (!_dirByPath.TryGetValue(scopePath, out scopeDir) && !_dirByPath.TryGetValue(request.ScopePath, out scopeDir) || _dirDead[scopeDir])
                    return Done(query, Array.Empty<int>(), 0, false, clock);
                if (scopeDir < _baseDirs) { lo = _dirFileStart[scopeDir]; hi = _dirSubFileEnd[scopeDir]; }
                else { lo = hi = 0; }
            }
            if (scopeRoot >= 0)
            {
                int rootDir = _rootDirs.TryGetValue(scopeRoot, out int rd) ? rd : -1;
                if (rootDir >= 0) { lo = Math.Max(lo, _dirFileStart[rootDir]); hi = Math.Min(hi, _dirSubFileEnd[rootDir]); }
                else { lo = hi = 0; }
            }

            // --- filters that apply to each file ---
            var kindOk = new bool[16];
            var typeKinds = query.Type != null ? FileKinds.MatchTypeOperator(query.Type) : null;
            foreach (FileKind k in Enum.GetValues<FileKind>())
                kindOk[(int)k] = FileKinds.InGroup(k, request.Type) && (typeKinds == null || typeKinds.Contains(k));
            int extFilter = -1;
            if (query.Ext != null)
            {
                if (!_extIds.TryGetValue(query.Ext, out ushort e))
                    return Done(query, Array.Empty<int>(), 0, false, clock);
                extFilter = e;
            }
            long since = request.ModifiedSince;

            // --- terms ---
            foreach (string term in query.Terms)
            {
                ulong[] named = Bits.Rent(n), exact = Bits.Rent(n);
                termNamed.Add(named);
                termExact.Add(exact);
                MarkNameMatches(term, named, exact);
                ulong[] any = Bits.Rent(n);
                Array.Copy(named, any, words);
                MarkFolderTermMatches(term, any);
                if (candidates == null) candidates = any;
                else { Bits.And(candidates, any, words); Bits.Return(any); }
            }

            // --- in: operator ---
            if (query.In != null)
            {
                ulong[] inMask = Bits.Rent(n);
                MarkFolderContains(query.In, inMask);
                if (candidates == null) candidates = inMask;
                else { Bits.And(candidates, inMask, words); Bits.Return(inMask); }
            }

            // --- walk the candidates ---
            var top = new TopK(this, Math.Max(1, request.Take), request.Sort);
            int total = 0;
            long now = DateTime.Now.Ticks;
            string? firstTerm = query.Terms.Count > 0 ? query.Terms[0] : null;

            bool Accept(int id)
            {
                if (Bits.Get(_dead, id)) return false;
                if (!kindOk[(int)_kind[id]]) return false;
                if (extFilter >= 0 && _ext[id] != extFilter) return false;
                if (since > 0 && _modified[id] < since) return false;
                return true;
            }

            double Score(int id)
            {
                double s = 0;
                for (int t = 0; t < termNamed.Count; t++)
                    s += Bits.Get(termExact[t], id) ? 6 : Bits.Get(termNamed[t], id) ? 4 : 1;
                if (firstTerm != null && _name[id].StartsWith(firstTerm, StringComparison.OrdinalIgnoreCase)) s += 3;
                double ageDays = (now - _modified[id]) / (double)TimeSpan.TicksPerDay;
                s += Math.Max(0, 2 - ageDays / 30);
                s -= _dirDepth[_fileDir[id]] * 0.08;
                if (Bits.Get(_new, id)) s += 0.5;
                return s;
            }

            bool needScore = request.Sort == SortMode.Best;
            if (candidates == null)
            {
                for (int id = lo; id < hi; id++)
                {
                    if (!Accept(id)) continue;
                    total++;
                    top.Add(id, needScore ? Score(id) : 0);
                }
            }
            else
            {
                int wFirst = lo >> 6, wLast = Bits.WordsFor(hi);
                for (int w = wFirst; w < wLast; w++)
                {
                    ulong bits = candidates[w];
                    while (bits != 0)
                    {
                        int id = (w << 6) + BitOperations.TrailingZeroCount(bits);
                        bits &= bits - 1;
                        if (id < lo || id >= hi) continue;
                        if (!Accept(id)) continue;
                        total++;
                        top.Add(id, needScore ? Score(id) : 0);
                    }
                }
            }
            // Rows added since the build.
            for (int id = _baseFiles; id < _fileCount; id++)
            {
                if (candidates != null && !Bits.Get(candidates, id)) continue;
                if (!InScope(_fileDir[id], scopeDir, scopeRoot)) continue;
                if (!Accept(id)) continue;
                total++;
                top.Add(id, needScore ? Score(id) : 0);
            }

            if (total == 0 && query.Terms.Count > 0)
                return CloseMatches(query, request, kindOk, extFilter, since, lo, hi, scopeDir, scopeRoot, clock);

            return Done(query, top.ToSortedArray(), total, false, clock);
        }
        finally
        {
            _lock.ExitReadLock();
            foreach (ulong[] b in termNamed) Bits.Return(b);
            foreach (ulong[] b in termExact) Bits.Return(b);
            Bits.Return(candidates);
        }
    }

    private static SearchResult Done(ParsedQuery query, int[] ids, int total, bool fuzzy, Stopwatch clock) =>
        new() { Query = query, Ids = ids, Total = total, Fuzzy = fuzzy, Ran = true, ElapsedMs = clock.Elapsed.TotalMilliseconds };

    private bool InScope(int dir, int scopeDir, int scopeRoot)
    {
        if (scopeRoot >= 0 && _dirRoot[dir] != scopeRoot) return false;
        if (scopeDir < 0) return true;
        for (int d = dir; d >= 0; d = _dirParent[d])
            if (d == scopeDir) return true;
        return false;
    }

    /// <summary>Marks files with a name token starting with <paramref name="term"/>; exact token matches also in <paramref name="exact"/>.</summary>
    private void MarkNameMatches(string term, ulong[] named, ulong[] exact)
    {
        (int a, int b) = PrefixRange(_nameTok, term);
        for (int t = a; t < b; t++)
        {
            bool isExact = _nameTok[t].Length == term.Length;
            for (int p = _nameTokStart[t]; p < _nameTokStart[t + 1]; p++)
            {
                int id = _namePost[p];
                Bits.Set(named, id);
                if (isExact) Bits.Set(exact, id);
            }
        }
        foreach (var (token, ids) in _deltaNameTok)
        {
            if (!token.StartsWith(term, StringComparison.Ordinal)) continue;
            bool isExact = token.Length == term.Length;
            foreach (int id in ids)
            {
                Bits.Set(named, id);
                if (isExact) Bits.Set(exact, id);
            }
        }
    }

    /// <summary>Marks every file inside a folder whose name has a token starting with <paramref name="term"/>.</summary>
    private void MarkFolderTermMatches(string term, ulong[] files)
    {
        (int a, int b) = PrefixRange(_dirTok, term);
        bool anyDelta = _deltaDirTok.Count > 0 && _deltaDirTok.Keys.Any(k => k.StartsWith(term, StringComparison.Ordinal));
        if (a == b && !anyDelta)
            return;

        // Matching folders in depth-first order; a folder inside one already marked is skipped.
        var matched = new List<int>();
        for (int t = a; t < b; t++)
            for (int p = _dirTokStart[t]; p < _dirTokStart[t + 1]; p++)
                matched.Add(_dirPost[p]);
        matched.Sort();
        int coveredUntil = -1;
        foreach (int d in matched)
        {
            if (d < coveredUntil) continue;
            Bits.SetRange(files, _dirFileStart[d], _dirSubFileEnd[d]);
            coveredUntil = _dirSubEnd[d];
        }

        if (_fileCount == _baseFiles && _dirCount == _baseDirs)
            return;
        // Rows added since the build: a folder matches if it or any parent matched.
        var hit = new bool[_dirCount];
        foreach (int d in matched) hit[d] = true;
        foreach (var (token, ids) in _deltaDirTok)
            if (token.StartsWith(term, StringComparison.Ordinal))
                foreach (int d in ids) hit[d] = true;
        for (int d = 0; d < _dirCount; d++)
            if (!hit[d] && _dirParent[d] >= 0 && hit[_dirParent[d]]) hit[d] = true;
        for (int f = _baseFiles; f < _fileCount; f++)
            if (hit[_fileDir[f]]) Bits.Set(files, f);
    }

    /// <summary>The in: operator: files whose folder path contains <paramref name="text"/>.</summary>
    private void MarkFolderContains(string text, ulong[] files)
    {
        bool fullPath = text.Contains('\\');
        var hit = new bool[_dirCount];
        for (int d = 0; d < _dirCount; d++)
        {
            int parent = _dirParent[d];
            hit[d] = (parent >= 0 && hit[parent]) ||
                     (fullPath ? _dirPath[d].Contains(text, StringComparison.OrdinalIgnoreCase)
                               : SegmentOf(_dirPath[d]).Contains(text, StringComparison.OrdinalIgnoreCase));
            if (hit[d] && d < _baseDirs && (parent < 0 || !hit[parent]))
                Bits.SetRange(files, _dirFileStart[d], _dirSubFileEnd[d]);
        }
        for (int f = _baseFiles; f < _fileCount; f++)
            if (hit[_fileDir[f]]) Bits.Set(files, f);
    }

    private static (int, int) PrefixRange(string[] sorted, string prefix)
    {
        int a = LowerBound(sorted, prefix);
        int b = LowerBound(sorted, prefix + char.MaxValue);
        return (a, b);
    }

    private static int LowerBound(string[] sorted, string value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (string.CompareOrdinal(sorted[mid], value) < 0) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>
    /// Nothing matched exactly: look for names containing the typed letters, first as one piece
    /// ("eport" in "Report"), then in order with gaps ("rpt" in "Report").
    /// </summary>
    private SearchResult CloseMatches(ParsedQuery query, SearchRequest request, bool[] kindOk, int extFilter, long since,
                                      int lo, int hi, int scopeDir, int scopeRoot, Stopwatch clock)
    {
        string needle = string.Concat(query.Terms);
        int take = Math.Max(1, request.Take);
        int threads = Math.Max(1, Environment.ProcessorCount);
        var tops = new TopK[threads];
        var counts = new int[threads];
        int chunk = (_fileCount + threads - 1) / threads;

        Parallel.For(0, threads, t =>
        {
            var top = new TopK(this, take, request.Sort == SortMode.Best ? SortMode.Best : request.Sort);
            int count = 0;
            int start = t * chunk, end = Math.Min(_fileCount, start + chunk);
            for (int id = start; id < end; id++)
            {
                if (id < _baseFiles && (id < lo || id >= hi)) continue;
                if (id >= _baseFiles && !InScope(_fileDir[id], scopeDir, scopeRoot)) continue;
                if (Bits.Get(_dead, id) || !kindOk[(int)_kind[id]]) continue;
                if (extFilter >= 0 && _ext[id] != extFilter) continue;
                if (since > 0 && _modified[id] < since) continue;
                string name = _name[id];
                double score;
                if (name.Contains(needle, StringComparison.OrdinalIgnoreCase)) score = 2;
                else if (IsSubsequence(needle, name)) score = 1;
                else continue;
                count++;
                top.Add(id, score);
            }
            tops[t] = top;
            counts[t] = count;
        });

        var merged = new TopK(this, take, request.Sort);
        foreach (TopK top in tops)
            top.MergeInto(merged);
        return Done(query, merged.ToSortedArray(), counts.Sum(), counts.Sum() > 0, clock);
    }

    /// <summary>
    /// True if the letters of <paramref name="needle"/> appear in <paramref name="name"/> in order and close
    /// together (within twice the needle's length), so "rpt" finds "Report" but a long string of
    /// unrelated characters doesn't match by chance.
    /// </summary>
    private static bool IsSubsequence(string needle, string name)
    {
        int limit = needle.Length * 2;
        for (int start = 0; start <= name.Length - needle.Length; start++)
        {
            if (char.ToLowerInvariant(name[start]) != needle[0]) continue;
            int i = 1;
            int end = Math.Min(name.Length, start + limit);
            for (int j = start + 1; j < end && i < needle.Length; j++)
                if (char.ToLowerInvariant(name[j]) == needle[i]) i++;
            if (i == needle.Length) return true;
        }
        return false;
    }

    /// <summary>Keeps the best K ids seen so far in a heap with the worst on top.</summary>
    private sealed class TopK
    {
        private readonly IndexEngine _e;
        private readonly int _k;
        private readonly SortMode _sort;
        private readonly int[] _ids;
        private readonly double[] _scores;
        private int _count;

        public TopK(IndexEngine engine, int k, SortMode sort)
        {
            _e = engine; _k = k; _sort = sort;
            _ids = new int[k];
            _scores = new double[k];
        }

        /// <summary>Negative if a ranks before b.</summary>
        private int Compare(int a, double sa, int b, double sb)
        {
            int c;
            switch (_sort)
            {
                case SortMode.Name:
                    c = string.Compare(_e._name[a], _e._name[b], StringComparison.OrdinalIgnoreCase);
                    break;
                case SortMode.Modified:
                    c = _e._modified[b].CompareTo(_e._modified[a]);
                    break;
                case SortMode.Size:
                    c = _e._size[b].CompareTo(_e._size[a]);
                    break;
                default:
                    c = sb.CompareTo(sa);
                    if (c == 0) c = _e._name[a].Length.CompareTo(_e._name[b].Length);
                    if (c == 0) c = string.Compare(_e._name[a], _e._name[b], StringComparison.OrdinalIgnoreCase);
                    break;
            }
            return c != 0 ? c : a.CompareTo(b);
        }

        public void Add(int id, double score)
        {
            if (_count < _k)
            {
                int i = _count++;
                _ids[i] = id; _scores[i] = score;
                SiftUp(i);
            }
            else if (Compare(id, score, _ids[0], _scores[0]) < 0)
            {
                _ids[0] = id; _scores[0] = score;
                SiftDown(0);
            }
        }

        public void MergeInto(TopK other)
        {
            for (int i = 0; i < _count; i++)
                other.Add(_ids[i], _scores[i]);
        }

        // Max-heap on "worse".
        private void SiftUp(int i)
        {
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (Compare(_ids[i], _scores[i], _ids[parent], _scores[parent]) <= 0) break;
                Swap(i, parent);
                i = parent;
            }
        }

        private void SiftDown(int i)
        {
            while (true)
            {
                int l = 2 * i + 1, r = l + 1, worst = i;
                if (l < _count && Compare(_ids[l], _scores[l], _ids[worst], _scores[worst]) > 0) worst = l;
                if (r < _count && Compare(_ids[r], _scores[r], _ids[worst], _scores[worst]) > 0) worst = r;
                if (worst == i) break;
                Swap(i, worst);
                i = worst;
            }
        }

        private void Swap(int a, int b)
        {
            (_ids[a], _ids[b]) = (_ids[b], _ids[a]);
            (_scores[a], _scores[b]) = (_scores[b], _scores[a]);
        }

        public int[] ToSortedArray()
        {
            var order = new int[_count];
            for (int i = 0; i < _count; i++) order[i] = i;
            Array.Sort(order, (x, y) => Compare(_ids[x], _scores[x], _ids[y], _scores[y]));
            var result = new int[_count];
            for (int i = 0; i < _count; i++) result[i] = _ids[order[i]];
            return result;
        }
    }

    // ==================================================================
    // Live changes (file watcher)
    // ==================================================================

    /// <summary>
    /// Adds a file, or updates it if it is already indexed. The folder must already be in the index
    /// (or be creatable from an indexed parent). Returns the file id, or -1 if the folder isn't indexed.
    /// </summary>
    public int AddOrUpdateFile(string fullPath, ScanFile file, bool markNew, out bool added)
    {
        added = false;
        string? dirPath = Path.GetDirectoryName(fullPath);
        if (dirPath == null)
            return -1;

        _lock.EnterWriteLock();
        try
        {
            if (!_dirByPath.TryGetValue(dirPath, out int dir) || _dirDead[dir])
                return -1;
            int existing = FindInDir(dir, file.Name);
            if (existing >= 0)
            {
                _size[existing] = file.Size;
                _modified[existing] = file.Modified;
                _indexed[existing] = file.Indexed;
                _flags[existing] = file.Flags;
                return existing;
            }
            added = true;
            return AppendFile(dir, file, markNew);
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Adds a folder (whose parent is indexed) and the folders and files below it.</summary>
    public int AddTree(List<ScanDir> dirs, bool markNew)
    {
        _lock.EnterWriteLock();
        try
        {
            int added = 0;
            foreach (ScanDir dir in dirs.OrderBy(d => d.Path.Length))
            {
                int d = EnsureDir(dir.Path, dir.RootId);
                if (d < 0) continue;
                foreach (ScanFile file in dir.Files)
                {
                    if (FindInDir(d, file.Name) >= 0) continue;
                    AppendFile(d, file, markNew);
                    added++;
                }
            }
            return added;
        }
        finally { _lock.ExitWriteLock(); }
    }

    private int EnsureDir(string path, int rootId)
    {
        if (_dirByPath.TryGetValue(path, out int existing))
        {
            if (_dirDead[existing]) _dirDead[existing] = false;
            return existing;
        }
        string? parentPath = Path.GetDirectoryName(path);
        if (parentPath == null || !_dirByPath.TryGetValue(parentPath, out int parent))
            return -1;
        if (_dirDead[parent]) _dirDead[parent] = false;

        if (_dirCount == _dirPath.Length)
        {
            int cap = _dirPath.Length * 2;
            Array.Resize(ref _dirPath, cap); Array.Resize(ref _dirParent, cap); Array.Resize(ref _dirRoot, cap);
            Array.Resize(ref _dirFileStart, cap); Array.Resize(ref _dirSubEnd, cap); Array.Resize(ref _dirSubFileEnd, cap);
            Array.Resize(ref _dirDepth, cap); Array.Resize(ref _dirDead, cap);
        }
        int d = _dirCount++;
        _dirPath[d] = path;
        _dirParent[d] = parent;
        _dirRoot[d] = _dirRoot[parent];
        _dirDepth[d] = (byte)Math.Min(255, _dirDepth[parent] + 1);
        _dirFileStart[d] = _dirSubFileEnd[d] = 0;
        _dirSubEnd[d] = d + 1;
        _dirDead[d] = false;
        _dirByPath[path] = d;

        Span<char> buffer = stackalloc char[128];
        Tokenizer.Tokenize(SegmentOf(path), buffer, token =>
        {
            string key = token.ToString();
            if (!_deltaDirTok.TryGetValue(key, out List<int>? list))
                _deltaDirTok[key] = list = new List<int>();
            list.Add(d);
        });
        return d;
    }

    private int AppendFile(int dir, ScanFile file, bool markNew)
    {
        if (_fileCount == _name.Length)
        {
            int cap = _name.Length * 2;
            Array.Resize(ref _name, cap); Array.Resize(ref _fileDir, cap); Array.Resize(ref _size, cap);
            Array.Resize(ref _modified, cap); Array.Resize(ref _created, cap); Array.Resize(ref _indexed, cap);
            Array.Resize(ref _kind, cap); Array.Resize(ref _ext, cap); Array.Resize(ref _flags, cap);
            Array.Resize(ref _dead, Bits.WordsFor(cap)); Array.Resize(ref _new, Bits.WordsFor(cap));
        }
        int id = _fileCount++;
        _name[id] = file.Name;
        _fileDir[id] = dir;
        _size[id] = file.Size;
        _modified[id] = file.Modified;
        _created[id] = file.Created;
        _indexed[id] = file.Indexed;
        _flags[id] = file.Flags;

        string ext = FileKinds.ExtensionOf(file.Name).ToString().ToLowerInvariant();
        if (!_extIds.TryGetValue(ext, out ushort extId))
        {
            extId = (ushort)Math.Min(_extNames.Count, ushort.MaxValue);
            _extNames.Add(ext);
            _extKinds.Add(FileKinds.FromExtension(ext));
            _extIds[ext] = extId;
        }
        _ext[id] = extId;
        _kind[id] = FileKinds.FromExtension(ext);
        Bits.Clear(_dead, id);
        if (markNew) Bits.Set(_new, id); else Bits.Clear(_new, id);

        if (!_deltaFilesByDir.TryGetValue(dir, out List<int>? inDir))
            _deltaFilesByDir[dir] = inDir = new List<int>();
        inDir.Add(id);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        Span<char> buffer = stackalloc char[128];
        Tokenizer.Tokenize(file.Name, buffer, token =>
        {
            string key = token.ToString();
            if (!seen.Add(key)) return;
            if (!_deltaNameTok.TryGetValue(key, out List<int>? list))
                _deltaNameTok[key] = list = new List<int>();
            list.Add(id);
        });
        return id;
    }

    /// <summary>Removes one file. Returns its last known entry, or null if it wasn't indexed.</summary>
    public FileView? RemoveFile(string fullPath)
    {
        _lock.EnterWriteLock();
        try
        {
            string? dirPath = Path.GetDirectoryName(fullPath);
            if (dirPath == null || !_dirByPath.TryGetValue(dirPath, out int dir) || _dirDead[dir])
                return null;
            int f = FindInDir(dir, Path.GetFileName(fullPath));
            if (f < 0) return null;
            Kill(f, null);
            return ViewOf(f);
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Removes a folder and everything below it. Returns how many files were removed;
    /// the ids go into the lists so the removal can be undone with <see cref="Revive"/>.
    /// </summary>
    public int RemoveFolder(string path, List<int>? removedFiles = null, List<int>? removedDirs = null)
    {
        _lock.EnterWriteLock();
        try
        {
            string key = Path.TrimEndingDirectorySeparator(path);
            if (!_dirByPath.TryGetValue(key, out int d) && !_dirByPath.TryGetValue(path, out d))
                return 0;
            return KillSubtree(d, removedFiles, removedDirs);
        }
        finally { _lock.ExitWriteLock(); }
    }

    private int KillSubtree(int d, List<int>? removedFiles, List<int>? removedDirs)
    {
        int removed = 0;
        if (d < _baseDirs)
        {
            for (int x = d; x < _dirSubEnd[d]; x++)
                if (!_dirDead[x]) { _dirDead[x] = true; removedDirs?.Add(x); }
            for (int f = _dirFileStart[d]; f < _dirSubFileEnd[d]; f++)
                if (Kill(f, removedFiles)) removed++;
        }
        else if (!_dirDead[d])
        {
            _dirDead[d] = true;
            removedDirs?.Add(d);
        }
        // Rows added since the build that live below this folder.
        for (int x = _baseDirs; x < _dirCount; x++)
        {
            if (_dirDead[x]) continue;
            for (int p = _dirParent[x]; p >= 0; p = _dirParent[p])
                if (p == d) { _dirDead[x] = true; removedDirs?.Add(x); break; }
        }
        for (int f = _baseFiles; f < _fileCount; f++)
            if (_dirDead[_fileDir[f]] && Kill(f, removedFiles)) removed++;
        return removed;
    }

    private bool Kill(int f, List<int>? removed)
    {
        if (Bits.Get(_dead, f)) return false;
        Bits.Set(_dead, f);
        _deadCount++;
        removed?.Add(f);
        return true;
    }

    /// <summary>Brings back files and folders removed earlier (Undo).</summary>
    public void Revive(IEnumerable<int> files, IEnumerable<int> dirs)
    {
        _lock.EnterWriteLock();
        try
        {
            foreach (int d in dirs) if (d < _dirCount) _dirDead[d] = false;
            foreach (int f in files)
            {
                if (f < _fileCount && Bits.Get(_dead, f))
                {
                    Bits.Clear(_dead, f);
                    _deadCount--;
                }
            }
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Removes everything a new exclusion rule covers. Returns how many files were removed.</summary>
    public int ApplyExclusion(string pattern, List<int>? removedFiles = null, List<int>? removedDirs = null)
    {
        var rules = new ExclusionRules(new[] { pattern });
        _lock.EnterWriteLock();
        try
        {
            int removed = 0;
            switch (ExclusionRules.Classify(pattern))
            {
                case ExclusionRules.RuleKind.Extension:
                    for (int f = 0; f < _fileCount; f++)
                        if (!Bits.Get(_dead, f) && rules.IsExcludedFile(_name[f]) && Kill(f, removedFiles)) removed++;
                    break;
                default:
                    for (int d = 0; d < _dirCount; d++)
                    {
                        if (_dirDead[d] || _dirParent[d] < 0) continue;
                        string path = _dirPath[d];
                        if (rules.IsExcludedFolder(path, SegmentOf(path)))
                            removed += KillSubtree(d, removedFiles, removedDirs);
                    }
                    break;
            }
            return removed;
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Removes hidden and system files and Office lock files (when "Include hidden" is turned off).</summary>
    public int RemoveHidden()
    {
        _lock.EnterWriteLock();
        try
        {
            int removed = 0;
            for (int f = 0; f < _fileCount; f++)
                if (!Bits.Get(_dead, f) && (_flags[f] != FileFlags.None || _name[f].StartsWith("~$", StringComparison.Ordinal)) && Kill(f, null))
                    removed++;
            return removed;
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Clears the "New" tag from every file.</summary>
    public void ClearNewTags()
    {
        _lock.EnterWriteLock();
        try { Array.Clear(_new); }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Every live folder and file, for saving and for rebuilding. Folders that <paramref name="keep"/>
    /// rejects (given the indexed folder id and the folder path) are left out.
    /// </summary>
    public List<ScanDir> Export(Func<int, string, bool>? keep = null, List<string>? newPaths = null)
    {
        _lock.EnterReadLock();
        try
        {
            var result = new List<ScanDir>(_dirCount);
            for (int d = 0; d < _dirCount; d++)
            {
                if (_dirDead[d] || (keep != null && !keep(_dirRoot[d], _dirPath[d]))) continue;
                var dir = new ScanDir(_dirPath[d], _dirRoot[d]);
                if (d < _baseDirs)
                {
                    int end = d + 1 < _baseDirs ? _dirFileStart[d + 1] : _baseFiles;
                    for (int f = _dirFileStart[d]; f < end; f++)
                        if (!Bits.Get(_dead, f)) Add(dir, f);
                }
                if (_deltaFilesByDir.TryGetValue(d, out List<int>? extra))
                    foreach (int f in extra)
                        if (!Bits.Get(_dead, f)) Add(dir, f);
                result.Add(dir);
            }
            return result;

            void Add(ScanDir dir, int f)
            {
                dir.Files.Add(new ScanFile { Name = _name[f], Size = _size[f], Modified = _modified[f], Created = _created[f], Indexed = _indexed[f], Flags = _flags[f] });
                if (newPaths != null && Bits.Get(_new, f)) newPaths.Add(Path.Join(dir.Path, _name[f]));
            }
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Marks these paths as new (used to carry "New" tags over a rebuild).</summary>
    public void MarkNew(IEnumerable<string> paths)
    {
        _lock.EnterWriteLock();
        try
        {
            foreach (string p in paths)
            {
                string? dirPath = Path.GetDirectoryName(p);
                if (dirPath == null || !_dirByPath.TryGetValue(dirPath, out int d)) continue;
                int f = FindInDir(d, Path.GetFileName(p));
                if (f >= 0) Bits.Set(_new, f);
            }
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Paths of every indexed folder in this root (for the debug/benchmark tools).</summary>
    public List<string> SamplePaths(int max)
    {
        _lock.EnterReadLock();
        try
        {
            var list = new List<string>(Math.Min(max, _fileCount));
            for (int f = 0; f < _fileCount && list.Count < max; f++)
                if (!Bits.Get(_dead, f)) list.Add(Path.Join(_dirPath[_fileDir[f]], _name[f]));
            return list;
        }
        finally { _lock.ExitReadLock(); }
    }
}
