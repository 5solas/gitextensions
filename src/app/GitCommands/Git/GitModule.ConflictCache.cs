// Lock ordering: _conflictCacheLock is a leaf lock protecting _conflictCache.

// No nested locks or external callbacks are invoked while holding this lock.

using GitCommands.Git;
using GitExtensions.Extensibility;

namespace GitCommands;

public sealed partial class GitModule
{
    private readonly Lock _conflictCacheLock = new();
    private ((long Length, DateTime LastWriteTimeUtc) Key, bool HasConflict)? _conflictCache;

    /// <summary>
    /// Gets the current index file key, or null if the index does not exist.
    /// </summary>
    public (long Length, DateTime LastWriteTimeUtc)? GetIndexKey()
    {
        if (string.IsNullOrEmpty(WorkingDirGitDir))
        {
            return null;
        }

        try
        {
            string indexPath = Path.Combine(WorkingDirGitDir, "index");
            FileInfo fileInfo = new(indexPath);
            return fileInfo.Exists ? (fileInfo.Length, fileInfo.LastWriteTimeUtc) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Injects a known conflict status for the specified index key.
    /// </summary>
    /// <param name="key">The index file key.</param>
    /// <param name="hasConflict">Whether merge conflicts exist.</param>
    public void InjectConflictCache((long Length, DateTime LastWriteTimeUtc) key, bool hasConflict)
    {
        lock (_conflictCacheLock)
        {
            _conflictCache = (key, hasConflict);
        }
    }

    /// <summary>
    /// Returns the cached conflicted merge status if the current index key matches; otherwise null.
    /// </summary>
    public bool? TryGetCachedConflictedMerge()
    {
        (long Length, DateTime LastWriteTimeUtc)? currentKey = GetIndexKey();
        if (currentKey is null)
        {
            return string.IsNullOrEmpty(WorkingDirGitDir) ? null : false;
        }

        lock (_conflictCacheLock)
        {
            if (_conflictCache is not null && _conflictCache.Value.Key == currentKey.Value)
            {
                return _conflictCache.Value.HasConflict;
            }
        }

        return null;
    }

    private bool CacheAndReturnConflictedMerge(ExecutionResult result)
    {
        bool hasConflict = result.ExitedSuccessfully && !string.IsNullOrEmpty(result.StandardOutput);

        if (result.ExitedSuccessfully && GetIndexKey() is (long, DateTime) currentKey)
        {
            lock (_conflictCacheLock)
            {
                _conflictCache = (currentKey, hasConflict);
            }
        }

        return hasConflict;
    }
}
