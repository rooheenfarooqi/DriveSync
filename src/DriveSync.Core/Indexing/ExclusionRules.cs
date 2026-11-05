namespace DriveSync.Core.Indexing;

/// <summary>
/// The Exclusions list from Indexed folders. Three kinds of rule:
/// a folder name ("bin", "node_modules"), an extension ("*.tmp") or a full path ("C:\Users\Me\Downloads\Old").
/// </summary>
public sealed class ExclusionRules
{
    public static readonly string[] Defaults = { "bin", "obj", ".git", "node_modules", "*.tmp" };

    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _suffixes = new();   // ".tmp"
    private readonly List<string> _paths = new();      // "c:\users\me\old"

    public ExclusionRules(IEnumerable<string> patterns)
    {
        Patterns = patterns.ToList();
        foreach (string p in Patterns)
        {
            switch (Classify(p))
            {
                case RuleKind.Extension: _suffixes.Add(p.TrimStart('*')); break;
                case RuleKind.Path: _paths.Add(Path.TrimEndingDirectorySeparator(p)); break;
                default: _names.Add(p); break;
            }
        }
    }

    public static readonly ExclusionRules None = new(Array.Empty<string>());

    public IReadOnlyList<string> Patterns { get; }

    public enum RuleKind { FolderName, Extension, Path }

    public static RuleKind Classify(string pattern) =>
        pattern.StartsWith("*.", StringComparison.Ordinal) ? RuleKind.Extension :
        pattern.Contains('\\') || pattern.Contains(':') ? RuleKind.Path :
        RuleKind.FolderName;

    /// <summary>True if this folder (and so everything below it) is skipped.</summary>
    public bool IsExcludedFolder(string fullPath, ReadOnlySpan<char> name)
    {
        if (_names.Count > 0 && _names.Contains(name.ToString()))
            return true;
        foreach (string p in _paths)
            if (IsSameOrInside(fullPath, p))
                return true;
        return false;
    }

    public bool IsExcludedFile(string name)
    {
        foreach (string suffix in _suffixes)
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>True if <paramref name="path"/> lies in an excluded folder or matches an excluded extension.</summary>
    public bool IsExcludedPath(string path, bool isFolder)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(path);
        foreach (string p in _paths)
            if (IsSameOrInside(trimmed, p))
                return true;
        if (_names.Count > 0)
        {
            string[] segments = trimmed.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            int folderSegments = isFolder ? segments.Length : segments.Length - 1;
            for (int i = 1; i < folderSegments; i++)
                if (_names.Contains(segments[i]))
                    return true;
        }
        return !isFolder && IsExcludedFile(Path.GetFileName(trimmed));
    }

    public static bool IsSameOrInside(string path, string folder) =>
        path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) &&
        (path.Length == folder.Length || Path.EndsInDirectorySeparator(folder) || path[folder.Length] == '\\');

    /// <summary>Checks a new rule. Returns an error message, or null if it can be added.</summary>
    public static string? Validate(string pattern, IEnumerable<string> existing)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return "Type a folder name, an extension like *.log, or a full path.";
        if (existing.Any(x => x.Equals(pattern, StringComparison.OrdinalIgnoreCase)))
            return $"“{pattern}” is already excluded.";
        if (pattern is "*" or "*." || pattern.StartsWith("*.", StringComparison.Ordinal) && pattern.Length == 2)
            return "Add an extension after *. — for example *.log.";
        if (pattern.StartsWith('*') && !pattern.StartsWith("*.", StringComparison.Ordinal))
            return "Use *. followed by an extension, for example *.log.";
        if (Classify(pattern) == RuleKind.Path && !Path.IsPathFullyQualified(pattern))
            return "Use a full path that starts with a drive letter, like C:\\Users\\You\\Old.";
        if (pattern.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || (Classify(pattern) == RuleKind.FolderName && pattern.IndexOfAny(new[] { '/', '*', '?', '"', '<', '>', '|' }) >= 0))
            return "That name contains characters Windows doesn't allow in folder names.";
        return null;
    }
}
