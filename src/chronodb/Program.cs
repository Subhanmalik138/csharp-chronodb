namespace ChronoDbCli;

/// <summary>
/// chronodb — command-line client for ChronoDB.
/// Stage 1: placeholder; commands arrive with the storage engine (stage 2),
/// the query engine (stage 3) and the HTTP server (stage 4).
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.WriteLine("chronodb — ChronoDB time-series database CLI (work in progress)");
        Console.WriteLine("Usage: chronodb <serve|ingest|query|list|stats> [options]");
        return 0;
    }
}
