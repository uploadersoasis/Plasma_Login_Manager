using Microsoft.Data.Sqlite;

namespace SqliteConsole.Data;

/// <summary>
/// Schema management. Every migration is an idempotent SQL script stored in a
/// hard-coded, ordered list and recorded in the `schema_migrations` table so we
/// know exactly which version the file on disk is at.
/// </summary>
public sealed class Schema
{
    private static readonly (int Version, string Name, string Sql)[] Migrations =
    {
        (1, "initial", """
            CREATE TABLE tasks (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                title        TEXT    NOT NULL,
                details      TEXT    NOT NULL DEFAULT '',
                status       TEXT    NOT NULL DEFAULT 'open'
                             CHECK (status IN ('open','doing','done')),
                priority     INTEGER NOT NULL DEFAULT 2,          -- 1 high, 2 medium, 3 low
                due_date     TEXT,                                -- ISO-8601 date, nullable
                created_utc  TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ','now')),
                completed_utc TEXT
            );

            CREATE INDEX idx_tasks_status ON tasks(status);
            CREATE INDEX idx_tasks_due    ON tasks(due_date);
            """),

        (2, "tags", """
            CREATE TABLE tags (
                id   INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE COLLATE NOCASE
            );

            CREATE TABLE task_tags (
                task_id INTEGER NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
                tag_id  INTEGER NOT NULL REFERENCES tags(id)  ON DELETE CASCADE,
                PRIMARY KEY (task_id, tag_id)
            );
            """),
    };

    public int CurrentVersion(SqliteConnection conn)
    {
        EnsureMigrationsTable(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>Applies any pending migrations inside a transaction each.</summary>
    public void Upgrade(Db db)
    {
        using var conn = db.Open();
        EnsureMigrationsTable(conn);

        foreach (var (version, name, sql) in Migrations)
        {
            if (version <= CurrentVersion(conn)) continue;

            Console.WriteLine($"  → migrating to v{version}: {name}");
            using var tx = conn.BeginTransaction();
            using (var apply = conn.CreateCommand())
            {
                apply.Transaction = tx;
                apply.CommandText = sql;
                apply.ExecuteNonQuery();
            }
            using (var rec = conn.CreateCommand())
            {
                rec.Transaction = tx;
                rec.CommandText =
                    "INSERT INTO schema_migrations (version, name, applied_utc) VALUES ($v, $n, strftime('%Y-%m-%dT%H:%M:%SZ','now'));";
                rec.Parameters.AddWithValue("$v", version);
                rec.Parameters.AddWithValue("$n", name);
                rec.ExecuteNonQuery();
            }
            tx.Commit();
        }

        // SQLite does not allow a subquery in PRAGMA, so read then write the value.
        int max;
        using (var v = conn.CreateCommand())
        {
            v.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
            max = Convert.ToInt32(v.ExecuteScalar());
        }
        // PRAGMA values cannot be parameterised, but `max` is an int we computed
        // ourselves, so interpolating it here carries no injection risk.
        using var pragma = conn.CreateCommand();
        pragma.CommandText = $"PRAGMA user_version = {max};";
        pragma.ExecuteNonQuery();
    }

    /// <summary>Lists tables with their row counts and declared columns.</summary>
    public void ListTables(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        var tables = new List<string>();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) tables.Add(reader.GetString(0));

        foreach (var t in tables)
        {
            using var count = conn.CreateCommand();
            // Table names come from sqlite_master (not user input), so quoting is enough.
            count.CommandText = $"SELECT COUNT(*) FROM \"{t}\";";
            var rows = Convert.ToInt64(count.ExecuteScalar());

            using var cols = conn.CreateCommand();
            cols.CommandText = $"SELECT name FROM pragma_table_info('{t}');";
            var names = new List<string>();
            using (var r = cols.ExecuteReader())
                while (r.Read()) names.Add(r.GetString(0));

            Console.WriteLine($"    • {t}  ({rows:N0} rows)");
            Console.WriteLine($"        columns: {string.Join(", ", names)}");
        }
    }

    private static void EnsureMigrationsTable(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version     INTEGER PRIMARY KEY,
                name        TEXT NOT NULL,
                applied_utc TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }
}
