using System.Diagnostics.CodeAnalysis;
using GitCommands.Git;
using GitCommands.RepoSnapshot;

namespace GitCommands.Settings;

/// <summary>
///  Reuse of the config which has been read by another provider for the same repository or before switching repositories.
/// </summary>
public sealed partial class GitConfigSnapshotProvider
{
    // A module gets an unverifiable config of another module only while the repository is being opened
    private const long MaxUnverifiableAgeMs = 10_000;

    // Not set: no reuse, e.g. for a mocked executable
    private readonly string? _repoKey;

    // Count of the requests, and which of them have been answered: a request during a verification is not lost,
    // and it remains pending for all threads until the result of the verification has been applied.
    private int _requestedVerification;
    private volatile int _completedVerification;

    // The state which the current snapshot depends on, null if unknown
    private volatile ConfigSnapshotPart? _currentPart;
    private long _loadedAtTickCount;

    internal GitConfigSnapshotProvider(GitExtensions.Extensibility.IExecutable gitExecutable, string repoKey)
        : this(gitExecutable)
    {
        _repoKey = repoKey;
    }

    /// <summary>
    ///  Gets or sets how to check that git would still read the same config, only to be changed by tests.
    /// </summary>
    internal Func<ConfigSnapshotPart, bool> IsPartCurrent { get; set; } = static part => part.IsCurrent();

    private static RepoSnapshotStore Store => RepoSnapshotStore.Default;

    /// <remarks>
    ///  Must be read before <see cref="_isValid"/>: if no verification is pending, the result of the last one is visible.
    /// </remarks>
    private bool IsVerificationPending => _completedVerification != Volatile.Read(ref _requestedVerification);

    /// <summary>
    ///  Requests that the current snapshot is verified when it is used next time. To be called if the provider is handed
    ///  to another module: the config may have been changed since the snapshot has been read.
    /// </summary>
    internal void RequestVerification()
    {
        if (_repoKey is not null && Store.IsEnabled)
        {
            Interlocked.Increment(ref _requestedVerification);
        }
    }

    /// <summary>
    ///  Gets the current config if it is known that git would read the same, never runs git.
    /// </summary>
    internal bool TryGetVerifiedPart([NotNullWhen(true)] out ConfigSnapshotPart? part)
    {
        part = _currentPart;
        return _isValid && part is not null && ReferenceEquals(part.Snapshot, _currentSnapshot) && part.IsCurrent();
    }

    /// <remarks>Must be called within the lock.</remarks>
    private void VerifyCurrentSnapshotIfRequested()
    {
        // Requests which arrive from now on need another verification, the files may be changed after having been compared
        int requested = Volatile.Read(ref _requestedVerification);
        if (requested == _completedVerification || !_isValid || _currentSnapshot is not GitConfigSnapshot snapshot)
        {
            // If there is no valid snapshot, loading it answers the requests
            return;
        }

        bool isCurrent = _currentPart is ConfigSnapshotPart part && ReferenceEquals(part.Snapshot, snapshot)
            ? IsPartCurrent(part)
            : Environment.TickCount64 - _loadedAtTickCount < MaxUnverifiableAgeMs;
        if (isCurrent)
        {
            ResetDerivedData(snapshot);
        }
        else
        {
            _isValid = false;
        }

        // The last step: other threads do not use the snapshot before
        _completedVerification = requested;
    }

    /// <remarks>Must be called within the lock.</remarks>
    private GitConfigSnapshot LoadOrReuseSnapshot(out ConfigSnapshotPart? part, out int answeredVerification)
    {
        part = null;

        // Requests which arrive from now on are not answered by this load
        answeredVerification = Volatile.Read(ref _requestedVerification);
        if (_repoKey is null || !Store.IsEnabled || !GitExecutable.SupportConfigShowScope())
        {
            return LoadSnapshot();
        }

        if (Store.TryGet(_repoKey, out ConfigSnapshotPart? storedPart))
        {
            if (IsPartCurrent(storedPart))
            {
                part = storedPart;
                ResetDerivedData(storedPart.Snapshot);
                return storedPart.Snapshot;
            }

            Store.Remove<ConfigSnapshotPart>(_repoKey);
        }

        return ConfigSnapshotPart.Load(GitExecutable, out part) ?? LoadSnapshot();
    }

    /// <summary>
    ///  Completes the verifications answered by loading the snapshot, to be called after it has been published.
    /// </summary>
    /// <remarks>Must be called within the lock.</remarks>
    private void CompleteVerification(int answeredVerification) => _completedVerification = answeredVerification;

    /// <remarks>Must be called within the lock.</remarks>
    private void PublishPart(ConfigSnapshotPart? part)
    {
        _currentPart = part;
        _loadedAtTickCount = Environment.TickCount64;
        if (part is not null && _repoKey is not null)
        {
            Store.Set(_repoKey, part);
        }
    }

    private void RemoveStoredPart()
    {
        if (_repoKey is not null)
        {
            Store.Remove<ConfigSnapshotPart>(_repoKey);
        }
    }

    /// <summary>
    ///  Resets the data which does not depend on the config only (e.g. the legacy remotes directories).
    /// </summary>
    private static void ResetDerivedData(GitConfigSnapshot snapshot)
    {
        snapshot.CachedRemoteNames = null;
        snapshot.CachedRemotes = null;
    }
}
