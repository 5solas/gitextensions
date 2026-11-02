using GitCommands.Git;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;

namespace GitCommands.Settings;

internal static class GitConfigSnapshotProviderCache
{
    private static readonly Lock _lock = new();
    private static readonly Dictionary<string, WeakReference<GitConfigSnapshotProvider>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public static GitConfigSnapshotProvider GetOrAdd(IExecutable gitExecutable)
    {
        if (gitExecutable is not Executable)
        {
            return new GitConfigSnapshotProvider(gitExecutable);
        }

        string workingDir = gitExecutable.WorkingDir;
        if (string.IsNullOrEmpty(workingDir))
        {
            return new GitConfigSnapshotProvider(gitExecutable);
        }

        string normalized;
        try
        {
            normalized = Path.GetFullPath(workingDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return new GitConfigSnapshotProvider(gitExecutable);
        }

        lock (_lock)
        {
            if (_cache.TryGetValue(normalized, out WeakReference<GitConfigSnapshotProvider>? weakRef)
                && weakRef.TryGetTarget(out GitConfigSnapshotProvider? provider))
            {
                return provider;
            }

            List<string>? deadKeys = null;
            foreach (KeyValuePair<string, WeakReference<GitConfigSnapshotProvider>> pair in _cache)
            {
                if (!pair.Value.TryGetTarget(out _))
                {
                    deadKeys ??= [];
                    deadKeys.Add(pair.Key);
                }
            }

            if (deadKeys is not null)
            {
                foreach (string deadKey in deadKeys)
                {
                    _cache.Remove(deadKey);
                }
            }

            GitConfigSnapshotProvider newProvider = new(gitExecutable);
            _cache[normalized] = new WeakReference<GitConfigSnapshotProvider>(newProvider);
            return newProvider;
        }
    }
}
