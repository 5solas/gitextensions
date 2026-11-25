using System.Diagnostics;
using GitCommands.RepoSnapshot;
using GitCommands.Settings;

namespace GitCommands.Git.Broker;

/// <summary>
///  The state of a repository which a persistent process depends on.
/// </summary>
/// <remarks>
///  The process does not check the owner of the repository because the git directory is passed explicitly.
///  So it is used only if git has accepted the repository, i.e. has read its local config.
/// </remarks>
internal sealed class GitBrokerRepository
{
    // Since this version, "--no-lazy-fetch" is supported (which sets GIT_NO_LAZY_FETCH). Older versions could run commands
    // from the config of the repository in order to fetch missing objects.
    private static readonly GitVersion _minGitVersion = new("2.45.0");
    private static readonly string[] _repositoryVariables = ["GIT_DIR", "GIT_COMMON_DIR", "GIT_WORK_TREE", "GIT_OBJECT_DIRECTORY", "GIT_NAMESPACE"];
    private static readonly HashSet<string> _supportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "noop", "noop-v1", "preciousobjects", "relativeworktrees", "worktreeconfig"
    };

    private readonly GitConfigSnapshot _snapshot;
    private readonly ConfigSnapshotPart _part;
    private readonly string _gitCommand;
    private readonly ConfigFileState? _config;

    private GitBrokerRepository(GitConfigSnapshot snapshot, ConfigSnapshotPart part, string gitCommand, string gitDir, ConfigFileState? config, bool isSupported)
    {
        _snapshot = snapshot;
        _part = part;
        _gitCommand = gitCommand;
        _config = config;
        GitDir = gitDir;
        IsSupported = isSupported;
    }

    public string GitDir { get; }

    /// <summary>
    ///  Gets whether a persistent process can be used.
    /// </summary>
    public bool IsSupported { get; }

    /// <summary>
    ///  Gets the state of the repository in the working directory of <paramref name="gitExecutable"/>.
    ///  Only the config which has been read by the application is used, git is not run for it.
    /// </summary>
    /// <param name="known">The last state, returned if it is still correct.</param>
    /// <returns><see langword="null"/> if it is not known whether a persistent process can be used.</returns>
    public static GitBrokerRepository? TryGet(Executable gitExecutable, GitBrokerRepository? known)
    {
        string workingDir = gitExecutable.WorkingDir;
        if (gitExecutable.PrefixArguments.Length > 0 || PathUtil.IsWslPath(workingDir) || IsSetByEnvironment())
        {
            return null;
        }

        bool isUnchanged = known?.IsUnchanged(gitExecutable) is true;
        GitConfigSnapshotProvider? provider = GitConfigSnapshotProviderCache.TryGet(workingDir);
        if (isUnchanged && !known!.IsSupported && ReferenceEquals(provider?.PeekSnapshot(), known._snapshot))
        {
            // If another file of the config is changed, the application will read the config again
            return known;
        }

        if (provider is null || !provider.TryGetKnownSnapshot(out GitConfigSnapshot? snapshot, out ConfigSnapshotPart? part))
        {
            // The application will read the config again, but it is known that it is still the same
            return isUnchanged && known!._part.IsCurrent() ? known : null;
        }

        if (part is null)
        {
            // It is not known whether the config has been changed after it has been read
            return null;
        }

        return isUnchanged && ReferenceEquals(known!._snapshot, snapshot) ? known : Create(gitExecutable, snapshot, part);
    }

    /// <summary>
    ///  Gets whether a process for <paramref name="other"/> can be used for this state, too.
    /// </summary>
    public bool HasSameProcess(GitBrokerRepository? other)
        => other is not null && GitDir == other.GitDir && _gitCommand == other._gitCommand && _config == other._config;

    /// <summary>
    ///  Gets whether <paramref name="other"/> is based on the same config.
    /// </summary>
    public bool HasSameConfig(GitBrokerRepository? other) => ReferenceEquals(_snapshot, other?._snapshot);

    public string? GetConfigValue(string normalizedName) => _snapshot.GetEffectiveValue(normalizedName);

    /// <summary>
    ///  Gets whether git has read the local config and nothing in it lets a process run commands or is unknown.
    /// </summary>
    internal static bool IsConfigSupported(GitConfigSnapshot snapshot)
    {
        bool hasLocalConfig = false;
        foreach (GitConfigEntry entry in snapshot.Entries)
        {
            bool isLocal = entry.Scope is "local" or "worktree";
            hasLocalConfig |= entry.Scope == "local";
            string section = entry.Key.Split('.')[0];
            string variable = entry.Key[(entry.Key.LastIndexOf('.') + 1)..];

            if ((Is(section, "extensions") && !_supportedExtensions.Contains(variable))
                || (Is(section, "remote") && (Is(variable, "promisor") || Is(variable, "partialclonefilter")))
                || (isLocal && Is(entry.Key, "core.sshcommand")))
            {
                return false;
            }
        }

        return hasLocalConfig;

        static bool Is(string value, string expected) => value.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static GitBrokerRepository Create(Executable gitExecutable, GitConfigSnapshot snapshot, ConfigSnapshotPart part)
    {
        GitBrokerStats.CountEvaluation();
        string gitDir = "";
        ConfigFileState? config = null;
        bool isSupported = false;

        try
        {
            GitDirectoryResolver resolver = new();
            gitDir = resolver.Resolve(gitExecutable.WorkingDir);

            if (!PathUtil.IsWslPath(gitDir) && File.Exists(Path.Combine(gitDir, "HEAD")))
            {
                config = ConfigFileState.TryRead(Path.Combine(resolver.ResolveCommonDir(gitDir), "config"));
                isSupported = config is not null
                    && IsConfigSupported(snapshot)
                    && Git.GitVersion.CurrentVersion(gitExecutable) is GitVersion version
                    && version >= _minGitVersion;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Trace.WriteLine(ex);
        }

        return new GitBrokerRepository(snapshot, part, gitExecutable.Command, gitDir, config, isSupported);
    }

    private static bool IsSetByEnvironment()
        => _repositoryVariables.Any(name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)));

    private bool IsUnchanged(Executable gitExecutable)
        => gitExecutable.Command == _gitCommand && (_config is null || _config == ConfigFileState.TryRead(_config.Path));

    private sealed record ConfigFileState(string Path, long Length, DateTime LastWriteTimeUtc)
    {
        public static ConfigFileState? TryRead(string path)
        {
            try
            {
                FileInfo file = new(path);
                return file.Exists ? new ConfigFileState(path, file.Length, file.LastWriteTimeUtc) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
