using System.Collections.Concurrent;
using System.Diagnostics;

namespace DriveSync.Core.Indexing;

/// <summary>
/// Walks folders on disk and collects every file name.
///
/// Several worker threads share one stack of "folders still to read". Each worker takes a folder,
/// reads it and pushes its sub-folders back, so all workers stay busy even when one folder
/// (like C:\Windows) is much bigger than the others.
/// </summary>
public static class FileScanner
{
    /// <summary>The root of every ready fixed or removable drive, for example "C:\" and "D:\".</summary>
    public static List<string> GetAllDriveRoots()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable))
                .Select(d => d.RootDirectory.FullName)
                .ToList();
        }
        catch (IOException)
        {
            return new List<string> { Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\" };
        }
    }

    /// <summary>
    /// Scans the given roots and everything below them (where each root allows it).
    /// </summary>
    /// <param name="skipFolder">Extra folders to skip, such as DriveSync's own data folder.</param>
    public static List<ScanDir> Scan(
        IReadOnlyList<ScanRoot> roots,
        ExclusionRules exclusions,
        bool includeHidden,
        Action<ScanProgress>? onProgress = null,
        CancellationToken cancel = default,
        Func<string, bool>? skipFolder = null)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            // Reparse points are junctions/shortcuts to other folders; following them can loop forever.
            AttributesToSkip = includeHidden ? FileAttributes.ReparsePoint : FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
        };

        var work = new ConcurrentStack<(string Path, int Root)>();
        for (int r = roots.Count - 1; r >= 0; r--)
            if (Directory.Exists(roots[r].Path))
                work.Push((roots[r].Path, r));

        var results = new ConcurrentBag<ScanDir>();
        int foldersRead = 0, busy = 0;
        long filesFound = 0;
        long lastReport = 0;
        var clock = Stopwatch.StartNew();
        long now = DateTime.Now.Ticks;

        void Worker()
        {
            var subFolders = new List<string>();
            while (!cancel.IsCancellationRequested)
            {
                Interlocked.Increment(ref busy);
                if (work.TryPop(out var item))
                {
                    ScanRoot root = roots[item.Root];
                    subFolders.Clear();
                    ScanDir dir = ReadFolder(item.Path, root, options, exclusions, includeHidden, now, subFolders, skipFolder);
                    results.Add(dir);
                    Interlocked.Add(ref filesFound, dir.Files.Count);
                    if (root.IncludeSubfolders)
                        foreach (string sub in subFolders)
                            work.Push((sub, item.Root));
                    Interlocked.Decrement(ref busy);

                    int count = Interlocked.Increment(ref foldersRead);
                    long ms = clock.ElapsedMilliseconds;
                    long last = Interlocked.Read(ref lastReport);
                    if (onProgress != null && ms - last >= 120 && Interlocked.CompareExchange(ref lastReport, ms, last) == last)
                        onProgress(new ScanProgress(count, Interlocked.Read(ref filesFound), work.Count));
                }
                else
                {
                    Interlocked.Decrement(ref busy);
                    if (Volatile.Read(ref busy) == 0 && work.IsEmpty)
                        return;
                    Thread.Sleep(1);
                }
            }
        }

        int workerCount = Math.Max(2, Environment.ProcessorCount);
        var workers = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Factory.StartNew(Worker, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default))
            .ToArray();
        Task.WaitAll(workers);

        onProgress?.Invoke(new ScanProgress(foldersRead, filesFound, 0));
        return results.ToList();
    }

    private static ScanDir ReadFolder(string folderPath, ScanRoot root, EnumerationOptions options, ExclusionRules exclusions,
                                      bool includeHidden, long now, List<string> subFolders, Func<string, bool>? skipFolder)
    {
        var dir = new ScanDir(Path.TrimEndingDirectorySeparator(folderPath), root.RootId);
        try
        {
            // EnumerateFileSystemInfos gets name, size and dates in one go from the directory listing,
            // without opening each file.
            foreach (FileSystemInfo item in new DirectoryInfo(folderPath).EnumerateFileSystemInfos("*", options))
            {
                if (item is FileInfo file)
                {
                    string name = file.Name;
                    if (!includeHidden && name.StartsWith("~$", StringComparison.Ordinal))
                        continue; // Office lock files
                    if (exclusions.IsExcludedFile(name))
                        continue;
                    if (root.Types != null && !root.Types.Contains(FileKinds.GroupOf(FileKinds.FromName(name))))
                        continue;

                    FileAttributes attributes = file.Attributes;
                    dir.Files.Add(new ScanFile
                    {
                        Name = name,
                        Size = file.Length,
                        Modified = file.LastWriteTime.Ticks,
                        Created = file.CreationTime.Ticks,
                        Indexed = now,
                        Flags = (attributes.HasFlag(FileAttributes.Hidden) ? FileFlags.Hidden : 0) |
                                (attributes.HasFlag(FileAttributes.System) ? FileFlags.System : 0),
                    });
                }
                else
                {
                    string full = item.FullName;
                    if (exclusions.IsExcludedFolder(full, item.Name))
                        continue;
                    if (skipFolder != null && skipFolder(full))
                        continue;
                    subFolders.Add(full);
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // The folder vanished or is locked while we were reading it. Keep what we got.
        }
        return dir;
    }
}
