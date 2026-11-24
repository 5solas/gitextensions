namespace GitCommands.Git.Broker;

/// <summary>
///  Counts what the persistent git processes do, because only their start is logged in the command log.
/// </summary>
internal static class GitBrokerStats
{
    private static int _cacheHits;
    private static int _evaluations;
    private static int _fallbacks;
    private static int _requests;
    private static int _runningProcesses;
    private static int _starts;

    /// <summary>
    ///  Gets the number of contents provided from memory.
    /// </summary>
    public static int CacheHits => Volatile.Read(ref _cacheHits);

    /// <summary>
    ///  Gets how often it has been determined whether a repository is supported.
    /// </summary>
    public static int Evaluations => Volatile.Read(ref _evaluations);

    /// <summary>
    ///  Gets the number of requests which could not be answered, so that the caller has to run git on its own.
    /// </summary>
    public static int Fallbacks => Volatile.Read(ref _fallbacks);

    /// <summary>
    ///  Gets the number of requests sent to the processes.
    /// </summary>
    public static int Requests => Volatile.Read(ref _requests);

    /// <summary>
    ///  Gets the number of processes which have been started and have not exited yet.
    /// </summary>
    public static int RunningProcesses => Volatile.Read(ref _runningProcesses);

    /// <summary>
    ///  Gets the number of processes started.
    /// </summary>
    public static int Starts => Volatile.Read(ref _starts);

    public static void CountCacheHit() => Interlocked.Increment(ref _cacheHits);

    public static void CountEvaluation() => Interlocked.Increment(ref _evaluations);

    public static void CountExit() => Interlocked.Decrement(ref _runningProcesses);

    public static void CountFallback() => Interlocked.Increment(ref _fallbacks);

    public static void CountRequest() => Interlocked.Increment(ref _requests);

    public static void CountStart()
    {
        Interlocked.Increment(ref _starts);
        Interlocked.Increment(ref _runningProcesses);
    }
}
