using GitCommands;
using GitExtensions.Extensibility.Git;
using GitUI.CommandsDialogs.BrowseDialog;
using Microsoft.VisualStudio.Threading;

namespace GitUI.UserControls;

public partial class InteractiveGitActionControl
{
    private static readonly string[] _actionFiles = ["CHERRY_PICK_HEAD", "REVERT_HEAD"];

    private readonly StatusUpdateWaiter _statusUpdateWaiter = new();
    private Func<IGitModule, int, StatusUpdateState>? _getStatusUpdateState;

    /// <summary>
    ///  Sets the source of the state of the status monitor, which calls <see cref="UpdateConflictStatus"/> with its results.
    /// </summary>
    internal void SetStatusUpdateSource(Func<IGitModule, int, StatusUpdateState> getStatusUpdateState)
    {
        _getStatusUpdateState = getStatusUpdateState;
        Disposed += (_, _) =>
        {
            lock (_conflictCheckLock)
            {
                _conflictCheckCts?.Cancel();
            }
        };
    }

    /// <summary>
    ///  Waits for the status monitor to provide the conflict state, if that can be expected.
    /// </summary>
    /// <remarks>
    ///  The result of the monitor is used only if the index has not changed since the status command was started.
    /// </remarks>
    private async Task WaitForConflictStateAsync(IGitModule module, CancellationToken cancellationToken)
    {
        if (_getStatusUpdateState is not { } getStatusUpdateState || module is not GitModule gitModule)
        {
            return;
        }

        await _statusUpdateWaiter.WaitAsync(
            isKnown: () => gitModule.TryGetCachedConflictedMerge() is not null,
            isUrgent: () => IsActionInProgress(module),
            milliseconds => getStatusUpdateState(module, milliseconds),
            waitForUIThread: async () =>
            {
                await this.SwitchToMainThreadAsync(cancellationToken);
                await TaskScheduler.Default;
            },
            cancellationToken);
    }

    /// <summary>
    ///  Determines whether conflicts are likely, so they should be shown without delay.
    /// </summary>
    private static bool IsActionInProgress(IGitModule module)
        => module.InTheMiddleOfMerge()
            || module.InTheMiddleOfRebase()
            || module.InTheMiddleOfPatch()
            || _actionFiles.Any(file => File.Exists(Path.Join(module.WorkingDirGitDir, file)));
}
