using System.Diagnostics;
using GitCommands;
using GitCommands.Git;
using GitExtensions.Extensibility.Git;

namespace GitUI.CommandsDialogs.BrowseDialog;

public sealed partial class GitStatusMonitor
{
    internal IndexRefreshPolicy IndexRefreshPolicy { get; init; } = new(() => Environment.TickCount64);

    internal Func<IGitModule, bool> IsIndexRefreshSafe { get; init; } = IndexRefreshGate.IsSafe;

    /// <summary>
    ///  The refresh of the index by a status update, if it is permitted.
    /// </summary>
    private sealed class IndexRefresh(GitStatusMonitor monitor, IGitModule module, bool noLocks, CancellationToken cancellationToken)
    {
        private bool _isRefreshing;
        private long _startTime;

        public CancellationToken CancellationToken => cancellationToken;

        /// <summary>
        ///  Determines how to run the status command. Must be called in the background.
        /// </summary>
        /// <returns>
        ///  Whether the index must not be written, and the token to cancel the command.
        ///  A command which is permitted to refresh the index must not be killed, it would leave its index.lock behind.
        /// </returns>
        public (bool NoLocks, CancellationToken CancellationToken) Begin()
        {
            string workingDir = module.WorkingDir;
            if (!noLocks)
            {
                monitor.IndexRefreshPolicy.OnLocksAllowed(workingDir);
            }
            else if (AppSettings.RefreshIndexAfterSlowStatus.Value && !cancellationToken.IsCancellationRequested)
            {
                _isRefreshing = monitor.IndexRefreshPolicy.TryPermit(workingDir, IsSafe);
            }

            _startTime = Stopwatch.GetTimestamp();
            return (noLocks && !_isRefreshing, _isRefreshing ? CancellationToken.None : cancellationToken);

            bool IsSafe()
                => monitor.UICommandsSource?.UICommands.RepoChangedNotifier.IsLocked is not true
                    && monitor.IsIndexRefreshSafe(module);
        }

        /// <returns>The token of the status update.</returns>
        public CancellationToken End(bool isSuccessful)
        {
            if (noLocks && !_isRefreshing && isSuccessful)
            {
                monitor.IndexRefreshPolicy.OnCompletedWithoutLocks(module.WorkingDir, Stopwatch.GetElapsedTime(_startTime));
            }

            _isRefreshing = false;
            return cancellationToken;
        }

        /// <summary>
        ///  Schedules an update without locks if the failed command was permitted to refresh the index.
        /// </summary>
        /// <returns><see langword="true"/> if the failure is not to be considered as a failure of the update.</returns>
        public bool TryFallBack()
        {
            if (!_isRefreshing)
            {
                return false;
            }

            _isRefreshing = false;
            monitor.IndexRefreshPolicy.OnRefreshFailed(module.WorkingDir);
            if (!cancellationToken.IsCancellationRequested)
            {
                monitor.ScheduleNextInteractiveTime();
            }

            return true;
        }
    }
}
