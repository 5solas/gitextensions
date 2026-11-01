// Lock ordering: _gitCommonLock (in GitModule.cs) protects _gitCommonDirectory resolution.
// TryResolveGitCommonDirectoryFromFiles performs lock-free filesystem inspections and acquires no locks.
using System.Diagnostics;
using GitCommands.Git;
using GitExtUtils;

namespace GitCommands;

public sealed partial class GitModule
{
    private string? TryResolveGitCommonDirectoryFromFiles()
    {
        string gitDir = _executor.GetGitDirectory();
        if (string.IsNullOrEmpty(gitDir))
        {
            return null;
        }

        try
        {
            string resolved = new GitDirectoryResolver().ResolveCommonDir(gitDir);
            if (Directory.Exists(resolved))
            {
                return resolved.EnsureTrailingPathSeparator();
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex.Message);
        }

        return null;
    }
}
