namespace GitCommands.Git.Broker;

/// <summary>
///  The processes which read attributes.
/// </summary>
internal sealed class CheckAttrPool(TimeSpan idleTimeout, int maxSessions)
    : BrokerSessionPool<CheckAttrSession>(idleTimeout, maxSessions, getTickCount: null, entryTimeout: null)
{
    /// <remarks>
    ///  Git reads the config only once.
    /// </remarks>
    protected override bool HasSameProcess(GitBrokerRepository repository, GitBrokerRepository? previous)
        => repository.HasSameProcess(previous) && repository.HasSameConfig(previous);

    protected override CheckAttrSession? StartSession(Executable gitExecutable, GitBrokerRepository repository)
        => CheckAttrSession.TryStart(gitExecutable, repository);
}
