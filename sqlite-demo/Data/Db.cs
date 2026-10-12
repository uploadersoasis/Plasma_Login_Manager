using Microsoft.Data.Sqlite;

namespace SqliteConsole.Data;

/// <summary>
/// Owns the SQLite connection string and exposes helpers to open connections.
/// Every command executed against the database goes through here, so the
/// journal mode / foreign-key settings are always consistent.
/// </summary>
public sealed class Db
{
    public string DbPath { get; }

    private readonly string _connectionString;

    public Db(string dbPath)
    {
        DbPath = Path.GetFullPath(dbPath);
        // Microsoft.Data.Sqlite builds the SQLite file automatically on first open,
        // but the containing directory must exist.
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,          // enforce FK constraints (off by default in SQLite!)
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        };
        _connectionString = builder.ToString();
    }

    /// <summary>Opens a connection and applies per-connection pragmas.</summary>
    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        // WAL gives concurrent readers + one writer and is much faster on Linux.
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        cmd.ExecuteNonQuery();

        return conn;
    }

    /// <summary>True if the file exists and looks like a SQLite database.</summary>
    public bool Exists()
    {
        if (!File.Exists(DbPath)) return false;
        try
        {
            using var conn = Open();
            using var check = conn.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM sqlite_master;";
            check.ExecuteScalar();
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    public void Delete()
    {
        SqliteConnection.ClearAllPools();   // otherwise -wal/-shm files stay locked
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var f = DbPath + suffix;
            if (File.Exists(f)) File.Delete(f);
        }
    }
}
