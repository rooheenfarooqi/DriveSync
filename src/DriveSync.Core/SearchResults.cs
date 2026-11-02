namespace DriveSync.Core;

/// <summary>What a search returns: the best matches plus some numbers to show the user.</summary>
/// <param name="Items">The best matches, best first (at most the requested maximum).</param>
/// <param name="TotalMatches">How many files matched in total.</param>
/// <param name="Elapsed">How long the search took. Useful for benchmarking against Windows search.</param>
public sealed record SearchResults(IReadOnlyList<FileEntry> Items, int TotalMatches, TimeSpan Elapsed)
{
    public static readonly SearchResults Empty = new(Array.Empty<FileEntry>(), 0, TimeSpan.Zero);
}
