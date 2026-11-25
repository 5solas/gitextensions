using System.Diagnostics;
using GitCommands.RepoSnapshot;

namespace GitCommands.Git.Broker;

/// <summary>
///  The state of the files which the attributes of a process depend on. The files are not opened.
/// </summary>
/// <remarks>
///  Git reads the index and the attributes of the system, the user and the repository only once. The ones of the folders
///  are read again only if paths of another folder have been requested meanwhile. So the process is outdated
///  if any of these files is not the same as before it has been read.
///  A file which is changed without another size, modification and creation time is not noticed.
/// </remarks>
internal sealed class AttributeFiles
{
    internal const string FileName = ".gitattributes";

    // The resolution of the file times of FAT file systems: a file could be changed again without another time
    private static readonly TimeSpan _minFileAge = TimeSpan.FromSeconds(2);
    private static readonly string[] _unsupportedVariables = ["GIT_ATTR_SOURCE", "GIT_INDEX_FILE"];

    private readonly string _workingDir;
    private readonly List<string> _commonFiles = [];
    private readonly Dictionary<string, FileState> _stateByPath = new(StringComparer.Ordinal);

    private AttributeFiles(string workingDir)
    {
        _workingDir = workingDir;
    }

    /// <summary>
    ///  Determines the files and their state, to be called before the process is started.
    /// </summary>
    /// <returns><see langword="null"/> if a process cannot be used.</returns>
    public static AttributeFiles? TryCreate(Executable gitExecutable, GitBrokerRepository repository)
    {
        try
        {
            string workingDir = Path.GetFullPath(gitExecutable.WorkingDir);
            string gitDir = repository.GitDir;
            string commonDir = new GitDirectoryResolver().ResolveCommonDir(gitDir);

            // Not a bare repository
            if (!Path.Exists(Path.Combine(workingDir, ".git"))
                || IsSetByEnvironment()
                || !TryGetGlobalFiles(repository, workingDir, out List<string> paths)
                || GetSystemFiles(gitExecutable.Command) is not IReadOnlyList<string> systemFiles)
            {
                return null;
            }

            paths.AddRange(systemFiles);
            paths.Add(Path.Combine(gitDir, "index"));
            paths.Add(Path.Combine(gitDir, "info", "attributes"));
            paths.Add(Path.Combine(commonDir, "info", "attributes"));
            paths.Add(Path.Combine(workingDir, FileName));

            AttributeFiles files = new(workingDir);
            files._commonFiles.AddRange(paths);
            return paths.All(files.TryAdd) ? files : null;
        }
        catch (Exception ex) when (IsFileException(ex))
        {
            Trace.WriteLine(ex);
            return null;
        }
    }

    /// <summary>
    ///  Gets whether a path in the repository means the same for a request as for a file on the disk.
    /// </summary>
    public static bool IsSupportedPath(string path)
        => !path.Contains(':')
            && path.Split('/').All(part => part.Length > 0 && part[^1] is not ('.' or ' '));

    /// <summary>
    ///  Gets whether none of the files which the attributes of <paramref name="path"/> depend on has been changed since it has been read.
    /// </summary>
    public bool IsCurrent(string path)
    {
        try
        {
            return !IsSetByEnvironment()
                && _commonFiles.All(IsUnchanged)
                && GetFilesOfFolders(path).All(file => !_stateByPath.ContainsKey(file) || IsUnchanged(file));
        }
        catch (Exception ex) when (IsFileException(ex))
        {
            return false;
        }
    }

    /// <summary>
    ///  Adds the files of all folders of a path, to be called before the request.
    /// </summary>
    /// <returns><see langword="false"/> if it would not be noticed for sure when one of the files is changed.</returns>
    public bool TryAddFoldersOf(string path)
    {
        try
        {
            return GetFilesOfFolders(path).All(TryAdd);
        }
        catch (Exception ex) when (IsFileException(ex))
        {
            return false;
        }
    }

    /// <summary>
    ///  Gets all files which could contain the attributes of the system for the git installation.
    /// </summary>
    /// <returns><see langword="null"/> if the installation is not known.</returns>
    internal static IReadOnlyList<string>? GetSystemFiles(string gitCommand)
    {
        string fileName = OperatingSystem.IsWindows() && !Path.HasExtension(gitCommand) ? $"{gitCommand}.exe" : gitCommand;
        string? fullPath = Path.IsPathFullyQualified(fileName)
            ? fileName
            : PathUtil.TryFindFullPath(fileName, out string? found) ? found : null;
        if (fullPath is null)
        {
            return null;
        }

        // Git determines it relative to its own folder (which may have been started by a launcher), e.g. <root>\mingw64\bin
        List<string> paths = [];
        bool isInstallation = false;
        foreach (string executable in (string[])[fullPath, GitBinaryResolver.Resolve(gitCommand) ?? fullPath])
        {
            for (string? folder = Path.GetDirectoryName(executable); folder is not null; folder = Path.GetDirectoryName(folder))
            {
                string path = Path.Combine(folder, "etc", "gitattributes");
                if (!paths.Contains(path))
                {
                    paths.Add(path);
                }

                // Otherwise it could be a shim of another folder
                isInstallation |= Directory.Exists(Path.Combine(folder, "libexec", "git-core"))
                    || File.Exists(Path.Combine(folder, "usr", "bin", "sh.exe"));
            }
        }

        return isInstallation ? paths : null;
    }

    /// <summary>
    ///  Gets all files which could contain the attributes of the user. Which one is read depends also on their existence.
    /// </summary>
    /// <returns>
    ///  <see langword="null"/> if they are not known. A folder in the format of MSYS ("/c/Users/x") is not converted by git,
    ///  like other relative ones it depends on the current drive of git.
    /// </returns>
    internal static IReadOnlyList<string>? GetDefaultGlobalFiles(Func<string, string?> getVariable)
    {
        List<string> folders = [];
        Add(getVariable("XDG_CONFIG_HOME"), "git");
        Add(getVariable("APPDATA"), "Git");

        string? home = getVariable("HOME");
        if (!string.IsNullOrEmpty(home))
        {
            Add(home, ".config", "git");
        }
        else
        {
            // Git uses the second one if the first one does not exist
            if (!string.IsNullOrEmpty(getVariable("HOMEDRIVE")))
            {
                Add($"{getVariable("HOMEDRIVE")}{getVariable("HOMEPATH")}", ".config", "git");
            }

            Add(getVariable("USERPROFILE"), ".config", "git");
        }

        return folders.Count > 0 && folders.All(Path.IsPathFullyQualified)
            ? [.. folders.Select(folder => Path.GetFullPath(Path.Combine(folder, "attributes")))]
            : null;

        void Add(string? folder, params string[] subFolders)
        {
            if (!string.IsNullOrEmpty(folder))
            {
                folders.Add(Path.Combine([folder, .. subFolders]));
            }
        }
    }

    internal static AttributeFiles CreateForTest(string workingDir) => new(workingDir);

    private static bool IsFileException(Exception ex)
        => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;

    private static bool IsSetByEnvironment()
        => _unsupportedVariables.Any(name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)));

    private static bool TryGetGlobalFiles(GitBrokerRepository repository, string workingDir, out List<string> paths)
    {
        paths = [];
        if (repository.GetConfigValue("attr.tree") is not null
            || repository.GetConfigValue("core.bare")?.ToLowerInvariant() is not (null or "false" or "no" or "off" or "0" or ""))
        {
            return false;
        }

        if (repository.GetConfigValue("core.worktree") is string worktree
            && !(GitConfigFiles.TryGetFullPath(worktree, repository.GitDir, out string? fullPath)
                && Path.TrimEndingDirectorySeparator(fullPath).Equals(Path.TrimEndingDirectorySeparator(workingDir), StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (repository.GetConfigValue("core.attributesfile") is string configured)
        {
            // Neither options (":(optional)") nor the parts which are known by git only
            if (configured.StartsWith(':') || !GitConfigFiles.TryGetFullPath(configured, workingDir, out string? path))
            {
                return false;
            }

            paths.Add(path);
            return true;
        }

        // The variables as set for the process, see Executable.Start()
        EnvironmentConfiguration.SetEnvironmentVariables();
        if (GetDefaultGlobalFiles(Environment.GetEnvironmentVariable) is not IReadOnlyList<string> defaultFiles)
        {
            return false;
        }

        paths.AddRange(defaultFiles);
        return true;
    }

    private IEnumerable<string> GetFilesOfFolders(string path)
    {
        string[] parts = path.Split('/');
        for (int count = 1; count < parts.Length; count++)
        {
            yield return Path.Combine([_workingDir, .. parts[..count], FileName]);
        }
    }

    private bool IsUnchanged(string path) => FileState.TryRead(path) == _stateByPath[path];

    private bool TryAdd(string path)
    {
        if (_stateByPath.ContainsKey(path))
        {
            return true;
        }

        if (FileState.TryRead(path) is not FileState state || state.LastWriteTimeUtc > DateTime.UtcNow - _minFileAge)
        {
            return false;
        }

        _stateByPath.Add(path, state);
        return true;
    }

    private sealed record FileState(long Length, DateTime LastWriteTimeUtc, DateTime CreationTimeUtc)
    {
        private static readonly FileState _missing = new(-1, default, default);

        /// <returns><see langword="null"/> if the state of the file does not tell whether its content is changed.</returns>
        public static FileState? TryRead(string path)
        {
            FileInfo file = new(path);
            if (!file.Exists)
            {
                return _missing;
            }

            return file.Attributes.HasFlag(FileAttributes.ReparsePoint)
                ? null
                : new FileState(file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc);
        }
    }
}
