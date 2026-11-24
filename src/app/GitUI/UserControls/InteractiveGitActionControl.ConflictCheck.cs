// Lock ordering: _conflictCheckLock is a leaf lock protecting background conflict check scheduling.
// Asynchronous operations and ThreadHelper.FileAndForget run outside the lock; no other locks are acquired inside.
using System.ComponentModel;
using GitCommands;
using GitExtensions.Extensibility.Git;
using GitExtUtils.GitUI;
using Microsoft.VisualStudio.Threading;

namespace GitUI.UserControls;

public partial class InteractiveGitActionControl
{
    private readonly Lock _conflictCheckLock = new();
    private CancellationTokenSource? _conflictCheckCts;
    private bool _conflictCheckInProgress;
    private IGitModule? _inFlightModule;

    private bool IsConflictStateCached()
    {
        if (Module is not GitModule gitModule)
        {
            return true;
        }

        return gitModule.TryGetCachedConflictedMerge() is not null;
    }

    private bool PreserveConflictState(GitAction action, bool hasConflicts)
    {
        if (_conflictCheckInProgress && action == _action && _hasConflicts)
        {
            return true;
        }

        return hasConflicts;
    }

    /// <summary>
    /// Updates the conflict status of the action banner.
    /// </summary>
    /// <param name="hasConflicts">Whether merge conflicts exist.</param>
    public void UpdateConflictStatus(bool hasConflicts)
    {
        if (!Module.IsValidGitWorkingDir())
        {
            return;
        }

        // The status monitor has injected its result unless the index was changed while git was running: then the
        // result may be outdated and must not be used for the current index, RefreshGitAction schedules a check.
        if (Module is GitModule gitModule && gitModule.TryGetCachedConflictedMerge() == hasConflicts)
        {
            // Do not preserve the previous state until a check waiting for this result has noticed it
            _conflictCheckInProgress = false;
        }

        RefreshGitAction(checkForConflicts: true);
    }

    private void ScheduleBackgroundConflictCheck()
    {
        lock (_conflictCheckLock)
        {
            if (_conflictCheckInProgress && ReferenceEquals(_inFlightModule, Module))
            {
                return;
            }

            _conflictCheckInProgress = true;
            _conflictCheckCts?.Cancel();
            _conflictCheckCts?.Dispose();
            _conflictCheckCts = new CancellationTokenSource();
            CancellationToken token = _conflictCheckCts.Token;
            IGitModule module = Module;
            _inFlightModule = module;

            // A displayed conflict must not outlive its resolution
            bool canWait = !_hasConflicts;

            ThreadHelper.FileAndForget(async () =>
            {
                try
                {
                    await TaskScheduler.Default;

                    if (canWait)
                    {
                        await WaitForConflictStateAsync(module, token);
                        if (token.IsCancellationRequested || IsDisposed || !ReferenceEquals(Module, module))
                        {
                            return;
                        }
                    }

                    bool hasConflicts;
                    try
                    {
                        hasConflicts = module.InTheMiddleOfConflictedMerge(throwOnErrorExit: false);
                    }
                    catch (Win32Exception)
                    {
                        hasConflicts = false;
                    }

                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    await this.SwitchToMainThreadAsync(token);

                    if (token.IsCancellationRequested || IsDisposed || !ReferenceEquals(Module, module))
                    {
                        return;
                    }

                    // The check could not be cached (git failed, or the index changed while it ran).
                    // Record the result we just computed for the current index so RefreshGitAction
                    // does not schedule another check in a loop; the next index change re-evaluates it.
                    if ((module as GitModule)?.TryGetCachedConflictedMerge() is null)
                    {
                        if (module is GitModule gitModule && gitModule.GetIndexKey() is (long, DateTime) key)
                        {
                            gitModule.InjectConflictCache(key, hasConflicts);
                        }
                    }

                    _conflictCheckInProgress = false;
                    RefreshGitAction(checkForConflicts: true);
                }
                finally
                {
                    lock (_conflictCheckLock)
                    {
                        if (ReferenceEquals(_inFlightModule, module) && !token.IsCancellationRequested)
                        {
                            _conflictCheckInProgress = false;
                        }
                    }
                }
            });
        }
    }
}
