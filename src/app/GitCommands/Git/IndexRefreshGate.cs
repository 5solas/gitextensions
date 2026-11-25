using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using GitCommands.Git.Extensions;
using GitExtensions.Extensibility.Git;

namespace GitCommands.Git;

/// <summary>
///  Determines whether a background command may take the lock of the index.
/// </summary>
public static partial class IndexRefreshGate
{
    // The executable, options of git and the subcommand: the subcommand must not be found in other arguments.
    [GeneratedRegex(@"^\s*(""[^""]*""|\S+)(\s+(-[cC]\s+(""[^""]*""|\S+)|--[a-z-]+(=(""[^""]*""|[^\s""])+)?))*\s+(cat-file\s+--batch(-command|-check)?(=|\s|$)|check-attr\s+--stdin(\s|$))", RegexOptions.ExplicitCapture)]
    private static partial Regex PersistentReaderCommandLineRegex { get; }

    /// <summary>
    ///  Checks that neither the index of <paramref name="module"/> is locked nor another git command is running.
    ///  No process is started.
    /// </summary>
    /// <remarks>
    ///  This only makes a collision unlikely, a command can still be started after the check.
    /// </remarks>
    public static bool IsSafe(IGitModule module)
        => OperatingSystem.IsWindows()
            && !PathUtil.IsWslPath(module.WorkingDir)
            && !PathUtil.IsWslPath(module.WorkingDirGitDir)
            && !new IndexLockManager(module).IsIndexLocked()
            && !IsGitCommandRunning();

    /// <summary>
    ///  Determines whether the command line is the one of a process which never locks the index and which remains
    ///  although no command is running: the fsmonitor daemon and persistent readers of objects and attributes (like the ones of GE).
    /// </summary>
    internal static bool IsPersistentProcess(string? commandLine)
        => commandLine is not null
            && (GitModule.IsGitFsMonitorDaemon(commandLine) || PersistentReaderCommandLineRegex.IsMatch(commandLine));

    [SupportedOSPlatform("windows")]
    private static bool IsGitCommandRunning()
    {
        Process[] processes = Process.GetProcessesByName("git");
        try
        {
            // A process whose command line cannot be read is considered a running git command.
            return processes.Any(p => !IsPersistentProcess(p.TryGetCommandLine()));
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }
}
