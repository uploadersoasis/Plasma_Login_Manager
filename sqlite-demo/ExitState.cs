namespace SqliteConsole;

/// <summary>
/// Process-wide exit-code accumulator. Unlike an AsyncLocal, this value is
/// shared across all threads and survives the InvokeAsync() thread hops that
/// System.CommandLine performs internally — so a handler that sets
/// CliExit.Want = 1 reliably makes the process exit non-zero.
/// </summary>
public static class ExitState
{
    private static int _want;

    public static int Want
    {
        get => System.Threading.Volatile.Read(ref _want);
        set => System.Threading.Volatile.Write(ref _want, value);
    }
}
