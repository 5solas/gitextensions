using System.Diagnostics.CodeAnalysis;
using GitCommands;
using GitExtensions.Extensibility.Git;

namespace GitUI.LeftPanel;

/// <summary>
///  Reads the only worktree of a repository from its files, like <c>git worktree list</c> reports it.
/// </summary>
internal static class MainWorktreeReader
{
    private const string _symbolicRefPrefix = "ref: ";

    /// <summary>
    ///  Gets the main worktree if the repository has no linked worktrees.
    /// </summary>
    /// <returns><see langword="false"/> if there are linked worktrees, or if it cannot be determined.</returns>
    public static bool TryRead(IGitModule module, [NotNullWhen(returnValue: true)] out GitWorktree? worktree)
    {
        worktree = null;
        string workingDir = module.WorkingDir;
        if (string.IsNullOrEmpty(workingDir) || PathUtil.IsWslPath(workingDir))
        {
            return false;
        }

        try
        {
            string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDir));
            string gitDir = Path.Combine(path, ".git");

            // Neither a linked worktree nor a submodule (".git" is a file) nor a bare repository
            if (!Directory.Exists(gitDir) || !IsSameDirectory(gitDir, module.WorkingDirGitDir) || !IsSameDirectory(gitDir, module.GitCommonDirectory))
            {
                return false;
            }

            string worktreesDir = Path.Combine(gitDir, "worktrees");
            if (Directory.Exists(worktreesDir) && Directory.EnumerateDirectories(worktreesDir).Any())
            {
                return false;
            }

            string head = File.ReadAllText(Path.Combine(gitDir, "HEAD")).Trim();
            string? branch = null;
            ObjectId objectId;
            if (!head.StartsWith(_symbolicRefPrefix, StringComparison.Ordinal))
            {
                if (!ObjectId.TryParse(head, out objectId))
                {
                    return false;
                }
            }
            else
            {
                string refName = head[_symbolicRefPrefix.Length..];
                if (!refName.StartsWith(GitRefName.RefsHeadsPrefix, StringComparison.Ordinal) || !TryResolve(gitDir, refName, out objectId))
                {
                    return false;
                }

                branch = refName[GitRefName.RefsHeadsPrefix.Length..];
            }

            worktree = new GitWorktree(path, branch is null ? GitWorktreeHeadType.Detached : GitWorktreeHeadType.Branch, objectId.ToString(), branch, IsDeleted: false) { IsMain = true };
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsSameDirectory(string path, string? otherPath)
        => !string.IsNullOrEmpty(otherPath)
            && string.Equals(path, Path.TrimEndingDirectorySeparator(Path.GetFullPath(otherPath)), StringComparison.OrdinalIgnoreCase);

    private static bool TryResolve(string gitDir, string refName, out ObjectId objectId)
    {
        objectId = default;
        string refFile = Path.Combine(gitDir, refName.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(refFile))
        {
            return ObjectId.TryParse(File.ReadAllText(refFile).Trim(), out objectId);
        }

        // An unborn branch and other ref backends cannot be resolved
        string packedRefsFile = Path.Combine(gitDir, "packed-refs");
        if (!File.Exists(packedRefsFile))
        {
            return false;
        }

        string suffix = $" {refName}";
        foreach (string line in File.ReadLines(packedRefsFile))
        {
            if (line.EndsWith(suffix, StringComparison.Ordinal) && ObjectId.TryParse(line[..^suffix.Length], out objectId))
            {
                return true;
            }
        }

        return false;
    }
}
