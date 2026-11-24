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
    private volatile bool _isVerificationRequested;

    // The state which the current snapshot depends on, null if unknown
    private volatile ConfigSnapshotPart? _currentPart;
    private long _loadedAtTickCount;

    internal GitConfigSnapshotProvider(GitExtensions.Extensibility.IExecutable gitExecutable, string repoKey)
        : this(gitExecutable)
    {
        _repoKey = repoKey;
    }

    private static RepoSnapshotStore Store => RepoSnapshotStore.Default;

    /// <summary>
    ///  Requests that the current snapshot is verified when it is used next time. To be called if the provider is handed
    ///  to another module: the config may have been changed since the snapshot has been read.
    /// </summary>
    internal void RequestVerification()
    {
        if (_repoKey is not null && Store.IsEnabled)
        {
            _isVerificationRequested = true;
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
        if (!_isVerificationRequested)
        {
            return;
        }

        _isVerificationRequested = false;
        if (!_isValid || _currentSnapshot is not GitConfigSnapshot snapshot)
        {
            return;
        }

        bool isCurrent = _currentPart is ConfigSnapshotPart part && ReferenceEquals(part.Snapshot, snapshot)
            ? part.IsCurrent()
            : Environment.TickCount64 - _loadedAtTickCount < MaxUnverifiableAgeMs;
        if (isCurrent)
        {
            ResetDerivedData(snapshot);
        }
        else
        {
            _isValid = false;
        }
    }

    /// <remarks>Must be called within the lock.</remarks>
    private GitConfigSnapshot LoadOrReuseSnapshot(out ConfigSnapshotPart? part)
    {
        part = null;
        _isVerificationRequested = false;
        if (_repoKey is null || !Store.IsEnabled || !GitExecutable.SupportConfigShowScope())
        {
            return LoadSnapshot();
        }

        if (Store.TryGet(_repoKey, out ConfigSnapshotPart? storedPart))
        {
            if (storedPart.IsCurrent())
            {
                part = storedPart;
                ResetDerivedData(storedPart.Snapshot);
                return storedPart.Snapshot;
            }

            Store.Remove<ConfigSnapshotPart>(_repoKey);
        }

        return ConfigSnapshotPart.Load(GitExecutable, out part) ?? LoadSnapshot();
    }

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
