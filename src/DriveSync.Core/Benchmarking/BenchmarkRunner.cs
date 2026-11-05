using System.Data.OleDb;
using System.Diagnostics;
using DriveSync.Core.Indexing;

namespace DriveSync.Core.Benchmarking;

/// <summary>One collection size on the Benchmarks page.</summary>
public sealed class BenchmarkPoint
{
    public int Files { get; set; }
    public double IndexedMs { get; set; }
    public double ScanMs { get; set; }
    public double? WindowsSearchMs { get; set; }
    public double BuildMs { get; set; }
    public int Tokens { get; set; }
}

public sealed class BenchmarkResult
{
    public List<BenchmarkPoint> Points { get; set; } = new();
    public List<string> Queries { get; set; } = new();
    public DateTime At { get; set; }
    public int RunsPerSize { get; set; }
    public bool WindowsSearchAvailable { get; set; }
    public string? WindowsSearchNote { get; set; }
}

/// <summary>
/// Measures, on this PC and with the user's real files, how long a name lookup takes in DriveSync's index
/// compared with walking the folders on disk, for growing collections (2k, 10k, 50k, 100k files).
/// Where Windows Search answers through its SystemIndex OLE DB provider, it is timed too.
/// </summary>
public static class BenchmarkRunner
{
    public static readonly int[] Sizes = { 2_000, 10_000, 50_000, 100_000 };

    public static BenchmarkResult Run(IReadOnlyList<string> roots, IProgress<(double Fraction, string Label)> progress, CancellationToken ct)
    {
        var result = new BenchmarkResult { At = DateTime.Now };
        var walkOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
        };

        // 1. A real collection: the first 100,000 files found under the indexed folders.
        progress.Report((0.01, "Collecting files from your indexed folders…"));
        var paths = new List<string>(Sizes[^1]);
        foreach (string root in roots.Where(Directory.Exists))
        {
            foreach (string p in Directory.EnumerateFiles(root, "*", walkOptions))
            {
                paths.Add(p);
                if (paths.Count >= Sizes[^1] || ct.IsCancellationRequested) break;
            }
            if (paths.Count >= Sizes[^1]) break;
        }
        ct.ThrowIfCancellationRequested();
        var sizes = Sizes.Where(s => s <= paths.Count).ToList();
        if (sizes.Count == 0 && paths.Count > 0) sizes.Add(paths.Count);
        if (sizes.Count == 0) return result;

        // 2. Queries: real words from the collection, from common to rare, plus a two-word query.
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string p in paths.Take(sizes[0]))
            foreach (string t in Tokenizer.Tokens(Path.GetFileNameWithoutExtension(p)).Distinct())
                if (t.Length >= 4 && t.All(char.IsLetter)) counts[t] = counts.GetValueOrDefault(t) + 1;
        var ranked = counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Select(kv => kv.Key).ToList();
        var queries = new List<string>();
        foreach (double at in new[] { 0.0, 0.02, 0.08, 0.2, 0.45 })
            if (ranked.Count > 0) queries.Add(ranked[Math.Min(ranked.Count - 1, (int)(at * ranked.Count))]);
        if (ranked.Count > 3) queries.Add(ranked[0] + " " + ranked[Math.Min(ranked.Count - 1, 3)]);
        queries = queries.Distinct().ToList();
        if (queries.Count == 0) queries.Add("a");
        result.Queries = queries;
        result.RunsPerSize = queries.Count * 5;

        // 3. Windows Search, once (it always searches its whole index for these folders).
        double? windows = MeasureWindowsSearch(roots, queries, out string? note);
        result.WindowsSearchAvailable = windows != null;
        result.WindowsSearchNote = note;

        for (int si = 0; si < sizes.Count; si++)
        {
            int n = sizes[si];
            double baseFraction = 0.05 + 0.9 * si / sizes.Count;
            double span = 0.9 / sizes.Count;
            progress.Report((baseFraction, $"Building a {n:N0}-file index…"));

            // Index build.
            var dirs = new Dictionary<string, ScanDir>(StringComparer.OrdinalIgnoreCase);
            long now = DateTime.Now.Ticks;
            for (int i = 0; i < n; i++)
            {
                string dir = Path.GetDirectoryName(paths[i])!;
                if (!dirs.TryGetValue(dir, out ScanDir? sd)) dirs[dir] = sd = new ScanDir(dir, 1);
                sd.Files.Add(new ScanFile { Name = Path.GetFileName(paths[i]), Modified = now, Created = now, Indexed = now });
            }
            AddParents(dirs);
            var buildClock = Stopwatch.StartNew();
            IndexEngine engine = IndexEngine.Build(dirs.Values.ToList(), new Dictionary<int, string>());
            double build = buildClock.Elapsed.TotalMilliseconds;
            ct.ThrowIfCancellationRequested();

            // Indexed lookups: average over many repeats, median over runs.
            progress.Report((baseFraction + span * 0.25, $"Timing {queries.Count} queries in the index ({n:N0} files)…"));
            var indexed = new List<double>();
            int repeats = n <= 10_000 ? 200 : 40;
            foreach (string q in queries)
            {
                var request = new SearchRequest { Query = q, Take = 100 };
                engine.Search(request);
                for (int run = 0; run < 5; run++)
                {
                    var clock = Stopwatch.StartNew();
                    for (int k = 0; k < repeats; k++) engine.Search(request);
                    indexed.Add(clock.Elapsed.TotalMilliseconds / repeats);
                }
            }

            // Full directory scan: walk the folders on disk until n files are seen, matching names.
            progress.Report((baseFraction + span * 0.5, $"Scanning {n:N0} files on disk for each query…"));
            var scans = new List<double>();
            int scanRuns = n <= 10_000 ? 3 : 1;
            for (int qi = 0; qi < queries.Count; qi++)
            {
                string[] terms = Tokenizer.Tokens(queries[qi]).ToArray();
                for (int run = 0; run < scanRuns; run++)
                {
                    ct.ThrowIfCancellationRequested();
                    var clock = Stopwatch.StartNew();
                    int seen = 0, hits = 0;
                    foreach (string root in roots.Where(Directory.Exists))
                    {
                        foreach (string p in Directory.EnumerateFiles(root, "*", walkOptions))
                        {
                            if (terms.All(t => p.Contains(t, StringComparison.OrdinalIgnoreCase))) hits++;
                            if (++seen >= n) break;
                        }
                        if (seen >= n) break;
                    }
                    scans.Add(clock.Elapsed.TotalMilliseconds);
                }
                progress.Report((baseFraction + span * (0.5 + 0.5 * (qi + 1) / queries.Count), $"Scanning {n:N0} files on disk… query {qi + 1} of {queries.Count}"));
            }

            result.Points.Add(new BenchmarkPoint
            {
                Files = n,
                IndexedMs = Math.Max(0.0005, Median(indexed)),
                ScanMs = Math.Max(0.001, Median(scans)),
                WindowsSearchMs = windows,
                BuildMs = build,
                Tokens = engine.DistinctTokens,
            });
        }
        progress.Report((1, "Done"));
        return result;
    }

    /// <summary>Adds the parent folders between the collected folders so the index has a proper tree.</summary>
    private static void AddParents(Dictionary<string, ScanDir> dirs)
    {
        foreach (string dir in dirs.Keys.ToList())
        {
            string? parent = Path.GetDirectoryName(dir);
            while (parent != null && !dirs.ContainsKey(parent))
            {
                dirs[parent] = new ScanDir(parent, 1);
                parent = Path.GetDirectoryName(parent);
            }
        }
    }

    /// <summary>Times the same queries against Windows Search, scoped to the indexed folders. Null if unavailable.</summary>
    private static double? MeasureWindowsSearch(IReadOnlyList<string> roots, List<string> queries, out string? note)
    {
        note = null;
        if (!OperatingSystem.IsWindows())
        {
            note = "Windows Search is only available on Windows.";
            return null;
        }
        try
        {
            using var connection = new OleDbConnection("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
            connection.Open();
            string scope = string.Join(" OR ", roots.Select(r => $"SCOPE='file:{r.Replace('\\', '/').Replace("'", "''")}'"));
            var times = new List<double>();
            foreach (string q in queries)
            {
                string where = string.Join(" AND ", Tokenizer.Tokens(q).Select(t => $"System.FileName LIKE '%{t.Replace("'", "''")}%'"));
                string sql = $"SELECT TOP 100 System.ItemPathDisplay FROM SystemIndex WHERE ({scope}) AND {where}";
                for (int run = 0; run < 3; run++)
                {
                    var clock = Stopwatch.StartNew();
                    using var cmd = new OleDbCommand(sql, connection);
                    using OleDbDataReader reader = cmd.ExecuteReader();
                    while (reader.Read()) { }
                    times.Add(clock.Elapsed.TotalMilliseconds);
                }
            }
            return Median(times);
        }
        catch (Exception ex)
        {
            note = "Windows Search didn't answer (" + ex.Message.Split('\n')[0].Trim() + ").";
            return null;
        }
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        int m = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[m] : (sorted[m - 1] + sorted[m]) / 2;
    }
}
