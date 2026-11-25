using GitCommands;

namespace GitUI.FsMonitor;

internal enum FsMonitorAdvice
{
    /// <summary>A large repository on a local drive.</summary>
    Recommended,

    /// <summary>The repository is too small to benefit from a daemon.</summary>
    SmallRepository,

    /// <summary>The daemon cannot ignore the changes in submodules.</summary>
    ManySubmodules,

    /// <summary>The drive is not a fixed one.</summary>
    NotFixedDrive,

    /// <summary>Git does not support the daemon on network drives and in WSL.</summary>
    NetworkDrive,

    /// <summary>There is no working directory.</summary>
    BareRepository
}

/// <summary>
///  Judges whether <c>core.fsmonitor</c> is suitable for a repository, without running git.
/// </summary>
internal static class FsMonitorAdvisor
{
    /// <summary>The size of the index of about 10,000 files.</summary>
    internal const long MinIndexBytes = 1024 * 1024;

    internal const int ManySubmodulesCount = 10;

    /// <returns>The advice and the size of the index, <see langword="null"/> if it cannot be determined.</returns>
    public static (FsMonitorAdvice Advice, long IndexBytes)? Evaluate(string workingDir, string gitDir, bool isBare, Func<string, DriveType>? getDriveType = null)
    {
        getDriveType ??= static root => new DriveInfo(root).DriveType;

        try
        {
            if (string.IsNullOrEmpty(workingDir) || string.IsNullOrEmpty(gitDir))
            {
                return null;
            }

            if (isBare)
            {
                return (FsMonitorAdvice.BareRepository, 0);
            }

            foreach (string path in new[] { workingDir, gitDir })
            {
                if (PathUtil.IsWslPath(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
                {
                    return (FsMonitorAdvice.NetworkDrive, 0);
                }

                string? root = Path.GetPathRoot(Path.GetFullPath(path));
                DriveType driveType = string.IsNullOrEmpty(root) ? DriveType.Unknown : getDriveType(root);
                if (driveType == DriveType.Network)
                {
                    return (FsMonitorAdvice.NetworkDrive, 0);
                }

                if (driveType != DriveType.Fixed)
                {
                    return (FsMonitorAdvice.NotFixedDrive, 0);
                }
            }

            FileInfo index = new(Path.Combine(gitDir, "index"));
            long indexBytes = index.Exists ? index.Length : 0;
            if (indexBytes < MinIndexBytes)
            {
                return (FsMonitorAdvice.SmallRepository, indexBytes);
            }

            string gitModules = Path.Combine(workingDir, ".gitmodules");
            bool hasManySubmodules = File.Exists(gitModules)
                && File.ReadLines(gitModules).Count(line => line.TrimStart().StartsWith("[submodule", StringComparison.OrdinalIgnoreCase)) >= ManySubmodulesCount;
            return (hasManySubmodules ? FsMonitorAdvice.ManySubmodules : FsMonitorAdvice.Recommended, indexBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
