using GitExtensions.Extensibility.Git;

namespace GitCommands;

public static partial class FileHelper
{
    private const long CacheExpirationMilliseconds = 10_000;

    private static readonly Lock _attributeLock = new();
    private static readonly Dictionary<(string WorkingDir, string FileName), (bool? IsBinary, long Timestamp)> _attributeCache = new();

    internal static bool TryGetCachedGitAttribute(IGitModule module, string fileName, out bool? isBinary)
    {
        isBinary = null;
        string workingDir = module.WorkingDir;
        if (string.IsNullOrEmpty(workingDir) || string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        lock (_attributeLock)
        {
            (string WorkingDir, string FileName) key = (workingDir, fileName);
            if (!_attributeCache.Remove(key, out (bool? IsBinary, long Timestamp) entry))
            {
                return false;
            }

            long elapsedMilliseconds = Environment.TickCount64 - entry.Timestamp;
            if (elapsedMilliseconds <= CacheExpirationMilliseconds)
            {
                isBinary = entry.IsBinary;
                return true;
            }

            return false;
        }
    }

    internal static bool? CacheGitAttribute(IGitModule module, string fileName, bool? isBinary)
    {
        string workingDir = module.WorkingDir;
        if (!string.IsNullOrEmpty(workingDir) && !string.IsNullOrEmpty(fileName))
        {
            long currentTimestamp = Environment.TickCount64;
            lock (_attributeLock)
            {
                CleanupExpiredAttributes(currentTimestamp);
                _attributeCache[(workingDir, fileName)] = (isBinary, currentTimestamp);
            }
        }

        return isBinary;
    }

    public static void PreloadGitAttributes(IGitModule module, string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return;
        }

        bool? isBinary = IsBinaryAccordingToGitAttributes(module, fileName);
        CacheGitAttribute(module, fileName, isBinary);
    }

    private static void CleanupExpiredAttributes(long currentTimestamp)
    {
        if (_attributeCache.Count == 0)
        {
            return;
        }

        List<(string WorkingDir, string FileName)>? expiredKeys = null;
        foreach (KeyValuePair<(string WorkingDir, string FileName), (bool? IsBinary, long Timestamp)> pair in _attributeCache)
        {
            if (currentTimestamp - pair.Value.Timestamp > CacheExpirationMilliseconds)
            {
                expiredKeys ??= [];
                expiredKeys.Add(pair.Key);
            }
        }

        if (expiredKeys is not null)
        {
            foreach ((string WorkingDir, string FileName) expiredKey in expiredKeys)
            {
                _attributeCache.Remove(expiredKey);
            }
        }
    }
}
