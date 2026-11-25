using System.Diagnostics;
using GitUI.CommandsDialogs.BrowseDialog;

namespace GitUITests.CommandsDialogs.BrowseDialog;

public sealed class StatusUpdateWaiterTests
{
    private readonly StatusUpdateWaiter _waiter = new()
    {
        StartTimeoutMilliseconds = 200,
        StartTimeoutAfterBusyMilliseconds = 100,
        TimeoutMilliseconds = 1500,
        PollMilliseconds = 5
    };

    private int _uiThreadWaits;

    [SetUp]
    public void SetUp()
    {
        _uiThreadWaits = 0;
    }

    [Test]
    public async Task WaitAsync_should_not_wait_if_no_update_is_expected()
    {
        (await MeasureAsync(() => false, () => false, _ => StatusUpdateState.None)).Should().BeLessThan(150);
    }

    [Test]
    public async Task WaitAsync_should_not_wait_if_urgent()
    {
        (await MeasureAsync(() => false, () => true, _ => StatusUpdateState.Running)).Should().BeLessThan(150);
    }

    [Test]
    public async Task WaitAsync_should_wait_for_running_command_beyond_the_start_timeout()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        long elapsed = await MeasureAsync(() => stopwatch.ElapsedMilliseconds >= 600, () => false, _ => StatusUpdateState.Running);

        elapsed.Should().BeInRange(590, 1400);
        _uiThreadWaits.Should().Be(0);
    }

    [Test]
    public async Task WaitAsync_should_stop_if_monitor_stops()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        long elapsed = await MeasureAsync(() => false, () => false, _ => stopwatch.ElapsedMilliseconds < 300 ? StatusUpdateState.Running : StatusUpdateState.None);

        elapsed.Should().BeInRange(290, 1400);
    }

    [Test]
    public async Task WaitAsync_should_limit_the_wait_for_the_start_of_the_command()
    {
        long elapsed = await MeasureAsync(() => false, () => false, _ => StatusUpdateState.Scheduled);

        elapsed.Should().BeInRange(290, 1400);
        _uiThreadWaits.Should().Be(1);
    }

    [Test]
    public async Task WaitAsync_should_consider_that_UI_thread_is_busy()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        bool isStarted = false;

        await _waiter.WaitAsync(
            () => stopwatch.ElapsedMilliseconds >= 900,
            () => false,
            _ => isStarted ? StatusUpdateState.Running : StatusUpdateState.Scheduled,
            async () =>
            {
                await Task.Delay(400);
                isStarted = true;
            },
            CancellationToken.None);

        stopwatch.ElapsedMilliseconds.Should().BeInRange(890, 1400);
    }

    [Test]
    public async Task WaitAsync_should_limit_the_overall_time()
    {
        long elapsed = await MeasureAsync(() => false, () => false, _ => StatusUpdateState.Running);

        elapsed.Should().BeInRange(1490, 5000);
    }

    private async Task<long> MeasureAsync(Func<bool> isKnown, Func<bool> isUrgent, Func<int, StatusUpdateState> getState)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        await _waiter.WaitAsync(
            isKnown,
            isUrgent,
            getState,
            () =>
            {
                _uiThreadWaits++;
                return Task.CompletedTask;
            },
            CancellationToken.None);
        return stopwatch.ElapsedMilliseconds;
    }
}
