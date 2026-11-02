using System.Diagnostics;

namespace DriveSync.Core;

/// <summary>
/// The in-memory file name index: every folder we know about and the files in it.
///
/// Searching walks through all file names in memory, using every CPU core at once.
/// Because nothing touches the disk during a search, it is far faster than asking
/// Windows to walk the folders again.
///
/// Several threads use the index at the same time (searches, the file watcher,
/// the background scan), so a ReaderWriterLockSlim protects it: many searches may
/// read together, but a change waits until no one is reading.
/// </summary>
public sealed class FileIndex
{
    private Dictionary<string, FolderNode> _folders;
    private readonly ReaderWriterLockSlim _lock = new();

    public FileIndex() : this(new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase)) { }

    private FileIndex(Dictionary<string, FolderNode> folders)
    {
        _folders = folders;
    }

    /// <summary>Builds an index from folders produced by <see cref="FileScanner"/> or <see cref="IndexStore"/>.</summary>
    public static FileIndex FromFolders(IEnumerable<FolderNode> folders)
    {
        var map = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
            map[folder.Path] = folder;
        return new FileIndex(map);
    }

    public int FolderCount
    {
        get
        {
            _lock.EnterReadLock();
            try { return _folders.Count; }
            finally { _lock.ExitReadLock(); }
        }
    }

    public int FileCount
    {
        get
        {
            _lock.EnterReadLock();
            try { return _folders.Values.Sum(f => f.Files.Count); }
            finally { _lock.ExitReadLock(); }
        }
    }

    // ------------------------------------------------------------------
    // Searching
    // ------------------------------------------------------------------

    /// <summary>
    /// Finds files whose name contains every word of the query (case does not matter).
    /// "budget 2026 .xlsx" finds "Budget-2026-final.xlsx".
    /// </summary>
    /// <param name="extension">
    /// Only return files of this type, for example "pdf" (no dot). Null means every type.
    /// With a type chosen, an empty query lists all files of that type.
    /// </param>
    /// <param name="folder">
    /// Only search this folder and the folders below it, for example "C:\Users\Ayesha\Documents".
    /// Null means every drive. With a folder chosen, an empty query lists the files in it.
    /// </param>
    public SearchResults Search(string query, int maxResults = 500, string? extension = null, string? folder = null)
    {
        string[] words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? suffix = string.IsNullOrEmpty(extension) ? null : "." + extension;
        string? scope = string.IsNullOrEmpty(folder) ? null : Path.TrimEndingDirectorySeparator(folder);
        if (words.Length == 0 && suffix == null && scope == null)
            return SearchResults.Empty;

        var stopwatch = Stopwatch.StartNew();
        var matches = new List<(FileEntry File, int Score)>();

        _lock.EnterReadLock();
        try
        {
            // With a folder chosen, folders outside it are dropped before any of their files are looked at.
            // One path check per folder is much cheaper than checking every file, so this makes the search
            // faster, not slower.
            FolderNode[] folders = scope == null
                ? _folders.Values.ToArray()
                : _folders.Values.Where(f => IsInFolder(f.Path, scope)).ToArray();

            // Each thread collects its own list of matches (no locking needed),
            // and at the end the lists are added together.
            Parallel.ForEach(
                folders,
                () => new List<(FileEntry, int)>(),
                (folder, _, localMatches) =>
                {
                    foreach (FileEntry file in folder.Files)
                    {
                        // The type check is the cheapest test, so it runs first.
                        if (suffix != null && !file.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (NameMatches(file.Name, words))
                            localMatches.Add((file, words.Length > 0 ? Score(file.Name, words[0]) : 0));
                    }
                    return localMatches;
                },
                localMatches =>
                {
                    lock (matches) matches.AddRange(localMatches);
                });
        }
        finally
        {
            _lock.ExitReadLock();
        }

        // Best first: lower score, then shorter name, then alphabetical.
        int totalMatches = matches.Count;
        List<FileEntry> best = TakeBest(matches, maxResults, CompareFileMatches).Select(m => m.File).ToList();

        stopwatch.Stop();
        return new SearchResults(best, totalMatches, stopwatch.Elapsed);
    }

    private static int CompareFileMatches((FileEntry File, int Score) a, (FileEntry File, int Score) b)
    {
        int result = a.Score.CompareTo(b.Score);
        if (result == 0)
            result = a.File.Name.Length.CompareTo(b.File.Name.Length);
        if (result == 0)
            result = StringComparer.OrdinalIgnoreCase.Compare(a.File.Name, b.File.Name);
        return result;
    }

    /// <summary>True if <paramref name="path"/> is <paramref name="folder"/> itself or anywhere below it.</summary>
    private static bool IsInFolder(string path, string folder)
    {
        if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            return false;
        // "C:\Work" must not include "C:\Workshop". A drive root like "C:\" already ends with "\".
        return path.Length == folder.Length ||
               Path.EndsInDirectorySeparator(folder) ||
               path[folder.Length] == Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// The best <paramref name="count"/> items, best first, without sorting the whole list.
    /// A search for something common (like every .dll) can match millions of files, but only
    /// the best 500 are shown. Keeping those in a small heap while going through the list takes
    /// far less time than sorting all of them.
    /// </summary>
    private static List<T> TakeBest<T>(List<T> items, int count, Comparison<T> compare)
    {
        if (count <= 0)
            return new List<T>();
        if (items.Count <= count)
        {
            items.Sort(compare);
            return items;
        }

        // The heap keeps the best items seen so far, with the worst of them on top,
        // so a new item only has to beat that one to get in.
        var worstFirst = Comparer<T>.Create((a, b) => compare(b, a));
        var heap = new PriorityQueue<T, T>(count + 1, worstFirst);
        foreach (T item in items)
        {
            if (heap.Count < count)
                heap.Enqueue(item, item);
            else if (compare(item, heap.Peek()) < 0)
                heap.DequeueEnqueue(item, item);
        }

        var best = new List<T>(count);
        while (heap.TryDequeue(out T? item, out _))
            best.Add(item);
        best.Reverse(); // the heap gives the worst first
        return best;
    }

    // ------------------------------------------------------------------
    // Finding folders (for the "Folder" picker)
    // ------------------------------------------------------------------

    /// <summary>
    /// Finds folders by name, for the "Folder" picker: "invoices 2026" finds "C:\Work\Invoices-2026".
    /// If the query looks like a path ("C:\Users\Ay" or "Users\Ayesha"), folders whose path
    /// starts with or contains it are found instead, so a path can be typed or pasted.
    /// </summary>
    public List<FolderMatch> SearchFolders(string query, int maxResults = 100)
    {
        query = query.Trim().Replace('/', '\\');
        if (query.Length == 0)
            return new List<FolderMatch>();

        bool byPath = query.Contains('\\') || query.Contains(':');
        string[] words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = new List<(FolderMatch Folder, int Score)>();

        _lock.EnterReadLock();
        try
        {
            FolderNode[] folders = _folders.Values.ToArray();

            Parallel.ForEach(
                folders,
                () => new List<(FolderMatch, int)>(),
                (folder, _, localMatches) =>
                {
                    int score = byPath ? PathScore(folder.Path, query) : FolderNameScore(folder.Path, words);
                    if (score >= 0)
                        localMatches.Add((new FolderMatch(folder.Path, folder.Files.Count), score));
                    return localMatches;
                },
                localMatches =>
                {
                    lock (matches) matches.AddRange(localMatches);
                });
        }
        finally
        {
            _lock.ExitReadLock();
        }

        // Best first: lower score, then shorter path (folders nearer the top), then alphabetical.
        return TakeBest(matches, maxResults, (a, b) =>
            {
                int result = a.Score.CompareTo(b.Score);
                if (result == 0)
                    result = a.Folder.Path.Length.CompareTo(b.Folder.Path.Length);
                if (result == 0)
                    result = StringComparer.OrdinalIgnoreCase.Compare(a.Folder.Path, b.Folder.Path);
                return result;
            })
            .Select(m => m.Folder)
            .ToList();
    }

    /// <summary>0 = path starts with the query, 1 = contains it, -1 = no match.</summary>
    private static int PathScore(string path, string query)
    {
        int position = path.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        return position < 0 ? -1 : position == 0 ? 0 : 1;
    }

    /// <summary>Same ranking as for file names, on the folder's own name; -1 = no match.</summary>
    private static int FolderNameScore(string path, string[] words)
    {
        string name = Path.GetFileName(path);
        if (name.Length == 0)
            name = path; // a drive root such as "C:\"
        return NameMatches(name, words) ? Score(name, words[0]) : -1;
    }

    /// <summary>
    /// How many files of each type are in the index, for example { "pdf": 812, "png": 5320 }.
    /// The extension is without the dot; files without an extension are not counted.
    /// </summary>
    public Dictionary<string, int> CountExtensions()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        _lock.EnterReadLock();
        try
        {
            foreach (FolderNode folder in _folders.Values)
            {
                foreach (FileEntry file in folder.Files)
                {
                    string extension = Path.GetExtension(file.Name);
                    if (extension.Length > 1)
                    {
                        string key = extension[1..];
                        counts[key] = counts.GetValueOrDefault(key) + 1;
                    }
                }
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }

        return counts;
    }

    private static bool NameMatches(string name, string[] words)
    {
        foreach (string word in words)
        {
            if (!name.Contains(word, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Ranking score for a matching name (lower is better):
    /// 0 = exact name, 1 = name starts with the word, 2 = a word inside the name starts with it, 3 = anywhere else.
    /// </summary>
    private static int Score(string name, string firstWord)
    {
        if (name.Equals(firstWord, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileNameWithoutExtension(name).Equals(firstWord, StringComparison.OrdinalIgnoreCase))
            return 0;

        int position = name.IndexOf(firstWord, StringComparison.OrdinalIgnoreCase);
        if (position == 0)
            return 1;
        if (position > 0 && !char.IsLetterOrDigit(name[position - 1]))
            return 2;
        return 3;
    }

    // ------------------------------------------------------------------
    // Keeping the index up to date
    // ------------------------------------------------------------------

    /// <summary>Adds a file, or updates its size and date if it is already in the index.</summary>
    public void AddOrUpdateFile(string fullPath, long size, DateTime modified)
    {
        string? folderPath = Path.GetDirectoryName(fullPath);
        string name = Path.GetFileName(fullPath);
        if (folderPath == null || name.Length == 0)
            return;

        _lock.EnterWriteLock();
        try
        {
            if (!_folders.TryGetValue(folderPath, out FolderNode? folder))
            {
                folder = new FolderNode(folderPath);
                _folders[folderPath] = folder;
            }

            var entry = new FileEntry(name, folder, size, modified);
            int existing = folder.Files.FindIndex(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
                folder.Files[existing] = entry;
            else
                folder.Files.Add(entry);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>Adds (or replaces) whole folders, for example after a new folder was copied in.</summary>
    public void AddFolders(IEnumerable<FolderNode> folders)
    {
        _lock.EnterWriteLock();
        try
        {
            foreach (var folder in folders)
                _folders[folder.Path] = folder;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>Removes a file, or a folder together with everything below it.</summary>
    public void RemovePath(string fullPath)
    {
        fullPath = Path.TrimEndingDirectorySeparator(fullPath);

        _lock.EnterWriteLock();
        try
        {
            if (_folders.ContainsKey(fullPath))
            {
                // It was a folder: remove it and all of its sub-folders.
                string prefix = fullPath + Path.DirectorySeparatorChar;
                var toRemove = _folders.Keys
                    .Where(p => p.Equals(fullPath, StringComparison.OrdinalIgnoreCase) ||
                                p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                foreach (string path in toRemove)
                    _folders.Remove(path);
            }
            else
            {
                // It was a file: remove it from its folder.
                string? folderPath = Path.GetDirectoryName(fullPath);
                string name = Path.GetFileName(fullPath);
                if (folderPath != null && _folders.TryGetValue(folderPath, out FolderNode? folder))
                    folder.Files.RemoveAll(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>Replaces everything in this index with the contents of a freshly scanned one.</summary>
    public void ReplaceWith(FileIndex other)
    {
        _lock.EnterWriteLock();
        try
        {
            _folders = other._folders;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// A copy of the folder list that can be read without holding the lock
    /// (used when saving, so saving does not block searches or the file watcher).
    /// </summary>
    public List<(string Path, FileEntry[] Files)> Snapshot()
    {
        _lock.EnterReadLock();
        try
        {
            return _folders.Values.Select(f => (f.Path, f.Files.ToArray())).ToList();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }
}
