using System.Diagnostics;

namespace GitCommands.Git.Broker;

/// <summary>
///  The state of a repository which a persistent process depends on.
/// </summary>
internal sealed record GitBrokerRepository(string GitCommand, string GitDir, string ConfigPath, long ConfigLength, DateTime ConfigLastWriteTime)
{
    private static readonly GitVersion _minGitVersion = new("2.36.0");
    private static readonly string[] _repositoryVariables = ["GIT_DIR", "GIT_COMMON_DIR", "GIT_WORK_TREE", "GIT_OBJECT_DIRECTORY", "GIT_NAMESPACE"];

    // Any occurrence counts, there is no need to parse the file: other repositories are only slower
    private static readonly string[] _unsupportedConfigTexts = ["partialclone", "promisor", "refstorage", "objectformat"];

    /// <summary>
    ///  Gets the state if a persistent process can be used for the repository in the working directory of <paramref name="gitExecutable"/>.
    /// </summary>
    public static GitBrokerRepository? TryCreate(Executable gitExecutable)
    {
        string workingDir = gitExecutable.WorkingDir;
        if (string.IsNullOrWhiteSpace(workingDir) || gitExecutable.PrefixArguments.Length > 0 || PathUtil.IsWslPath(workingDir) || IsSetByEnvironment())
        {
            return null;
        }

        try
        {
            GitDirectoryResolver resolver = new();
            string gitDir = resolver.Resolve(workingDir);
            if (PathUtil.IsWslPath(gitDir) || !File.Exists(Path.Combine(gitDir, "HEAD")))
            {
                return null;
            }

            // The state of the file is read first: if it is changed later, this is noticed by the next request
            FileInfo config = new(Path.Combine(resolver.ResolveCommonDir(gitDir), "config"));
            GitBrokerRepository repository = new(gitExecutable.Command, gitDir, config.FullName, config.Length, config.LastWriteTimeUtc);
            string configText = File.ReadAllText(config.FullName);
            if (_unsupportedConfigTexts.Any(text => configText.Contains(text, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            return Git.GitVersion.CurrentVersion(gitExecutable) is GitVersion version && version >= _minGitVersion
                ? repository
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Trace.WriteLine(ex);
            return null;
        }
    }

    /// <summary>
    ///  Gets whether the process can still be used.
    /// </summary>
    public bool IsCurrent(Executable gitExecutable)
    {
        if (gitExecutable.Command != GitCommand || IsSetByEnvironment())
        {
            return false;
        }

        try
        {
            FileInfo config = new(ConfigPath);
            return config.Exists && config.Length == ConfigLength && config.LastWriteTimeUtc == ConfigLastWriteTime;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsSetByEnvironment()
        => _repositoryVariables.Any(name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)));
}
