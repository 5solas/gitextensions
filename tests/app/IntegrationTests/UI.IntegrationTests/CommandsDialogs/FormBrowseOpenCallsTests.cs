using System.Reflection;
using CommonTestUtils;
using GitCommands;
using GitCommands.Git.Broker;
using GitCommands.Logging;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitUI;
using GitUI.CommandsDialogs;
using GitUI.CommandsDialogs.BrowseDialog;
using GitUI.LeftPanel;
using GitUI.UserControls;
using NUnit.Framework;

namespace GitExtensions.UITests.CommandsDialogs;

/// <summary>
///  Checks that git commands avoided when a repository is opened do not change what is displayed.
/// </summary>
[Apartment(ApartmentState.STA)]
[NonParallelizable]
public sealed class FormBrowseOpenCallsTests
{
    private const int TimeoutMilliseconds = 30_000;

    private ReferenceRepository _repository = null!;
    private bool _originalShowAuthorAvatarColumn;
    private bool _originalShowAvailableDiffTools;

    [SetUp]
    public void SetUp()
    {
        _originalShowAuthorAvatarColumn = AppSettings.ShowAuthorAvatarColumn;
        _originalShowAvailableDiffTools = AppSettings.ShowAvailableDiffTools;
        AppSettings.ShowAuthorAvatarColumn = false;
        AppSettings.ShowAvailableDiffTools = false;

        _repository = new ReferenceRepository();

        // Like the start of the application: the first git process must not be started by several threads at the same time
        _repository.Module.GitExecutable.GetOutput("--version");
    }

    [TearDown]
    public void TearDown()
    {
        AppSettings.ShowAuthorAvatarColumn = _originalShowAuthorAvatarColumn;
        AppSettings.ShowAvailableDiffTools = _originalShowAvailableDiffTools;
        GitBroker.ReleaseAll();
        UITest.ProcessUntil("Persistent processes ended", () => GitBrokerStats.RunningProcesses == 0, TimeoutMilliseconds);
        _repository.Dispose();
    }

    [Test]
    public void Single_worktree_should_be_loaded_when_expanded()
    {
        RunForm(async form =>
        {
            await WaitAsync(form);
            TreeNode root = GetWorktreesNode(form);
            GetCommands("worktree list").Should().BeEmpty();
            root.IsExpanded.Should().BeFalse();
            (string, string, string, string?) lazy = Describe(root.Nodes.Cast<TreeNode>().Single());

            root.Expand();
            UITest.ProcessUntil("Worktrees loaded", () => GetCommands("worktree list").Length == 1, TimeoutMilliseconds);
            await WaitAsync(form);

            root.IsExpanded.Should().BeTrue();
            Describe(root.Nodes.Cast<TreeNode>().Single()).Should().Be(lazy);
            GetCommands("worktree list").Should().ContainSingle().Which.IsOnMainThread.Should().BeFalse();

            // As long as the node is expanded, it is loaded with each refresh
            CommandLog.Clear();
            form.GetTestAccessor().RefreshRevisions();
            UITest.ProcessUntil("Worktrees loaded", () => GetCommands("worktree list").Length == 1, TimeoutMilliseconds);
            await WaitAsync(form);
            root.IsExpanded.Should().BeTrue();

            root.Collapse();
            root.Expand();
            UITest.ProcessEventsFor(300);
            GetCommands("worktree list").Should().ContainSingle();

            return;

            static (string Text, string Name, string ToolTip, string? Branch) Describe(TreeNode node)
                => (node.Text, node.Name, node.ToolTipText, ((WorktreeNode)node.Tag!).Worktree.Branch);
        });
    }

    [Test]
    public void Linked_worktrees_should_be_loaded_immediately()
    {
        string path = Path.TrimEndingDirectorySeparator(_repository.Module.WorkingDir) + "-linked";
        _repository.Module.GitExecutable.GetOutput($"worktree add -q --detach \"{path}\"");

        RunForm(async form =>
        {
            await WaitAsync(form);
            TreeNode root = GetWorktreesNode(form);
            UITest.ProcessUntil("Worktrees loaded", () => root.Nodes.Count == 2, TimeoutMilliseconds);
            root.IsExpanded.Should().BeTrue();
            GetCommands("worktree list").Should().NotBeEmpty();
        });
    }

    [Test]
    public void Linked_worktree_created_outside_should_be_shown_by_refresh()
    {
        RunForm(async form =>
        {
            await WaitAsync(form);
            string path = Path.TrimEndingDirectorySeparator(_repository.Module.WorkingDir) + "-linked";
            _repository.Module.GitExecutable.GetOutput($"worktree add -q --detach \"{path}\"");

            form.GetTestAccessor().RefreshRevisions();

            // The nodes are added when the message loop is idle, which is not the case in ProcessUntil
            await WaitAsync(form);
            GetWorktreesNode(form).Nodes.Count.Should().Be(2);
        });
    }

    [Test]
    public void No_conflict_should_be_detected_by_the_status()
    {
        RunForm(async form =>
        {
            await WaitAsync(form);
            WaitForConflictState(form, hasConflicts: false);
            GetBanner(form).Visible.Should().BeFalse();
            GetCommands("ls-files").Should().BeEmpty();
        });
    }

    [Test]
    public void Conflict_without_action_in_progress_should_be_shown_by_the_status()
    {
        CreateConflict(isMerge: false);

        RunForm(async form =>
        {
            await WaitAsync(form);
            UITest.ProcessUntil("Banner shown", () => GetBanner(form).HasConflicts, TimeoutMilliseconds);
            await WaitAsync(form);
            GetBanner(form).Action.Should().Be(InteractiveGitActionControl.GitAction.None);
            GetBanner(form).Visible.Should().BeTrue();
            GetCommands("ls-files").Should().BeEmpty();
        });
    }

    [Test]
    public void Conflict_of_merge_should_be_shown_without_waiting()
    {
        CreateConflict(isMerge: true);

        RunForm(async form =>
        {
            UITest.ProcessUntil("Banner shown", () => GetBanner(form).HasConflicts, TimeoutMilliseconds);
            GetBanner(form).Action.Should().Be(InteractiveGitActionControl.GitAction.Merge);
            GetCommands("ls-files").Should().NotBeEmpty();
            await WaitAsync(form);
        });
    }

    [Test]
    public void Conflict_should_be_shown_without_status_monitor()
    {
        RunForm(async form =>
        {
            await WaitAsync(form);
            WaitForConflictState(form, hasConflicts: false);

            // The settings do not stop the monitor as long as the repository is not reopened by the settings dialog
            GitStatusMonitor monitor = (GitStatusMonitor)typeof(FormBrowse).GetField("_gitStatusMonitor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
            monitor.Active = false;
            CreateConflict(isMerge: false);
            CommandLog.Clear();

            FindBanner(form).RefreshGitAction(checkForConflicts: true);

            UITest.ProcessUntil("Banner shown", () => GetBanner(form).HasConflicts, TimeoutMilliseconds);
            GetCommands("ls-files").Should().NotBeEmpty();
            await WaitAsync(form);
        });
    }

    [Test]
    public void Outdated_status_should_not_hide_a_conflict()
    {
        RunForm(async form =>
        {
            await WaitAsync(form);
            WaitForConflictState(form, hasConflicts: false);
            CreateConflict(isMerge: false);

            // The status read the index before the conflict
            FindBanner(form).UpdateConflictStatus(hasConflicts: false);

            UITest.ProcessUntil("Banner shown", () => GetBanner(form).HasConflicts, TimeoutMilliseconds);
            await WaitAsync(form);
            GetBanner(form).HasConflicts.Should().BeTrue();
        });
    }

    [Test]
    public void Resolved_conflict_should_be_hidden_without_waiting_for_the_status()
    {
        CreateConflict(isMerge: false);

        RunForm(async form =>
        {
            await WaitAsync(form);
            UITest.ProcessUntil("Banner shown", () => GetBanner(form).HasConflicts, TimeoutMilliseconds);
            _repository.Module.GitExecutable.GetOutput("reset -q --hard");
            CommandLog.Clear();

            FindBanner(form).RefreshGitAction(checkForConflicts: true);

            UITest.ProcessUntil("Banner hidden", () => !GetBanner(form).HasConflicts, TimeoutMilliseconds);
            await WaitAsync(form);
        });
    }

    private void CreateConflict(bool isMerge)
    {
        IGitModule module = _repository.Module;
        string file = Path.Combine(module.WorkingDir, "conflict.txt");
        File.WriteAllText(file, "base");
        Run("add .");
        Run("-c user.name=x -c user.email=x@y commit -q -m base");
        if (isMerge)
        {
            Run("checkout -q -b conflicting");
            File.WriteAllText(file, "theirs");
            Run("-c user.name=x -c user.email=x@y commit -q -a -m theirs");
            Run("checkout -q -");
        }
        else
        {
            File.WriteAllText(file, "theirs");
            Run("-c user.name=x -c user.email=x@y stash push -q");
        }

        File.WriteAllText(file, "ours");
        Run("-c user.name=x -c user.email=x@y commit -q -a -m ours");
        Run(isMerge ? "-c user.name=x -c user.email=x@y merge -q conflicting" : "stash apply -q", throwOnErrorExit: false);
        module.GitExecutable.GetOutput("ls-files -u").Should().NotBeEmpty();

        void Run(string arguments, bool throwOnErrorExit = true)
            => module.GitExecutable.Execute(arguments, throwOnErrorExit: throwOnErrorExit);
    }

    private void RunForm(Func<FormBrowse, Task> runTestAsync)
    {
        using UITestThreadExceptions threadExceptions = new();
        GitUICommands commands = new(GlobalServiceContainer.CreateDefaultMockServiceContainer(), _repository.Module);

        UITest.RunForm(
            showForm: () =>
            {
                CommandLog.Clear();
                commands.StartBrowseDialog(owner: null).Should().BeTrue();
            },
            runTestAsync);

        threadExceptions.ThrowIfAny();
    }

    private static CommandLogEntry[] GetCommands(string arguments)
        => [.. CommandLog.Commands.Where(c => c.Arguments.StartsWith(arguments))];

    private static InteractiveGitActionControl FindBanner(FormBrowse form)
        => form.Controls.Find("notificationBarGitActionInProgress", searchAllChildren: true).OfType<InteractiveGitActionControl>().Single();

    private static InteractiveGitActionControl.TestAccessor GetBanner(FormBrowse form)
        => FindBanner(form).GetTestAccessor();

    private static TreeNode GetWorktreesNode(FormBrowse form)
        => form.GetTestAccessor().RepoObjectsTree.GetTestAccessor().TreeView.Nodes.Cast<TreeNode>().Single(n => n.Text == TranslatedStrings.Worktrees);

    private static void WaitForConflictState(FormBrowse form, bool hasConflicts)
        => UITest.ProcessUntil("Conflict state known", () => ((GitModule)form.Module).TryGetCachedConflictedMerge() == hasConflicts, TimeoutMilliseconds);

    private static async Task WaitAsync(FormBrowse form)
    {
        UITest.ProcessUntil("Revision grid loaded", () => form.GetTestAccessor().RevisionGrid.GetTestAccessor().IsDataLoadComplete, TimeoutMilliseconds);
        await AsyncTestHelper.JoinPendingOperationsAsync(TimeSpan.FromMilliseconds(TimeoutMilliseconds));
        UITest.ProcessUntil("Worktrees shown", () => GetWorktreesNode(form).Nodes.Count > 0, TimeoutMilliseconds);
        UITest.ProcessEventsFor(500);
        await AsyncTestHelper.JoinPendingOperationsAsync(TimeSpan.FromMilliseconds(TimeoutMilliseconds));
    }
}
