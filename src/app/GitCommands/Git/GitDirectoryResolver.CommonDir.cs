using GitExtUtils;

namespace GitCommands.Git;

public sealed partial class GitDirectoryResolver
{
    /// <summary>
    /// Resolves the git common directory for the given git directory.
    /// </summary>
    /// <param name="gitDir">The repository .git folder path.</param>
    /// <returns>The resolved location of the common git folder with a trailing separator.</returns>
    public string ResolveCommonDir(string gitDir)
    {
        ArgumentNullException.ThrowIfNull(gitDir);

        if (string.IsNullOrWhiteSpace(gitDir))
        {
            return string.Empty;
        }

        string commondirFile = Path.Combine(gitDir, "commondir");
        if (_fileSystem.File.Exists(commondirFile))
        {
            string? line = _fileSystem.File.ReadLines(commondirFile).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();
            if (!string.IsNullOrEmpty(line))
            {
                string nativePath = line.ToNativePath();
                if (Path.IsPathFullyQualified(nativePath))
                {
                    return nativePath.EnsureTrailingPathSeparator();
                }

                if (Path.IsPathRooted(nativePath))
                {
                    return PathUtil.GetWindowsPath(line, PathUtil.GetWslDistro(gitDir))
                        .EnsureTrailingPathSeparator();
                }

                return Path.GetFullPath(Path.Join(gitDir, nativePath)).EnsureTrailingPathSeparator();
            }
        }

        return gitDir.EnsureTrailingPathSeparator();
    }
}
