using System.Reflection;
using System.Text;
using CommonTestUtils;
using GitCommands;
using GitCommands.Git;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitUI;
using GitUI.CommandsDialogs;
using GitUI.UserControls.RevisionGrid.Reachability;
using NSubstitute;

namespace GitExtensions.UITests.UserControls.RevisionGrid;

/// <summary>
///  The end of a grid load without reachability index must be signalled, otherwise CommitInfo waits for the timeout
///  before it runs git.
/// </summary>
[Apartment(ApartmentState.STA)]
[NonParallelizable]
public class RevisionGridReachabilityTests
{
    private static readonly TimeSpan _endTimeout = TimeSpan.FromSeconds(10);

    private TimeSpan _originalWaitTimeout;

    [SetUp]
    public void SetUp()
    {
        // Only the end of the load may end the wait
        _originalWaitTimeout = CommitReachabilityTracker.DefaultWaitTimeout;
        CommitReachabilityTracker.DefaultWaitTimeout = TimeSpan.FromMinutes(5);
    }

    [TearDown]
    public void TearDown()
    {
        CommitReachabilityTracker.DefaultWaitTimeout = _originalWaitTimeout;
    }

    [Test]
    public void Load_without_revisions_should_end_the_wait()
    {
        using ReferenceRepository repository = new(createCommit: false);
        RunFormBrowseTest(repository, (grid, _) =>
        {
            Task<CommitReachabilityIndex?> wait = ((ICommitReachabilityProvider)grid).WaitForIndexAsync(CancellationToken.None);

            // RevisionReader notifies an empty list of revisions, so the load is completed with an empty index
            SpinWait.SpinUntil(() => wait.IsCompleted, _endTimeout).Should().BeTrue();
            wait.CompletedResult()!.CommitCount.Should().Be(0);
        });
    }

    [Test]
    public void Failed_load_should_end_the_wait()
    {
        using ReferenceRepository repository = new();
        RunFormBrowseTest(repository, (grid, module) =>
        {
            GitExecutor.TestAccessor executor = module.GetTestAccessor().Executor;
            ICommitReachabilityProvider provider = grid;
            provider.TryGetIndex(out _).Should().BeTrue();

            using ManualResetEventSlim fail = new();
            IGitCommandRunner originalRunner = executor.GitCommandRunner;
            IGitCommandRunner failingRunner = Substitute.For<IGitCommandRunner>();
            failingRunner.RunDetached(Arg.Any<CancellationToken>(), Arg.Any<ArgumentString>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<Encoding?>(), Arg.Any<bool>())
                .Returns(_ =>
                {
                    fail.Wait();
                    throw new InvalidOperationException("git log failed");
                });
            executor.GitCommandRunner = failingRunner;
            try
            {
                grid.PerformRefreshRevisions();
                Task<CommitReachabilityIndex?> wait = provider.WaitForIndexAsync(CancellationToken.None);
                wait.IsCompleted.Should().BeFalse();

                // Do not process messages until the report of the exception is cancelled, it would fail the test
                fail.Set();
                bool isEnded = SpinWait.SpinUntil(() => wait.IsCompleted, _endTimeout);
                Thread.Sleep(1000);
                typeof(ThreadHelper).GetMethod("CancelSwitchToMainThread", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);

                isEnded.Should().BeTrue();
                wait.CompletedResult().Should().BeNull();
            }
            finally
            {
                fail.Set();
                executor.GitCommandRunner = originalRunner;
            }
        });
    }

    private static void RunFormBrowseTest(ReferenceRepository repository, Action<RevisionGridControl, GitModule> runTest)
    {
        GitUICommands commands = new(GlobalServiceContainer.CreateDefaultMockServiceContainer(), repository.Module);
        UITest.RunForm<FormBrowse>(
            showForm: () => commands.StartBrowseDialog(owner: null).Should().BeTrue(),
            runTestAsync: async form =>
            {
                RevisionGridControl grid = form.GetTestAccessor().RevisionGrid;
                UITest.ProcessUntil("Loading revisions", () => grid.GetTestAccessor().IsDataLoadComplete);
                await AsyncTestHelper.JoinPendingOperationsAsync(AsyncTestHelper.UnexpectedTimeout);
                runTest(grid, (GitModule)form.Module);
            });
    }
}
