using System.Diagnostics;

namespace GitUI.CommandsDialogs.BrowseDialog;

/// <summary>
///  Waits for the result of the status monitor instead of running a separate git command.
/// </summary>
internal sealed class StatusUpdateWaiter
{
    /// <summary>The time to wait for a scheduled command to be started.</summary>
    internal int StartTimeoutMilliseconds { get; init; } = 1500;

    /// <summary>The additional time to wait for the start after the UI thread, which starts the command, was busy.</summary>
    internal int StartTimeoutAfterBusyMilliseconds { get; init; } = 300;

    /// <summary>The overall limit, i.e. the time to wait for a running command.</summary>
    internal int TimeoutMilliseconds { get; init; } = 30_000;

    internal int PollMilliseconds { get; init; } = 50;

    /// <summary>
    ///  Waits until the result is known or is not to be expected.
    /// </summary>
    /// <param name="isKnown">Whether the result is known.</param>
    /// <param name="isUrgent">Whether not to wait (any longer).</param>
    /// <param name="getState">Gets the state of the status monitor, for the given number of milliseconds.</param>
    /// <param name="waitForUIThread">Completes as soon as the UI thread handles messages.</param>
    /// <param name="cancellationToken">The token to cancel waiting.</param>
    public async Task WaitAsync(Func<bool> isKnown, Func<bool> isUrgent, Func<int, StatusUpdateState> getState, Func<Task> waitForUIThread, CancellationToken cancellationToken)
    {
        long startTime = Stopwatch.GetTimestamp();
        long notRunningSince = startTime;
        bool hasWaitedForUIThread = false;

        while (!isKnown() && !isUrgent() && Stopwatch.GetElapsedTime(startTime).TotalMilliseconds < TimeoutMilliseconds)
        {
            switch (getState(StartTimeoutMilliseconds))
            {
                case StatusUpdateState.None:
                    // The result may have been published since it has been checked
                    return;

                case StatusUpdateState.Running:
                    notRunningSince = Stopwatch.GetTimestamp();
                    hasWaitedForUIThread = false;
                    break;

                case StatusUpdateState.Scheduled
                    when Stopwatch.GetElapsedTime(notRunningSince).TotalMilliseconds >= (hasWaitedForUIThread ? StartTimeoutAfterBusyMilliseconds : StartTimeoutMilliseconds):
                    if (hasWaitedForUIThread)
                    {
                        return;
                    }

                    // The timer of the status monitor cannot tick as long as the UI thread is busy
                    await waitForUIThread();
                    hasWaitedForUIThread = true;
                    notRunningSince = Stopwatch.GetTimestamp();
                    break;
            }

            await Task.Delay(PollMilliseconds, cancellationToken);
        }
    }
}
