using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.NamingConventionBinder;
using System.Globalization;
using System.Reflection;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using Microsoft.Data.Sqlite;
using SqliteConsole;                  // CliExit lives here
using SqliteConsole.Data;
using SqliteConsole.Services;

// ============================================================================
//  sqlite-demo — a console app that creates & manages a SQLite database.
//
//  Usage:
//    sqlite-demo [--db PATH] <command> [args]
//
//  Every command opens the file lazily, so `--db` pointing at a new path just
//  works. Exit code 0 = success, non-zero = the shell can react (`&&`, `||`).
// ============================================================================

const string dbPathDefault = "tasks.db";
var dbPath = Environment.GetEnvironmentVariable("TASKDB") ?? dbPathDefault;
var db = new Db(dbPath);   // non-readonly: rebound by --db above
var schema = new Schema();
var repo = new TaskRepository(db);

var root = new RootCommand("sqlite-demo — create and manage a SQLite database from the Linux terminal");

var pathOpt = new Option<string>(
    aliases: new[] { "--db", "-d" },
    getDefaultValue: () => dbPath,
    description: "Path of the SQLite file (created on demand). Env override: TASKDB");
root.AddGlobalOption(pathOpt);

// Resolve the target file BEFORE building any command: precedence is
//   --db/-d  >  $TASKDB  >  ./tasks.db
var parsed = root.Parse(args);
var resolvedDb = parsed.GetValueForOption(pathOpt);
// If the user did not pass --db explicitly, honour $TASKDB.
if (string.IsNullOrWhiteSpace(resolvedDb) || resolvedDb == dbPath)
    resolvedDb = Environment.GetEnvironmentVariable("TASKDB") ?? dbPath;
if (Path.GetFullPath(resolvedDb) != db.DbPath)
{
    db = new Db(resolvedDb);
    repo = new TaskRepository(db);       // handlers read `repo`, not a stale copy
}

const string Open = "open", Doing = "doing", Done = "done";

// ----------------------------------------------------------- init / info / sql
var init = new Command("init", "Create the database file and apply all pending migrations.");
init.Handler = CommandHandler.Create(() =>
{
    var existed = db.Exists();
    schema.Upgrade(db);
    using var conn = db.Open();
    Console.WriteLine(existed ? $"✔ Already initialised: {db.DbPath}" : $"✔ Created: {db.DbPath}");
    Console.WriteLine($"  schema version : v{schema.CurrentVersion(conn)}");
    Console.WriteLine($"  journal mode   : {Scalar(conn, "PRAGMA journal_mode;")}");
    Console.WriteLine($"  foreign keys   : {Scalar(conn, "PRAGMA foreign_keys;")}");
    Console.WriteLine($"  page size      : {Scalar(conn, "PRAGMA page_size;")} bytes");
    Console.WriteLine("  tables:");
    schema.ListTables(conn);
});
root.AddCommand(init);

var info = new Command("info", "Show file size, schema version and row counts.");
info.Handler = CommandHandler.Create(() =>
{
    if (!db.Exists())
    {
        Console.Error.WriteLine($"✖ No usable database at {db.DbPath} — run: sqlite-demo init");
        ExitState.Want = 1;
        return;
    }
    using var conn = db.Open();
    var fi = new FileInfo(db.DbPath);
    Console.WriteLine($"  path      : {db.DbPath}");
    Console.WriteLine($"  size      : {fi.Length:N0} bytes (+ .db-wal / .db-shm while open)");
    Console.WriteLine($"  modified  : {fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
    Console.WriteLine($"  schema    : v{schema.CurrentVersion(conn)}");
    foreach (var t in new[] { "tasks", "tags", "task_tags" })
        Console.WriteLine($"  {t,-9}: {Scalar(conn, $"SELECT COUNT(*) FROM {t};")} rows");
});
root.AddCommand(info);

var sqlCmd = new Command("sql", "Run one SQL statement and print the result set.");
var sqlArg = new Argument<string>("statement", () => "SELECT name, type FROM sqlite_master ORDER BY name;");
sqlCmd.AddArgument(sqlArg);
// NOTE: the lambda parameter name MUST match the Argument's name
// ("statement") — System.CommandLine's NamingConventionBinder binds by name.
sqlCmd.Handler = CommandHandler.Create<string>(statement =>
{
    try { repo.RunRawSql(statement); }
    catch (SqliteException ex)
    {
        Console.Error.WriteLine($"✖ SQLite error {ex.SqliteErrorCode}: {ex.Message}");
        ExitState.Want = 1;
    }
});
root.AddCommand(sqlCmd);

var backup = new Command("backup", "Copy the database to another file (safe online backup API).");
var outArg = new Argument<string?>("destination", () => null, "Target .db file [default: <name>.backup.db beside the source]");
backup.AddArgument(outArg);
backup.Handler = CommandHandler.Create<string?>(dest =>
{
    dest = string.IsNullOrWhiteSpace(dest) ? Path.ChangeExtension(db.DbPath, ".backup.db") : dest;
    repo.Backup(dest);
    var full = Path.GetFullPath(dest);
    Console.WriteLine($"✔ Wrote {full} ({new FileInfo(full).Length:N0} bytes)");
});
root.AddCommand(backup);

var yesOpt = new Option<bool>("--yes", "Required confirmation to delete the database file.");
var drop = new Command("drop", "Delete the database file (and -wal/-shm). Irreversible.");
drop.AddOption(yesOpt);
drop.Handler = CommandHandler.Create<bool>(yes =>
{
    if (!yes) { Console.Error.WriteLine("✖ Refusing without --yes"); ExitState.Want = 2; return; }
    db.Delete();
    Console.WriteLine("✔ Deleted " + db.DbPath);
});
root.AddCommand(drop);

// ------------------------------------------------------------------- task CRUD
var add = new Command("add", "Insert a new task.");
var titleArg = new Argument<string>("title", "Task title");
var notesOpt = new Option<string>(new[] { "--notes", "-n" }, () => "", "Free-form details");
var prioOpt = new Option<int>(new[] { "--priority", "-p" }, () => 2, "1 = high, 2 = medium, 3 = low");
var dueOpt = new Option<string?>("--due", () => null, "Due date as YYYY-MM-DD");
add.AddArgument(titleArg);
foreach (var o in new Option[] { notesOpt, prioOpt, dueOpt }) add.AddOption(o);
add.Handler = CommandHandler.Create<string, string, int, string?>((title, notes, priority, dueRaw) =>
{
    if (priority is < 1 or > 3) { Console.Error.WriteLine("✖ --priority must be 1, 2 or 3"); ExitState.Want = 2; return; }

    DateOnly? due = null;
    if (!string.IsNullOrWhiteSpace(dueRaw))
    {
        if (!DateOnly.TryParseExact(dueRaw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        { Console.Error.WriteLine("✖ --due must be YYYY-MM-DD"); ExitState.Want = 2; return; }
        due = d;
    }

    try
    {
        var id = repo.Add(title, notes, priority, due);
        Console.WriteLine($"✔ Added task #{id}: {title}");
    }
    catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
    { Console.Error.WriteLine("✖ Constraint violated: " + ex.Message); ExitState.Want = 1; }
});
root.AddCommand(add);

var list = new Command("list", "List tasks with optional filtering and ordering.");
var statusOpt = new Option<string?>(new[] { "--status", "-s" }, () => null, "open | doing | done");
var grepOpt = new Option<string?>(new[] { "--grep", "-g" }, () => null, "Substring match on title/notes");
var orderOpt = new Option<string>(new[] { "--order", "-o" }, () => "id", "id | due | priority");
foreach (var o in new Option[] { statusOpt, grepOpt, orderOpt }) list.AddOption(o);
list.Handler = CommandHandler.Create<string?, string?, string>((status, grep, order) =>
{
    if (status is not (null or Open or Doing or Done))
    { Console.Error.WriteLine("✖ --status must be open|doing|done"); ExitState.Want = 2; return; }
    if (order is not ("id" or "due" or "priority"))
    { Console.Error.WriteLine("✖ --order must be id|due|priority"); ExitState.Want = 2; return; }

    var rows = repo.Query(status, grep, order);
    if (rows.Count == 0) { Console.WriteLine("(no matching tasks)"); return; }

    Console.WriteLine($"  {"ID",-4}{"State",-8}{"P",-3}{"Due",-12}Title");
    Console.WriteLine("  " + new string('-', 66));
    foreach (var t in rows)
        Console.WriteLine($"  {t.Id,-4}{Mark(t.Status),-8}{t.Priority,-3}{(t.DueDate ?? "-"),-12}{Clip(t.Title, 40)}");
    Console.WriteLine($"  {rows.Count} task(s)");
});
root.AddCommand(list);

foreach (var (name, what, status) in new[]
{
    ("done", "Mark a task as done.", Done),
    ("start", "Mark a task as in progress.", Doing),
    ("reopen", "Set a task back to open.", Open),
})
{
    var cmd = new Command(name, what);
    var idArg = new Argument<long>("id", () => 0, "Task id");
    cmd.AddArgument(idArg);
    cmd.Handler = CommandHandler.Create<long>(id => Say(repo.SetStatus(id, status), id, status));
    root.AddCommand(cmd);
}

var rm = new Command("rm", "Delete a task (cascades to its tag links).");
var rmId = new Argument<long>("id", () => 0, "Task id");
rm.AddArgument(rmId);
rm.Handler = CommandHandler.Create<long>(id => Say(repo.Delete(id), id, "deleted"));
root.AddCommand(rm);

var seed = new Command("seed", "Insert N demo rows inside one transaction (fast bulk write).");
var nArg = new Argument<int>("count", () => 50, "How many rows");
seed.AddArgument(nArg);
seed.Handler = CommandHandler.Create<int>(n =>
{
    if (n <= 0) { Console.Error.WriteLine("✖ count must be > 0"); ExitState.Want = 2; return; }
    var written = repo.Seed(Math.Min(n, 100_000));
    Console.WriteLine($"✔ Seeded {written} rows in one transaction");
});
root.AddCommand(seed);

var stats = new Command("stats", "Aggregate counts (single SELECT with conditional sums).");
stats.Handler = CommandHandler.Create(() =>
{
    var s = repo.Stats();
    Console.WriteLine($"  total    : {s.Total}");
    Console.WriteLine($"  open     : {s.Open}");
    Console.WriteLine($"  doing    : {s.Doing}");
    Console.WriteLine($"  done     : {s.Done}");
    Console.WriteLine($"  overdue  : {s.Overdue}");
});
root.AddCommand(stats);

// Add error handling + exit codes, then run. A bad SQL statement or an
// unwritable path should print one clean line, not a .NET stack trace.
var pipeline = new CommandLineBuilder(root)
    .UseDefaults()                       // help, version, typo suggestions, @response files
    .UseExceptionHandler((ex, ctx) =>
    {
        var inner = ex is TargetInvocationException tie && tie.InnerException is not null
            ? tie.InnerException : ex;
        Console.Error.WriteLine(inner is SqliteException se
            ? $"✖ SQLite error {se.SqliteErrorCode}: {se.Message}"
            : $"✖ {inner.GetType().Name}: {inner.Message}");
        ctx.ExitCode = 1;
    }, -100)
    .Build();

var rc = await pipeline.InvokeAsync(args);
return rc != 0 ? rc : ExitState.Want;

// -------------------------------------------------------------------- helpers
static object Scalar(SqliteConnection conn, string sql)
{
    using var c = conn.CreateCommand();
    c.CommandText = sql;
    return c.ExecuteScalar() ?? "(null)";
}

static string Mark(string status) => status switch { "done" => "✓ done", "doing" => "~ doing", _ => "· open" };

static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

static void Say(bool ok, long id, string what)
{
    if (ok) { Console.WriteLine($"✔ Task #{id} {what}"); return; }
    Console.Error.WriteLine($"✖ No task with id {id}");
    ExitState.Want = 1;                         // so scripts can test `$?` / use && and ||
}

// (local) const strings Open/Doing/Done are declared above and reused by the helpers.
