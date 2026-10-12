namespace SqliteConsole;

/// <summary>
/// System.CommandLine resets Environment.ExitCode, so failed operations record
/// their status here instead (async-local, so it survives the handler thread hop).
/// </summary>
public static class CliExit
{
    private static readonly AsyncLocal<int> Wanted = new();
    public static int Want
    {
        get => Wanted.Value;
        set => Wanted.Value = value;
    }
}
