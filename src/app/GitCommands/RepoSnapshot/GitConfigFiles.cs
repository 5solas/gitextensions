using System.Diagnostics.CodeAnalysis;
using GitCommands.Git;
using GitCommands.Settings;

namespace GitCommands.RepoSnapshot;

/// <summary>
///  A git config entry with the file it is from ("git config list --show-origin").
/// </summary>
internal readonly record struct GitConfigEntryOrigin(GitConfigEntry Entry, string Origin);

/// <summary>
///  Determines which files git reads (or would read if they existed) for "git config list".
/// </summary>
internal sealed class GitConfigFiles
{
    private const string FileOriginPrefix = "file:";
    private const string CommandLineOrigin = "command line:";
    private const string SystemScope = "system";
    private const string LocalScope = "local";
    private const string HomePrefix = "~/";

    private GitConfigFiles(string workingDir, string gitDir, string commonDir)
    {
        WorkingDir = workingDir;
        GitDir = gitDir;
        CommonDir = commonDir;
    }

    public string WorkingDir { get; }

    public string GitDir { get; }

    public string CommonDir { get; }

    public string HeadFile => Path.Combine(GitDir, "HEAD");

    /// <summary>
    ///  Resolves the git directories of a repository, reads files only.
    /// </summary>
    public static bool TryResolve(string workingDir, [NotNullWhen(true)] out GitConfigFiles? files)
    {
        files = null;
        if (!Path.IsPathFullyQualified(workingDir) || PathUtil.IsWslPath(workingDir))
        {
            return false;
        }

        try
        {
            GitDirectoryResolver resolver = new();
            string gitDir = resolver.Resolve(workingDir);
            string commonDir = string.IsNullOrEmpty(gitDir) ? "" : resolver.ResolveCommonDir(gitDir);
            if (!Path.IsPathFullyQualified(gitDir) || !Path.IsPathFullyQualified(commonDir) || !Directory.Exists(commonDir))
            {
                return false;
            }

            files = new GitConfigFiles(workingDir, Normalize(gitDir), Normalize(commonDir));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public bool HasSameDirectories(GitConfigFiles other)
        => string.Equals(WorkingDir, other.WorkingDir, StringComparison.OrdinalIgnoreCase)
            && string.Equals(GitDir, other.GitDir, StringComparison.OrdinalIgnoreCase)
            && string.Equals(CommonDir, other.CommonDir, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///  Gets the files which git reads in any case, independent of the contents of the config. The system config is missing.
    /// </summary>
    /// <returns><see langword="false"/> if the files cannot be determined.</returns>
    public bool TryGetDefaultFiles([NotNullWhen(true)] out List<string>? paths)
    {
        paths =
        [
            Path.Combine(CommonDir, "config"),
            Path.Combine(GitDir, "config.worktree"),
            Path.Combine(GitDir, "commondir")
        ];

        string dotGit = Path.Combine(WorkingDir, ".git");
        if (!Directory.Exists(dotGit))
        {
            // The file of a submodule or worktree (missing for a bare repository)
            paths.Add(dotGit);
        }

        return TryAddEnvironmentFile("GIT_CONFIG_SYSTEM", paths, out _)
            && TryAddEnvironmentFile("GIT_CONFIG_GLOBAL", paths, out bool isGlobalConfigSet)
            && (isGlobalConfigSet || TryAddGlobalFiles(paths));
    }

    /// <summary>
    ///  Gets all files which git has read for the <paramref name="entries"/>.
    /// </summary>
    /// <returns><see langword="null"/> if the files cannot be determined.</returns>
    public IReadOnlyList<string>? TryGetAllFiles(IReadOnlyList<GitConfigEntryOrigin> entries)
    {
        if (!TryGetDefaultFiles(out List<string>? paths))
        {
            return null;
        }

        string localConfig = Path.Combine(CommonDir, "config");
        bool hasLocalConfig = false;

        // The path of the system config is known only if it is set explicitly or if there is an entry
        bool isSystemConfigKnown = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GIT_CONFIG_SYSTEM"));

        foreach ((GitConfigEntry entry, string origin) in entries)
        {
            string? file = null;
            if (origin.StartsWith(FileOriginPrefix, StringComparison.Ordinal))
            {
                if (!TryGetFullPath(origin[FileOriginPrefix.Length..], WorkingDir, out file))
                {
                    return null;
                }

                paths.Add(file);
                isSystemConfigKnown |= entry.Scope == SystemScope;
                hasLocalConfig |= entry.Scope == LocalScope && string.Equals(file, localConfig, StringComparison.OrdinalIgnoreCase);
            }
            else if (origin != CommandLineOrigin)
            {
                return null;
            }

            // Git uses lower case for section and variable, but preserves the subsection
            bool isConditionalInclude = entry.Key.StartsWith("includeif.", StringComparison.Ordinal) && entry.Key.EndsWith(".path", StringComparison.Ordinal);
            if (isConditionalInclude || entry.Key == "include.path")
            {
                // A file which does not exist (yet) or is not included currently is added, too
                string? baseDir = file is null ? null : Path.GetDirectoryName(file);
                if (!TryGetFullPath(entry.Value, baseDir, out string? included))
                {
                    return null;
                }

                paths.Add(included);
            }

            if (isConditionalInclude && entry.Key.StartsWith("includeif.onbranch:", StringComparison.Ordinal))
            {
                if (Directory.Exists(Path.Combine(CommonDir, "reftable")))
                {
                    return null;
                }

                paths.Add(HeadFile);
            }
        }

        // If the local config has not been read, git did not accept the repository (e.g. because of its owner)
        return isSystemConfigKnown && hasLocalConfig ? paths : null;
    }

    /// <summary>
    ///  Gets the full path for a path in a git config file.
    /// </summary>
    internal static bool TryGetFullPath(string path, string? baseDir, [NotNullWhen(true)] out string? fullPath)
    {
        fullPath = null;
        if (path.StartsWith(HomePrefix, StringComparison.Ordinal))
        {
            baseDir = GetHome();
            path = path[HomePrefix.Length..];
        }
        else if (path.StartsWith('~') || path.StartsWith("%(", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            if (!Path.IsPathFullyQualified(path))
            {
                // e.g. "/etc/gitconfig" is relative to the root of the git installation
                if (Path.IsPathRooted(path) || string.IsNullOrEmpty(path) || string.IsNullOrEmpty(baseDir) || !Path.IsPathFullyQualified(baseDir))
                {
                    return false;
                }

                path = Path.Combine(baseDir, path);
            }

            fullPath = Path.GetFullPath(path);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string? GetHome() => Environment.GetEnvironmentVariable("HOME");

    private static bool TryAddEnvironmentFile(string variable, List<string> paths, out bool isSet)
    {
        string? path = Environment.GetEnvironmentVariable(variable);
        isSet = !string.IsNullOrEmpty(path);
        if (!isSet)
        {
            return true;
        }

        if (path == "/dev/null")
        {
            return true;
        }

        if (!Path.IsPathFullyQualified(path!))
        {
            return false;
        }

        paths.Add(path!);
        return true;
    }

    private static bool TryAddGlobalFiles(List<string> paths)
    {
        string? home = GetHome();
        if (string.IsNullOrEmpty(home) || !Path.IsPathFullyQualified(home))
        {
            return false;
        }

        paths.Add(Path.Combine(home, ".gitconfig"));

        string? xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrEmpty(xdgConfigHome))
        {
            if (!Path.IsPathFullyQualified(xdgConfigHome))
            {
                return false;
            }

            paths.Add(Path.Combine(xdgConfigHome, "git", "config"));
        }
        else
        {
            paths.Add(Path.Combine(home, ".config", "git", "config"));

            // Git for Windows
            string? appData = Environment.GetEnvironmentVariable("APPDATA");
            if (!string.IsNullOrEmpty(appData) && Path.IsPathFullyQualified(appData))
            {
                paths.Add(Path.Combine(appData, "Git", "config"));
            }
        }

        // Old versions of Git for Windows
        string? programData = Environment.GetEnvironmentVariable("PROGRAMDATA");
        if (!string.IsNullOrEmpty(programData) && Path.IsPathFullyQualified(programData))
        {
            paths.Add(Path.Combine(programData, "Git", "config"));
        }

        return true;
    }
}
