using GitCommands.Git;
using GitCommands.Settings;
using GitExtensions.Extensibility;

namespace GitCommands.RepoSnapshot;

/// <summary>
///  The git config of a repository together with the state of everything it depends on.
/// </summary>
internal sealed class ConfigSnapshotPart : IRepoSnapshotPart
{
    // The resolution of the file times of FAT file systems
    private static readonly TimeSpan _minFileAge = TimeSpan.FromSeconds(2);

    private readonly GitConfigFiles _directories;
    private readonly FileFingerprint _files;

    private ConfigSnapshotPart(GitConfigSnapshot snapshot, GitConfigFiles directories, FileFingerprint files, string environment)
    {
        Snapshot = snapshot;
        _directories = directories;
        _files = files;
        Environment = environment;
        EstimatedBytes = files.EstimatedBytes + (2L * environment.Length) + EstimateBytes(snapshot);
    }

    public GitConfigSnapshot Snapshot { get; }

    /// <summary>
    ///  Gets the <see cref="GitEnvironmentFingerprint"/>.
    /// </summary>
    public string Environment { get; }

    public long EstimatedBytes { get; }

    internal IReadOnlyCollection<string> Files => _files.Paths;

    /// <summary>
    ///  Reads the config by running git.
    /// </summary>
    /// <param name="part">
    ///  The config with the state it depends on, <see langword="null"/> if it cannot be determined:
    ///  then the returned snapshot must not be reused without reading it again.
    /// </param>
    /// <returns><see langword="null"/> if git failed.</returns>
    public static GitConfigSnapshot? Load(IExecutable gitExecutable, out ConfigSnapshotPart? part)
    {
        part = null;

        // The state before git reads the files: the contents of these files must not change while git is running
        string? environment = GitEnvironmentFingerprint.TryRead();
        FileFingerprint? filesBefore = null;
        if (environment is not null
            && GitConfigFiles.TryResolve(gitExecutable.WorkingDir, out GitConfigFiles? directories)
            && directories.TryGetDefaultFiles(out List<string>? defaultFiles))
        {
            // HEAD is needed only for conditional includes
            filesBefore = FileFingerprint.TryRead([.. defaultFiles, directories.HeadFile]);
        }
        else
        {
            directories = null;
        }

        DateTime startTimeUtc = DateTime.UtcNow;
        IReadOnlyList<GitConfigEntryOrigin>? entries = gitExecutable.GetGitSettingsWithOrigin();
        if (entries is null)
        {
            return null;
        }

        GitConfigSnapshot snapshot = new([.. entries.Select(entry => entry.Entry)]);

        if (filesBefore is not null
            && directories?.TryGetAllFiles(entries) is IReadOnlyList<string> allFiles
            && FileFingerprint.TryRead(allFiles) is FileFingerprint filesAfter
            && filesAfter.IsSameForCommonFiles(filesBefore)
            && allFiles.All(file => filesBefore.Contains(file) || IsOlderThan(file, startTimeUtc - _minFileAge))
            && environment == GitEnvironmentFingerprint.TryRead())
        {
            part = new ConfigSnapshotPart(snapshot, directories, filesAfter, environment!);
        }

        return snapshot;
    }

    /// <summary>
    ///  Gets whether git would read the same config now, without running git.
    /// </summary>
    public bool IsCurrent()
        => Environment == GitEnvironmentFingerprint.TryRead()
            && GitConfigFiles.TryResolve(_directories.WorkingDir, out GitConfigFiles? directories)
            && directories.HasSameDirectories(_directories)
            && _files.IsCurrent();

    /// <summary>
    ///  Gets whether the file has not been changed since <paramref name="timeUtc"/>: git has read the current contents.
    /// </summary>
    private static bool IsOlderThan(string file, DateTime timeUtc)
    {
        try
        {
            // 1601-01-01 for missing files
            return File.GetLastWriteTimeUtc(file) < timeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static long EstimateBytes(GitConfigSnapshot snapshot)
        => snapshot.Entries.Sum(entry => 200L + (4L * (entry.Key.Length + entry.Value.Length)));
}
