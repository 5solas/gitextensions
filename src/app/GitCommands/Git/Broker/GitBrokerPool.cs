using System.Diagnostics;
using GitExtensions.Extensibility;
using GitUI;

namespace GitCommands.Git.Broker;

/// <summary>
///  Manages the persistent processes: one per working directory, limited number, ended when idle.
/// </summary>
internal sealed class GitBrokerPool
{
    private const int MaxFailures = 3;
    private const long FailureWindowMilliseconds = 60_000;
    private const long DisabledMilliseconds = 5 * 60_000;
    private const int IdleToleranceMilliseconds = 50;

    private static readonly TimeSpan _queueTimeout = TimeSpan.FromSeconds(5);

    // Lock ordering: the gate of an entry, then _lock. No callbacks are invoked while holding _lock.
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeSpan _idleTimeout;
    private readonly int _maxSessions;
    private readonly Func<long> _getTickCount;

    public GitBrokerPool(TimeSpan idleTimeout, int maxSessions, Func<long>? getTickCount = null)
    {
        _idleTimeout = idleTimeout;
        _maxSessions = maxSessions;
        _getTickCount = getTickCount ?? (() => Environment.TickCount64);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (CatFileSession session in TakeSessions())
            {
                session.KillNow();
            }
        };
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
    ///  Sends a request to the process of the repository in the working directory of <paramref name="gitExecutable"/>.
    ///  Blocks the thread until the response has been received, so nothing is done on the UI thread.
    /// </summary>
    /// <returns><see langword="false"/> if there is no result, so the caller has to run git on its own.</returns>
    public bool TryRequest<T>(Executable gitExecutable, Func<CatFileSession, CancellationToken, T?> request, CancellationToken cancellationToken, out T? result)
    {
        result = default;
        cancellationToken.ThrowIfCancellationRequested();
        if (!ThreadHelper.HasJoinableTaskContext || ThreadHelper.JoinableTaskContext.IsOnMainThread || string.IsNullOrWhiteSpace(gitExecutable.WorkingDir))
        {
            return false;
        }

        Entry entry = GetEntry(gitExecutable.WorkingDir);
        if (!entry.Gate.Wait(_queueTimeout, cancellationToken))
        {
            return false;
        }

        CatFileSession? session = null;
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
            if (session is not null)
            {
                entry.LastUsed = Environment.TickCount64;
                entry.IdleTimer.Change(_idleTimeout, Timeout.InfiniteTimeSpan);
            }

            entry.Gate.Release();
        }
    }

    /// <summary>
    ///  Ends all processes without waiting for them, e.g. before a command which replaces packs.
    /// </summary>
    public void ReleaseAll()
    {
        foreach (CatFileSession session in TakeSessions())
        {
            session.Dispose();
        }
    }

    private Entry GetEntry(string workingDir)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(workingDir, out Entry? entry))
            {
                entry = new Entry(OnIdle);
                _entries.Add(workingDir, entry);
            }

            return entry;
        }
    }

    private CatFileSession? GetSession(Entry entry, Executable gitExecutable)
    {
        CatFileSession? session = entry.Session;
        if (session is not null)
        {
            if (!session.IsStopped && entry.Repository?.IsCurrent(gitExecutable) is true)
            {
                return session;
            }

            Release(entry, session);
        }

        entry.Repository = GitBrokerRepository.TryCreate(gitExecutable);
        if (entry.Repository is null)
        {
            return null;
        }

        session = CatFileSession.Start(gitExecutable, entry.Repository.GitDir);
        List<CatFileSession> excessSessions = [];
        lock (_lock)
        {
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
            if (entry.Session is CatFileSession session && Environment.TickCount64 - entry.LastUsed >= _idleTimeout.TotalMilliseconds - IdleToleranceMilliseconds)
            {
                Release(entry, session);
            }
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private bool Release(Entry entry, CatFileSession session)
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

    private List<CatFileSession> TakeSessions()
    {
        lock (_lock)
        {
            List<CatFileSession> sessions = [];
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
        public Entry(TimerCallback onIdle)
        {
            IdleTimer = new System.Threading.Timer(onIdle, this, Timeout.Infinite, Timeout.Infinite);
        }

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public System.Threading.Timer IdleTimer { get; }

        public CatFileSession? Session { get; set; }

        public GitBrokerRepository? Repository { get; set; }

        public long LastUsed { get; set; }

        public int FailureCount { get; set; }

        public long FirstFailure { get; set; }

        public long? DisabledUntil { get; set; }
    }
}
