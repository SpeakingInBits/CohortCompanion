namespace CohortCompanion.LoadTest;

/// <summary>
/// Measures how live sessions behave with many students. Running it starts a copy of the real app in a child
/// process, limited to one CPU core and a B1-sized memory cap, then connects simulated students to it.
/// See "Load testing" in docs/DEPLOYMENT.md.
/// </summary>
internal static class Entry
{
    public static Task<int> Main(string[] args) =>
        args.FirstOrDefault() == LoadServer.ModeArgument ? LoadServer.RunAsync(args) : LoadRunner.RunAsync(args);
}
