using GitExtensions.Extensibility.Git;

namespace GitUI.CommandsDialogs.BrowseDialog;

public sealed partial class GitStatusMonitor
{
    /// <summary>
    ///  Gets whether a status update for <paramref name="module"/> is running or will be started within <paramref name="withinMilliseconds"/>.
    /// </summary>
    internal StatusUpdateState GetUpdateState(IGitModule module, int withinMilliseconds)
    {
        lock (_statusSequenceLock)
        {
            if (_disposed || _currentStatus != GitStatusMonitorState.Running || !ReferenceEquals(module, Module))
            {
                return StatusUpdateState.None;
            }

            if (_commandIsRunningAndNotCancelled)
            {
                return StatusUpdateState.Running;
            }

            return _nextUpdateTime - Environment.TickCount <= withinMilliseconds
                ? StatusUpdateState.Scheduled
                : StatusUpdateState.None;
        }
    }
}
