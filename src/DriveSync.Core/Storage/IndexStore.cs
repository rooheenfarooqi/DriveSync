using System.Text.Json;
using DriveSync.Core.Indexing;
using Microsoft.Data.Sqlite;

namespace DriveSync.Core.Storage;

/// <summary>An indexed folder as saved in the database.</summary>
public sealed class RootRecord
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Paused { get; set; }
    public bool IncludeSubfolders { get; set; } = true;

    /// <summary>Type groups to index, e.g. "Doc,Pdf"; empty means every type.</summary>
    public string Types { get; set; } = "";
    public long Added { get; set; }
    public long LastScan { get; set; }
}

/// <summary>One saved Live activity event.</summary>
public sealed record EventRecord(long At, string Kind, string Path, string? OldPath, long Size, int RootId);

/// <summary>
/// The SQLite database at %LOCALAPPDATA%\DriveSync\index.db: indexed folders, folders, files, settings,
/// exclusions, search history and activity events.
///
/// After a full scan the file table is rewritten in one transaction. Between scans, watcher changes are
/// written in batches every few seconds (<see cref="ApplyDelta"/>), so the index survives restarts and
/// start-up loads it instead of scanning again.
/// </summary>
public sealed class IndexStore
{
    private const int SchemaVersion = 3;
    private readonly string _path;
    private readonly object _writeLock = new();

    public IndexStore(string path)
    {
        _path = path;
    }

    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriveSync", "index.db");

    public string FilePath => _path;

    /// <summary>Size of the database files on disk, in bytes.</summary>
    public long SizeOnDisk
    {
        get
        {
            long total = 0;
            foreach (string suffix in new[] { "", "-wal", "-shm" })
            {
                var info = new FileInfo(_path + suffix);
                if (info.Exists) total += info.Length;
            }
            return total;
        }
    }

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        var connection = new SqliteConnection($"Data Source={_path};Cache=Shared");
        connection.Open();
        Execute(connection, "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA temp_store = MEMORY;");
        return connection;
    }

    /// <summary>Creates the tables. Returns true if an index from an older DriveSync version was found and replaced.</summary>
    public bool EnsureSchema()
    {
        lock (_writeLock)
        {
            using var c = Open();
            bool hadOld = false;
            long version = 0;
            if (TableExists(c, "meta"))
                version = Convert.ToInt64(Scalar(c, "SELECT value FROM meta WHERE key='schema'") ?? 0L);
            if (version != SchemaVersion)
            {
                // DriveSync 1 kept folders/files tables; earlier builds of this version kept dirs/files.
                // The index is rebuilt from the folders (first-run setup for DriveSync 1).
                hadOld = TableExists(c, "folders");
                Execute(c, "DROP TABLE IF EXISTS files; DROP TABLE IF EXISTS folders; DROP TABLE IF EXISTS dirs; DROP TABLE IF EXISTS snapshot; DROP TABLE IF EXISTS journal;");
            }
            Execute(c, """
                CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);
                CREATE TABLE IF NOT EXISTS roots (id INTEGER PRIMARY KEY, name TEXT NOT NULL, path TEXT NOT NULL, paused INTEGER NOT NULL,
                    subfolders INTEGER NOT NULL, types TEXT NOT NULL, added INTEGER NOT NULL, last_scan INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS snapshot (part INTEGER PRIMARY KEY, data BLOB NOT NULL);
                CREATE TABLE IF NOT EXISTS journal (seq INTEGER PRIMARY KEY AUTOINCREMENT, op INTEGER NOT NULL, folder TEXT NOT NULL,
                    root_id INTEGER NOT NULL, name TEXT, size INTEGER, modified INTEGER, created INTEGER, indexed INTEGER, flags INTEGER);
                CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS exclusions (pattern TEXT PRIMARY KEY, position INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS history (query TEXT PRIMARY KEY, at INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS events (id INTEGER PRIMARY KEY AUTOINCREMENT, at INTEGER NOT NULL, kind TEXT NOT NULL,
                    path TEXT NOT NULL, old_path TEXT, size INTEGER NOT NULL, root_id INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS events_by_time ON events (at);
                """);
            SetMeta(c, "schema", SchemaVersion.ToString());
            return hadOld;
        }
    }

    // ------------------------------------------------------------------
    // Index: snapshot (full save and load) and journal (batched changes)
    // ------------------------------------------------------------------
    //
    // The index is saved as a compact binary snapshot, split into 8 MB blobs in the snapshot table.
    // Watcher changes between snapshots go into the journal table in batches every few seconds; on
    // start-up the snapshot is read and the journal replayed. A 1.3-million-file index reads back in
    // seconds, instead of tens of seconds for one row per file.

    private const int ChunkSize = 8 * 1024 * 1024;
    private const int Magic = 0x33495344; // "DSI3"

    /// <summary>Replaces the saved index with this snapshot and clears the journal (one transaction).</summary>
    public void SaveIndex(List<ScanDir> dirs, Action<double>? progress = null)
    {
        lock (_writeLock)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            Execute(c, "DELETE FROM snapshot; DELETE FROM journal;", tx);
            using var insert = Prepare(c, tx, "INSERT INTO snapshot (part, data) VALUES ($p, $d)", "$p", "$d");

            var buffer = new MemoryStream(ChunkSize + 64 * 1024);
            var w = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true);
            int part = 0;
            void Flush()
            {
                w.Flush();
                if (buffer.Length == 0) return;
                insert.Parameters[0].Value = part++;
                insert.Parameters[1].Value = buffer.ToArray();
                insert.ExecuteNonQuery();
                buffer.SetLength(0);
            }

            w.Write(Magic);
            w.Write(dirs.Count);
            int done = 0;
            foreach (ScanDir dir in dirs)
            {
                w.Write(dir.Path);
                w.Write(dir.RootId);
                w.Write(dir.Files.Count);
                foreach (ScanFile f in dir.Files)
                {
                    w.Write(f.Name);
                    w.Write(f.Size);
                    w.Write(f.Modified);
                    w.Write(f.Created);
                    w.Write(f.Indexed);
                    w.Write((byte)f.Flags);
                }
                if (buffer.Length >= ChunkSize) Flush();
                if ((++done & 0x3FFF) == 0)
                    progress?.Invoke(0.95 * done / Math.Max(1, dirs.Count));
            }
            Flush();
            SetMeta(c, "cleared", "0", tx);
            tx.Commit();
            progress?.Invoke(1);
        }
        Checkpoint();
    }

    /// <summary>Loads the saved index (snapshot plus journal), or null if there is none.</summary>
    public List<ScanDir>? LoadIndex()
    {
        lock (_writeLock)
        {
            using var c = Open();
            var buffer = new MemoryStream();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT data FROM snapshot ORDER BY part";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    using Stream blob = r.GetStream(0);
                    blob.CopyTo(buffer);
                }
            }
            if (buffer.Length == 0)
                return null;

            buffer.Position = 0;
            var reader = new BinaryReader(buffer, System.Text.Encoding.UTF8);
            if (reader.ReadInt32() != Magic)
                return null;
            int dirCount = reader.ReadInt32();
            var dirs = new List<ScanDir>(dirCount);
            for (int d = 0; d < dirCount; d++)
            {
                var dir = new ScanDir(reader.ReadString(), reader.ReadInt32());
                int files = reader.ReadInt32();
                dir.Files.Capacity = files;
                for (int i = 0; i < files; i++)
                {
                    dir.Files.Add(new ScanFile
                    {
                        Name = reader.ReadString(),
                        Size = reader.ReadInt64(),
                        Modified = reader.ReadInt64(),
                        Created = reader.ReadInt64(),
                        Indexed = reader.ReadInt64(),
                        Flags = (FileFlags)reader.ReadByte(),
                    });
                }
                dirs.Add(dir);
            }
            ReplayJournal(c, dirs);
            return dirs;
        }
    }

    private static void ReplayJournal(SqliteConnection c, List<ScanDir> dirs)
    {
        Dictionary<string, ScanDir>? byPath = null;
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT op, folder, root_id, name, size, modified, created, indexed, flags FROM journal ORDER BY seq";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            byPath ??= dirs.ToDictionary(d => d.Path, StringComparer.OrdinalIgnoreCase);
            int op = r.GetInt32(0);
            string folder = r.GetString(1);
            switch (op)
            {
                case 1: // upsert file
                {
                    if (!byPath.TryGetValue(folder, out ScanDir? dir))
                    {
                        dir = new ScanDir(folder, r.GetInt32(2));
                        byPath[folder] = dir;
                        dirs.Add(dir);
                    }
                    string name = r.GetString(3);
                    dir.Files.RemoveAll(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
                    dir.Files.Add(new ScanFile
                    {
                        Name = name, Size = r.GetInt64(4), Modified = r.GetInt64(5), Created = r.GetInt64(6),
                        Indexed = r.GetInt64(7), Flags = (FileFlags)r.GetInt32(8),
                    });
                    break;
                }
                case 2: // delete file
                    if (byPath.TryGetValue(folder, out ScanDir? owner))
                    {
                        string name = r.GetString(3);
                        owner.Files.RemoveAll(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
                    }
                    break;
                case 3: // delete folder and everything below it
                    dirs.RemoveAll(d => ExclusionRules.IsSameOrInside(d.Path, folder));
                    foreach (string key in byPath.Keys.Where(k => ExclusionRules.IsSameOrInside(k, folder)).ToList())
                        byPath.Remove(key);
                    break;
            }
        }
    }

    /// <summary>Deletes the saved index (Clear index).</summary>
    public void ClearIndex()
    {
        lock (_writeLock)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            Execute(c, "DELETE FROM snapshot; DELETE FROM journal;", tx);
            SetMeta(c, "cleared", "1", tx);
            tx.Commit();
        }
        Checkpoint();
        using var v = Open();
        Execute(v, "VACUUM;");
    }

    public abstract record Delta;
    public sealed record UpsertFile(string Folder, int RootId, ScanFile File) : Delta;
    public sealed record DeleteFile(string Folder, string Name) : Delta;
    public sealed record DeleteFolder(string Folder) : Delta;

    /// <summary>Appends a batch of watcher changes to the journal in one transaction.</summary>
    public void ApplyDelta(IReadOnlyList<Delta> batch)
    {
        if (batch.Count == 0) return;
        lock (_writeLock)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            using var insert = Prepare(c, tx,
                "INSERT INTO journal (op, folder, root_id, name, size, modified, created, indexed, flags) VALUES ($o, $f, $r, $n, $s, $m, $c, $i, $g)",
                "$o", "$f", "$r", "$n", "$s", "$m", "$c", "$i", "$g");
            foreach (Delta delta in batch)
            {
                for (int i = 0; i < 9; i++) insert.Parameters[i].Value = DBNull.Value;
                switch (delta)
                {
                    case UpsertFile u:
                        insert.Parameters[0].Value = 1;
                        insert.Parameters[1].Value = u.Folder;
                        insert.Parameters[2].Value = u.RootId;
                        insert.Parameters[3].Value = u.File.Name;
                        insert.Parameters[4].Value = u.File.Size;
                        insert.Parameters[5].Value = u.File.Modified;
                        insert.Parameters[6].Value = u.File.Created;
                        insert.Parameters[7].Value = u.File.Indexed;
                        insert.Parameters[8].Value = (int)u.File.Flags;
                        break;
                    case DeleteFile d:
                        insert.Parameters[0].Value = 2;
                        insert.Parameters[1].Value = d.Folder;
                        insert.Parameters[2].Value = 0;
                        insert.Parameters[3].Value = d.Name;
                        break;
                    case DeleteFolder df:
                        insert.Parameters[0].Value = 3;
                        insert.Parameters[1].Value = df.Folder;
                        insert.Parameters[2].Value = 0;
                        break;
                }
                insert.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    // ------------------------------------------------------------------
    // Indexed folders
    // ------------------------------------------------------------------

    public List<RootRecord> LoadRoots()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, name, path, paused, subfolders, types, added, last_scan FROM roots ORDER BY id";
        using var r = cmd.ExecuteReader();
        var list = new List<RootRecord>();
        while (r.Read())
            list.Add(new RootRecord
            {
                Id = r.GetInt32(0), Name = r.GetString(1), Path = r.GetString(2), Paused = r.GetInt32(3) != 0,
                IncludeSubfolders = r.GetInt32(4) != 0, Types = r.GetString(5), Added = r.GetInt64(6), LastScan = r.GetInt64(7),
            });
        return list;
    }

    public void SaveRoots(IEnumerable<RootRecord> roots)
    {
        lock (_writeLock)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            Execute(c, "DELETE FROM roots;", tx);
            using var cmd = Prepare(c, tx,
                "INSERT INTO roots (id, name, path, paused, subfolders, types, added, last_scan) VALUES ($id, $n, $p, $pa, $s, $t, $a, $l)",
                "$id", "$n", "$p", "$pa", "$s", "$t", "$a", "$l");
            foreach (RootRecord root in roots)
            {
                cmd.Parameters[0].Value = root.Id;
                cmd.Parameters[1].Value = root.Name;
                cmd.Parameters[2].Value = root.Path;
                cmd.Parameters[3].Value = root.Paused ? 1 : 0;
                cmd.Parameters[4].Value = root.IncludeSubfolders ? 1 : 0;
                cmd.Parameters[5].Value = root.Types;
                cmd.Parameters[6].Value = root.Added;
                cmd.Parameters[7].Value = root.LastScan;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    // ------------------------------------------------------------------
    // Settings, exclusions, history, events, meta
    // ------------------------------------------------------------------

    public T? LoadSettings<T>(string key) where T : class
    {
        using var c = Open();
        string? json = Scalar(c, "SELECT value FROM settings WHERE key = $k", ("$k", key)) as string;
        if (json == null) return null;
        try { return JsonSerializer.Deserialize<T>(json); }
        catch (JsonException) { return null; }
    }

    public void SaveSettings<T>(string key, T value)
    {
        lock (_writeLock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", JsonSerializer.Serialize(value));
            cmd.ExecuteNonQuery();
        }
    }

    public List<string>? LoadExclusions()
    {
        using var c = Open();
        if (Scalar(c, "SELECT value FROM meta WHERE key='exclusions_set'") == null)
            return null;
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT pattern FROM exclusions ORDER BY position";
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public void SaveExclusions(IReadOnlyList<string> patterns)
    {
        lock (_writeLock)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            Execute(c, "DELETE FROM exclusions;", tx);
            using var cmd = Prepare(c, tx, "INSERT INTO exclusions (pattern, position) VALUES ($p, $i)", "$p", "$i");
            for (int i = 0; i < patterns.Count; i++)
            {
                cmd.Parameters[0].Value = patterns[i];
                cmd.Parameters[1].Value = i;
                cmd.ExecuteNonQuery();
            }
            SetMeta(c, "exclusions_set", "1", tx);
            tx.Commit();
        }
    }

    public List<string> LoadHistory()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT query FROM history ORDER BY at DESC LIMIT 12";
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public void SaveHistory(IReadOnlyList<string> newestFirst)
    {
        lock (_writeLock)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            Execute(c, "DELETE FROM history;", tx);
            using var cmd = Prepare(c, tx, "INSERT OR REPLACE INTO history (query, at) VALUES ($q, $a)", "$q", "$a");
            long now = DateTime.UtcNow.Ticks;
            for (int i = 0; i < newestFirst.Count; i++)
            {
                cmd.Parameters[0].Value = newestFirst[i];
                cmd.Parameters[1].Value = now - i;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void AddEvents(IReadOnlyList<EventRecord> events)
    {
        if (events.Count == 0) return;
        lock (_writeLock)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            using var cmd = Prepare(c, tx,
                "INSERT INTO events (at, kind, path, old_path, size, root_id) VALUES ($a, $k, $p, $o, $s, $r)",
                "$a", "$k", "$p", "$o", "$s", "$r");
            foreach (EventRecord e in events)
            {
                cmd.Parameters[0].Value = e.At;
                cmd.Parameters[1].Value = e.Kind;
                cmd.Parameters[2].Value = e.Path;
                cmd.Parameters[3].Value = (object?)e.OldPath ?? DBNull.Value;
                cmd.Parameters[4].Value = e.Size;
                cmd.Parameters[5].Value = e.RootId;
                cmd.ExecuteNonQuery();
            }
            // Keep a week of history.
            Execute(c, $"DELETE FROM events WHERE at < {DateTime.Now.AddDays(-7).Ticks};", tx);
            tx.Commit();
        }
    }

    public List<EventRecord> LoadEvents(DateTime since, int max)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT at, kind, path, old_path, size, root_id FROM events WHERE at >= $s ORDER BY at DESC LIMIT $m";
        cmd.Parameters.AddWithValue("$s", since.Ticks);
        cmd.Parameters.AddWithValue("$m", max);
        using var r = cmd.ExecuteReader();
        var list = new List<EventRecord>();
        while (r.Read())
            list.Add(new EventRecord(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt64(4), r.GetInt32(5)));
        return list;
    }

    /// <summary>Event times from the last 24 hours (for the changes chart and "changes today").</summary>
    public List<long> LoadEventTimes(DateTime since)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT at FROM events WHERE at >= $s";
        cmd.Parameters.AddWithValue("$s", since.Ticks);
        using var r = cmd.ExecuteReader();
        var list = new List<long>();
        while (r.Read()) list.Add(r.GetInt64(0));
        return list;
    }

    public string? GetMeta(string key)
    {
        using var c = Open();
        return Scalar(c, "SELECT value FROM meta WHERE key = $k", ("$k", key)) as string;
    }

    public void SetMeta(string key, string value)
    {
        lock (_writeLock)
        {
            using var c = Open();
            SetMeta(c, key, value);
        }
    }

    // ------------------------------------------------------------------

    private void Checkpoint()
    {
        try
        {
            using var c = Open();
            Execute(c, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        catch (SqliteException)
        {
            // Another reader is busy; SQLite checkpoints on its own later.
        }
    }

    private static void SetMeta(SqliteConnection c, string key, string value, SqliteTransaction? tx = null)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO meta (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection c, string name) =>
        Scalar(c, "SELECT name FROM sqlite_master WHERE type='table' AND name=$n", ("$n", name)) != null;

    private static object? Scalar(SqliteConnection c, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        object? result = cmd.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    private static void Execute(SqliteConnection c, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static SqliteCommand Prepare(SqliteConnection c, SqliteTransaction tx, string sql, params string[] names)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (string name in names)
            cmd.Parameters.Add(new SqliteParameter { ParameterName = name });
        cmd.Prepare();
        return cmd;
    }
}
