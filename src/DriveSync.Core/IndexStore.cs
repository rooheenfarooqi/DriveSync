using Microsoft.Data.Sqlite;

namespace DriveSync.Core;

/// <summary>
/// Saves the index to an SQLite database file and loads it back.
///
/// Scanning every drive takes a while, but loading a saved index takes only a moment.
/// So when DriveSync starts, it loads the saved index and you can search straight away,
/// while a fresh scan runs quietly in the background.
///
/// Tables:
///   folders(id, path)
///   files(folder_id, name, size, modified)
/// </summary>
public sealed class IndexStore
{
    private readonly string _databasePath;

    public IndexStore(string databasePath)
    {
        _databasePath = databasePath;
    }

    /// <summary>%LOCALAPPDATA%\DriveSync\index.db</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriveSync", "index.db");

    public bool Exists => File.Exists(_databasePath);

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        return connection;
    }

    public void Save(FileIndex index)
    {
        var folders = index.Snapshot();

        using var connection = Open();
        Execute(connection, "PRAGMA journal_mode = WAL; PRAGMA synchronous = OFF;");

        // One transaction for everything: much faster, and if the app crashes
        // half way, the previous saved index stays intact.
        using var transaction = connection.BeginTransaction();
        Execute(connection, """
            DROP TABLE IF EXISTS files;
            DROP TABLE IF EXISTS folders;
            CREATE TABLE folders (id INTEGER PRIMARY KEY, path TEXT NOT NULL);
            CREATE TABLE files (folder_id INTEGER NOT NULL, name TEXT NOT NULL, size INTEGER NOT NULL, modified INTEGER NOT NULL);
            """);

        // Prepared commands are re-used for every row, which keeps inserts fast.
        using var insertFolder = connection.CreateCommand();
        insertFolder.CommandText = "INSERT INTO folders (id, path) VALUES ($id, $path)";
        var folderId = insertFolder.Parameters.Add("$id", SqliteType.Integer);
        var folderPath = insertFolder.Parameters.Add("$path", SqliteType.Text);

        using var insertFile = connection.CreateCommand();
        insertFile.CommandText = "INSERT INTO files (folder_id, name, size, modified) VALUES ($folder, $name, $size, $modified)";
        var fileFolder = insertFile.Parameters.Add("$folder", SqliteType.Integer);
        var fileName = insertFile.Parameters.Add("$name", SqliteType.Text);
        var fileSize = insertFile.Parameters.Add("$size", SqliteType.Integer);
        var fileModified = insertFile.Parameters.Add("$modified", SqliteType.Integer);

        long id = 0;
        foreach (var (path, files) in folders)
        {
            id++;
            folderId.Value = id;
            folderPath.Value = path;
            insertFolder.ExecuteNonQuery();

            foreach (FileEntry file in files)
            {
                fileFolder.Value = id;
                fileName.Value = file.Name;
                fileSize.Value = file.Size;
                fileModified.Value = file.Modified.Ticks;
                insertFile.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    /// <summary>Loads the saved index, or returns null if there is none (or it can't be read).</summary>
    public FileIndex? Load()
    {
        if (!Exists)
            return null;

        try
        {
            using var connection = Open();
            var foldersById = new Dictionary<long, FolderNode>();

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT id, path FROM folders";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    foldersById[reader.GetInt64(0)] = new FolderNode(reader.GetString(1));
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT folder_id, name, size, modified FROM files";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (foldersById.TryGetValue(reader.GetInt64(0), out FolderNode? folder))
                        folder.Files.Add(new FileEntry(reader.GetString(1), folder, reader.GetInt64(2), new DateTime(reader.GetInt64(3))));
                }
            }

            return FileIndex.FromFolders(foldersById.Values);
        }
        catch (SqliteException)
        {
            return null; // damaged or old-format file: a fresh scan will replace it
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
