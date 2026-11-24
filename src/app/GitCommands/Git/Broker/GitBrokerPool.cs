namespace GitCommands.Git.Broker;

/// <summary>
///  The processes which read objects.
/// </summary>
internal sealed class GitBrokerPool : BrokerSessionPool<CatFileSession>
{
    private readonly Func<Executable, string, CatFileSession> _startSession;

    public GitBrokerPool(TimeSpan idleTimeout, int maxSessions, Func<long>? getTickCount = null, TimeSpan? entryTimeout = null, Func<Executable, string, CatFileSession>? startSession = null)
        : base(idleTimeout, maxSessions, getTickCount, entryTimeout)
    {
        _startSession = startSession ?? CatFileSession.Start;
    }

    protected override CatFileSession StartSession(Executable gitExecutable, GitBrokerRepository repository)
        => _startSession(gitExecutable, repository.GitDir);
}
