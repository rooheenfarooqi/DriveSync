namespace DriveSync.Core;

/// <summary>What a search returns: the best matches plus some numbers to show the user.</summary>
/// <param name="Items">The best matches, best first (at most the requested maximum).</param>
/// <param name="TotalMatches">How many files matched in total.</param>
/// <param name="Elapsed">How long the search took. Useful for benchmarking against Windows search.</param>
public sealed record SearchResults(IReadOnlyList<FileEntry> Items, int TotalMatches, TimeSpan Elapsed)
{
    public static readonly SearchResults Empty = new(Array.Empty<FileEntry>(), 0, TimeSpan.Zero);
}

/// <summary>A folder found by <see cref="FileIndex.SearchFolders"/>.</summary>
/// <param name="Path">Full folder path, for example "C:\Users\Ayesha\Documents".</param>
/// <param name="FileCount">How many files are directly inside it (not counting sub-folders).</param>
public sealed record FolderMatch(string Path, int FileCount);
