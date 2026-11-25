using System.Diagnostics;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;

namespace GitCommands.Git;

/// <summary>
///  The <c>git fsmonitor--daemon</c> of a working directory.
/// </summary>
public static class FsMonitorDaemon
{
    private const string _pipeDirectory = @"\\.\pipe\";
    private const string _ipcName = "fsmonitor--daemon.ipc";
    private static readonly TimeSpan _stopTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    ///  Gets whether the builtin daemon is enabled in the config which has already been read, never runs git.
    /// </summary>
    /// <returns><see langword="null"/> if the config is not known.</returns>
    public static bool? IsEnabled(IGitModule module)
        => (module as GitModule)?.ConfigSnapshotProvider.PeekSnapshot() is { } snapshot
            ? IsEnabled(snapshot.GetEffectiveValue("core.fsmonitor"))
            : null;

    /// <summary>
    ///  Gets the name of the named pipe which the daemon of the working directory with the git directory listens to.
    /// </summary>
    /// <returns><see langword="null"/> if there cannot be a daemon on a local drive.</returns>
    public static string? GetPipeName(string gitDir)
    {
        // git: initialize_pipe_name() in compat/simple-ipc/ipc-win32.c, the real path with "C:\" replaced by "C_\"
        try
        {
            string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gitDir));
            return path.Length > 2 && path[1] == Path.VolumeSeparatorChar && Path.IsPathFullyQualified(gitDir)
                ? $@"{_pipeDirectory}{path[0]}_{path[2..]}\{_ipcName}"
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    ///  Gets the pipes of all running daemons, without connecting to them and without running git.
    /// </summary>
    public static IReadOnlySet<string> GetPipeNames()
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Only all pipes can be enumerated
            foreach (string name in Directory.EnumerateFiles(_pipeDirectory))
            {
                if (name.EndsWith(_ipcName, StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(name);
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"{nameof(FsMonitorDaemon)}: The pipes cannot be enumerated: {ex.Message}");
        }

        return names;
    }

    /// <summary>
    ///  Stops the daemon. Must not be called on the UI thread.
    /// </summary>
    public static void Stop(string workingDir)
    {
        if (!Directory.Exists(workingDir))
        {
            return;
        }

        using CancellationTokenSource cancellationTokenSource = new(_stopTimeout);
        ExecutionResult result = new Executable(() => AppSettings.GitCommand, workingDir)
            .Execute("fsmonitor--daemon stop", throwOnErrorExit: false, cancellationToken: cancellationTokenSource.Token);
        if (!result.ExitedSuccessfully)
        {
            Trace.WriteLine($"{nameof(FsMonitorDaemon)}: The daemon of {workingDir} cannot be stopped: {result.StandardError}");
        }
    }

    /// <summary>
    ///  Other values than booleans are the path of a hook, which is used instead of the daemon.
    /// </summary>
    internal static bool IsEnabled(string? value)
        => value?.ToLowerInvariant() is "true" or "yes" or "on" or "1";
}
