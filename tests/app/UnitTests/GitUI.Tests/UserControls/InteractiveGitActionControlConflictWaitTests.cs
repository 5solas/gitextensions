using CommonTestUtils;
using GitCommands.Logging;
using GitExtensions.Extensibility.Git;
using GitUI;
using GitUI.CommandsDialogs.BrowseDialog;
using GitUI.UserControls;
using NSubstitute;

namespace GitUITests.UserControls;

[Apartment(ApartmentState.STA)]
[NonParallelizable]
public sealed class InteractiveGitActionControlConflictWaitTests
{
    [Test]
    public void Dispose_should_cancel_waiting_for_the_status()
    {
        using ReferenceRepository repository = new();
        IGitUICommands commands = Substitute.For<IGitUICommands>();
        commands.Module.Returns(repository.Module);
        IGitUICommandsSource source = Substitute.For<IGitUICommandsSource>();
        source.UICommands.Returns(commands);
        int stateRequests = 0;
        InteractiveGitActionControl control = new() { UICommandsSource = source };
        control.SetStatusUpdateSource((_, _) =>
        {
            Interlocked.Increment(ref stateRequests);
            return StatusUpdateState.Running;
        });
        _ = control.Handle;
        CommandLog.Clear();

        control.RefreshGitAction(checkForConflicts: true);
        WinFormsTestHelper.ProcessUntil("Waiting", () => Volatile.Read(ref stateRequests) > 0, maxMilliseconds: 5000);
        control.Dispose();
        WinFormsTestHelper.ProcessEventsFor(300);
        int requestsAfterDispose = Volatile.Read(ref stateRequests);
        WinFormsTestHelper.ProcessEventsFor(500);

        Volatile.Read(ref stateRequests).Should().Be(requestsAfterDispose);
        CommandLog.Commands.Should().NotContain(c => c.Arguments.StartsWith("ls-files"));
    }
}
