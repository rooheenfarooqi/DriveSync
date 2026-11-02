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
    public SearchResults Search(string query, int maxResults = 500, string? extension = null)
    {
        string[] words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? suffix = string.IsNullOrEmpty(extension) ? null : "." + extension;
        if (words.Length == 0 && suffix == null)
            return SearchResults.Empty;

        var stopwatch = Stopwatch.StartNew();
        var matches = new List<(FileEntry File, int Score)>();

        _lock.EnterReadLock();
        try
        {
            FolderNode[] folders = _folders.Values.ToArray();

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
        List<FileEntry> best = matches
            .OrderBy(m => m.Score)
            .ThenBy(m => m.File.Name.Length)
            .ThenBy(m => m.File.Name, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .Select(m => m.File)
            .ToList();

        stopwatch.Stop();
        return new SearchResults(best, matches.Count, stopwatch.Elapsed);
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
