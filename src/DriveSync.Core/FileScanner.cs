using System.Collections.Concurrent;

namespace DriveSync.Core;

/// <summary>
/// Walks through folders on disk and collects every file name.
///
/// Instead of letting one thread walk the whole drive, several worker threads share
/// a stack of "folders still to read". Each worker takes a folder, reads it, and puts
/// its sub-folders back on the stack for any worker to pick up. This keeps all workers
/// busy even when one folder (like C:\Windows) is much bigger than the others.
/// </summary>
public static class FileScanner
{
    private static readonly EnumerationOptions ReadOneFolderOptions = new()
    {
        RecurseSubdirectories = false,   // we visit sub-folders ourselves, in parallel
        IgnoreInaccessible = true,       // skip folders we have no permission to read
        ReturnSpecialDirectories = false, // skip "." and ".."
        // Reparse points are shortcuts/junctions to other folders; following them can loop forever.
        // System items are Windows internals such as "System Volume Information".
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
    };

    /// <summary>The root of every ready fixed or removable drive, for example "C:\" and "D:\".</summary>
    public static List<string> GetAllDriveRoots()
    {
        return DriveInfo.GetDrives()
            .Where(d => d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable))
            .Select(d => d.RootDirectory.FullName)
            .ToList();
    }

    /// <summary>
    /// Scans the given root folders and everything below them.
    /// </summary>
    /// <param name="roots">Folders to start from, for example all drive roots.</param>
    /// <param name="onProgress">Called now and then with the number of folders read so far.</param>
    /// <param name="cancel">Lets the caller stop the scan early.</param>
    public static List<FolderNode> Scan(IEnumerable<string> roots, Action<int>? onProgress = null, CancellationToken cancel = default)
    {
        var foldersToRead = new ConcurrentStack<string>(roots);
        var results = new ConcurrentBag<FolderNode>();
        int foldersRead = 0;
        int busyWorkers = 0;

        void Worker()
        {
            var subFolders = new List<string>();

            while (!cancel.IsCancellationRequested)
            {
                // Mark ourselves busy *before* taking work, so other workers
                // don't think the scan is finished while we hold a folder.
                Interlocked.Increment(ref busyWorkers);

                if (foldersToRead.TryPop(out string? folderPath))
                {
                    subFolders.Clear();
                    results.Add(ReadFolder(folderPath, subFolders));
                    if (subFolders.Count > 0)
                        foldersToRead.PushRange(subFolders.ToArray());

                    Interlocked.Decrement(ref busyWorkers);

                    int count = Interlocked.Increment(ref foldersRead);
                    if (count % 2000 == 0)
                        onProgress?.Invoke(count);
                }
                else
                {
                    Interlocked.Decrement(ref busyWorkers);

                    // Nothing to take. If no one else is working either, no new
                    // folders can appear, so the scan is complete.
                    if (Volatile.Read(ref busyWorkers) == 0 && foldersToRead.IsEmpty)
                        return;

                    Thread.Sleep(1);
                }
            }
        }

        // Reading folders mostly waits on the disk, so one worker per CPU core works well.
        int workerCount = Math.Max(2, Environment.ProcessorCount);
        var workers = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Factory.StartNew(Worker, TaskCreationOptions.LongRunning))
            .ToArray();
        Task.WaitAll(workers);

        onProgress?.Invoke(foldersRead);
        return results.ToList();
    }

    /// <summary>Reads one folder: its files go into the returned node, its sub-folders into <paramref name="subFolders"/>.</summary>
    private static FolderNode ReadFolder(string folderPath, List<string> subFolders)
    {
        var node = new FolderNode(folderPath);
        try
        {
            // EnumerateFileSystemInfos gets name, size and date in one go from the
            // directory listing, without opening each file.
            foreach (FileSystemInfo item in new DirectoryInfo(folderPath).EnumerateFileSystemInfos("*", ReadOneFolderOptions))
            {
                if (item is FileInfo file)
                    node.Files.Add(new FileEntry(file.Name, node, file.Length, file.LastWriteTime));
                else
                    subFolders.Add(item.FullName);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // The folder vanished or is locked while we were reading it. Keep what we got.
        }
        return node;
    }
}
