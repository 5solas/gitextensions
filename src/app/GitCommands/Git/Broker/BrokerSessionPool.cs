using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using GitCommands.RepoSnapshot;
using GitExtensions.Extensibility;
using GitUI;

namespace GitCommands.Git.Broker;

/// <summary>
///  Manages the persistent processes of one kind: one per working directory, limited number, ended when idle.
/// </summary>
internal abstract class BrokerSessionPool<TSession>
    where TSession : BrokerSession
{
    private const int MaxFailures = 3;
    private const long FailureWindowMilliseconds = 60_000;
    private const long DisabledMilliseconds = 5 * 60_000;
    private const int IdleToleranceMilliseconds = 50;

    // Not before the failures are forgotten
    private static readonly TimeSpan _defaultEntryTimeout = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan _queueTimeout = TimeSpan.FromSeconds(5);

    // Lock ordering: the gate of an entry, then _lock. No callbacks are invoked while holding _lock.
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeSpan _entryTimeout;
    private readonly TimeSpan _idleTimeout;
    private readonly int _maxSessions;
    private readonly Func<long> _getTickCount;
    private int _releaseGeneration;

    protected BrokerSessionPool(TimeSpan idleTimeout, int maxSessions, Func<long>? getTickCount, TimeSpan? entryTimeout)
    {
        _idleTimeout = idleTimeout;
        _entryTimeout = entryTimeout ?? _defaultEntryTimeout;
        _maxSessions = maxSessions;
        _getTickCount = getTickCount ?? (() => Environment.TickCount64);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (TSession session in TakeSessions())
            {
                session.KillNow();
            }
        };
    }

    internal int EntryCount
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    internal int SessionCount
    {
        get
        {
            lock (_lock)
            {
                return _entries.Values.Count(e => e.Session is not null);
            }
        }
    }

    /// <summary>
    ///  Gets the key of a repository, the same one for all spellings of its working directory.
    /// </summary>
    public static bool TryGetKey(string? workingDir, [NotNullWhen(true)] out string? key)
    {
        if (!RepoSnapshotStore.TryGetKey(workingDir, out key))
        {
            return false;
        }

        key = key.ToUpperInvariant();
        return true;
    }

    /// <summary>
    ///  Sends a request to the process of the repository in the working directory of <paramref name="gitExecutable"/>.
    ///  Blocks the thread until the response has been received, so nothing is done on the UI thread.
    /// </summary>
    /// <returns><see langword="false"/> if there is no result, so the caller has to run git on its own.</returns>
    public bool TryRequest<T>(Executable gitExecutable, Func<TSession, CancellationToken, T?> request, CancellationToken cancellationToken, out T? result)
    {
        result = default;
        cancellationToken.ThrowIfCancellationRequested();
        if (!ThreadHelper.HasJoinableTaskContext || ThreadHelper.JoinableTaskContext.IsOnMainThread || !TryGetKey(gitExecutable.WorkingDir, out string? key))
        {
            return false;
        }

        Entry entry = GetEntry(key);
        if (!entry.Gate.Wait(_queueTimeout, cancellationToken))
        {
            return false;
        }

        if (entry.IsRemoved)
        {
            // While having waited, rare
            entry.Gate.Release();
            return false;
        }

        TSession? session = null;
        try
        {
            if (entry.DisabledUntil is long disabledUntil && _getTickCount() < disabledUntil)
            {
                return false;
            }

            session = GetSession(entry, gitExecutable);
            if (session is null)
            {
                return false;
            }

            result = request(session, cancellationToken);
            entry.FailureCount = 0;
            return result is not null;
        }
        catch (Exception ex) when (ex is GitBrokerException or ExternalOperationException)
        {
            Trace.WriteLine(ex);

            // A session which has been released by another thread is not a failure
            if (session is null || Release(entry, session))
            {
                HandleFailure(entry);
            }

            return false;
        }
        finally
        {
            entry.LastUsed = Environment.TickCount64;
            entry.IdleTimer.Change(_idleTimeout, Timeout.InfiniteTimeSpan);
            entry.Gate.Release();
        }
    }

    /// <summary>
    ///  Ends all processes without waiting for them, e.g. before a command which replaces packs.
    /// </summary>
    public void ReleaseAll()
    {
        foreach (TSession session in TakeSessions())
        {
            session.Dispose();
        }
    }

    /// <summary>
    ///  Gets whether a process can be used for the repository.
    /// </summary>
    protected virtual bool IsSupported(GitBrokerRepository repository) => repository.IsSupported;

    /// <summary>
    ///  Gets whether a process which has been started for the <paramref name="previous"/> state can still be used.
    /// </summary>
    protected virtual bool HasSameProcess(GitBrokerRepository repository, GitBrokerRepository? previous) => repository.HasSameProcess(previous);

    /// <returns><see langword="null"/> if a process cannot be used.</returns>
    protected abstract TSession? StartSession(Executable gitExecutable, GitBrokerRepository repository);

    private Entry GetEntry(string key)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out Entry? entry))
            {
                entry = new Entry(key, OnIdle);
                _entries.Add(key, entry);
            }

            return entry;
        }
    }

    private TSession? GetSession(Entry entry, Executable gitExecutable)
    {
        int releaseGeneration = Volatile.Read(ref _releaseGeneration);
        GitBrokerRepository? repository = GitBrokerRepository.TryGet(gitExecutable, entry.Repository);
        bool isSupported = repository is not null && IsSupported(repository);
        TSession? session = entry.Session;
        if (session is not null)
        {
            if (isSupported && !session.IsStopped && HasSameProcess(repository!, entry.Repository))
            {
                entry.Repository = repository;
                return session;
            }

            Release(entry, session);
        }

        entry.Repository = repository;
        if (!isSupported)
        {
            return null;
        }

        session = StartSession(gitExecutable, repository!);
        if (session is null)
        {
            return null;
        }

        List<TSession> excessSessions = [];
        lock (_lock)
        {
            if (releaseGeneration != _releaseGeneration)
            {
                // All processes should have been ended meanwhile
                session.Dispose();
                return null;
            }

            entry.LastUsed = Environment.TickCount64;
            entry.Session = session;
            foreach (Entry other in _entries.Values.Where(e => e.Session is not null && e != entry).OrderByDescending(e => e.LastUsed).Skip(_maxSessions - 1))
            {
                excessSessions.Add(other.Session!);
                other.Session = null;
            }
        }

        excessSessions.ForEach(s => s.Dispose());
        return session;
    }

    private void HandleFailure(Entry entry)
    {
        long now = _getTickCount();
        if (entry.FailureCount == 0 || now - entry.FirstFailure > FailureWindowMilliseconds)
        {
            entry.FailureCount = 0;
            entry.FirstFailure = now;
        }

        if (++entry.FailureCount >= MaxFailures)
        {
            entry.FailureCount = 0;
            entry.DisabledUntil = now + DisabledMilliseconds;
        }
    }

    private void OnIdle(object? state)
    {
        Entry entry = (Entry)state!;

        // If the gate is closed, the timer will be started again after the request
        if (!entry.Gate.Wait(0))
        {
            return;
        }

        try
        {
            if (entry.IsRemoved)
            {
                return;
            }

            TimeSpan idleTime = TimeSpan.FromMilliseconds(Environment.TickCount64 - entry.LastUsed + IdleToleranceMilliseconds);
            if (idleTime >= _idleTimeout && entry.Session is TSession session)
            {
                Release(entry, session);
            }

            if (idleTime >= _entryTimeout)
            {
                Remove(entry);
            }
            else if (idleTime >= _idleTimeout)
            {
                entry.IdleTimer.Change(_entryTimeout - idleTime + TimeSpan.FromMilliseconds(IdleToleranceMilliseconds), Timeout.InfiniteTimeSpan);
            }
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <remarks>Must be called within the gate of the entry.</remarks>
    private void Remove(Entry entry)
    {
        lock (_lock)
        {
            entry.IsRemoved = true;
            _entries.Remove(entry.Key);
        }

        entry.IdleTimer.Dispose();
    }

    private bool Release(Entry entry, TSession session)
    {
        lock (_lock)
        {
            if (entry.Session != session)
            {
                return false;
            }

            entry.Session = null;
        }

        session.Dispose();
        return true;
    }

    private List<TSession> TakeSessions()
    {
        lock (_lock)
        {
            _releaseGeneration++;
            List<TSession> sessions = [];
            foreach (Entry entry in _entries.Values)
            {
                if (entry.Session is not null)
                {
                    sessions.Add(entry.Session);
                    entry.Session = null;
                }
            }

            return sessions;
        }
    }

    private sealed class Entry
    {
        public Entry(string key, TimerCallback onIdle)
        {
            Key = key;
            IdleTimer = new System.Threading.Timer(onIdle, this, Timeout.Infinite, Timeout.Infinite);
        }

        public string Key { get; }

        public bool IsRemoved { get; set; }

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public System.Threading.Timer IdleTimer { get; }

        public TSession? Session { get; set; }

        public GitBrokerRepository? Repository { get; set; }

        public long LastUsed { get; set; }

        public int FailureCount { get; set; }

        public long FirstFailure { get; set; }

        public long? DisabledUntil { get; set; }
    }
}
