using System.Diagnostics;
using System.Text;
using CommonTestUtils;
using GitCommands;
using GitCommands.Logging;
using GitExtensions.Extensibility.Git;
using GitUI;
using GitUI.CommandsDialogs;
using GitUI.UserControls;
using NUnit.Framework;

namespace GitExtensions.UITests.CommandsDialogs;

[Apartment(ApartmentState.STA)]
[Category("GitProcessBudget")]
[NonParallelizable]
public class FormBrowseGitProcessBudgetTests
{
    // Budgets for opening a repository and selecting HEAD. Only ever lower these numbers;
    // a failure means a change added git processes (or moved one onto the UI thread).
    // Without stashes no git process may run on the UI thread. With stashes 'git stash list' is
    // still read on the UI thread (moving it off caused a selection race; see the perf notes).
    private const int MaxUiThreadGitProcessesWithoutStash = 0;
    private const int MaxUiThreadGitProcessesWithStash = 1;
    // Measured 15-16 / 16-17: one background call depends on timing, hence one process of headroom.
    private const int MaxGitProcessesWithoutStash = 16;
    private const int MaxGitProcessesWithStash = 17;

    // 'git status' legitimately runs twice: once for the status monitor and once for the worktree diff.
    private const int MaxProcessesPerCommand = 2;

    private const int StepTimeoutMilliseconds = 30_000;
    private const int IdleSettleMilliseconds = 500;
    private const int TruncatedArgumentLength = 120;
    private const int TopCallStackFramesCount = 5;

    private bool _originalShowAuthorAvatarColumn;
    private bool _originalShowAvailableDiffTools;
    private bool _originalLogCaptureCallStacks;

    [OneTimeSetUp]
    public void SetUpFixture()
    {
        _originalShowAuthorAvatarColumn = AppSettings.ShowAuthorAvatarColumn;
        _originalShowAvailableDiffTools = AppSettings.ShowAvailableDiffTools;
        _originalLogCaptureCallStacks = AppSettings.LogCaptureCallStacks;

        AppSettings.ShowAvailableDiffTools = false;
        AppSettings.ShowAuthorAvatarColumn = false;
        AppSettings.LogCaptureCallStacks = true;
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        AppSettings.ShowAuthorAvatarColumn = _originalShowAuthorAvatarColumn;
        AppSettings.ShowAvailableDiffTools = _originalShowAvailableDiffTools;
        AppSettings.LogCaptureCallStacks = _originalLogCaptureCallStacks;
    }

    [Test]
    public void Open_repository_without_stash_should_stay_within_git_process_budget()
    {
        RunGitProcessBudgetScenario(hasStash: false);
    }

    [Test]
    public void Open_repository_with_stash_should_stay_within_git_process_budget()
    {
        RunGitProcessBudgetScenario(hasStash: true);
    }

    private static void RunGitProcessBudgetScenario(bool hasStash)
    {
        using ReferenceRepository referenceRepository = CreateBudgetTestRepository(hasStash);
        GitUICommands commands = new(GlobalServiceContainer.CreateDefaultMockServiceContainer(), referenceRepository.Module);

        IReadOnlyList<CommandLogEntry>? recordedCommands = null;

        UITest.RunForm(
            showForm: () =>
            {
                CommandLog.Clear();
                commands.StartBrowseDialog(owner: null).Should().BeTrue();
            },
            async (FormBrowse form) =>
            {
                FormBrowse.TestAccessor formAccessor = form.GetTestAccessor();

                // 1. Wait for RevisionGrid data load completion
                UITest.ProcessUntil(
                    "Revision grid data load complete",
                    () => formAccessor.RevisionGrid.GetTestAccessor().IsDataLoadComplete
                        && formAccessor.RevisionGrid.GetTestAccessor().VisibleRevisionCount >= 3,
                    maxMilliseconds: StepTimeoutMilliseconds);

                // 2. Wait for LeftPanel load completion
                await AsyncTestHelper.JoinPendingOperationsAsync(TimeSpan.FromMilliseconds(StepTimeoutMilliseconds));
                NativeTreeView leftTreeView = formAccessor.RepoObjectsTree.GetTestAccessor().TreeView;
                UITest.ProcessUntil(
                    "Left panel load complete",
                    () =>
                    {
                        TreeNode? branchesNode = leftTreeView.Nodes.OfType<TreeNode>().FirstOrDefault(n => n.Text == TranslatedStrings.Branches);
                        TreeNode? remotesNode = leftTreeView.Nodes.OfType<TreeNode>().FirstOrDefault(n => n.Text == TranslatedStrings.Remotes);
                        TreeNode? tagsNode = leftTreeView.Nodes.OfType<TreeNode>().FirstOrDefault(n => n.Text == TranslatedStrings.Tags);

                        return branchesNode is not null && branchesNode.Nodes.Count >= 2
                            && remotesNode is not null && remotesNode.Nodes.Count >= 1
                            && tagsNode is not null && tagsNode.Nodes.Count >= 2;
                    },
                    maxMilliseconds: StepTimeoutMilliseconds);

                // 3. Select HEAD revision and wait for Diff file list to load
                ObjectId headCommitId = referenceRepository.CommitHash is not null
                    ? ObjectId.Parse(referenceRepository.CommitHash)
                    : form.Module.GetCurrentCheckout();

                formAccessor.RevisionGrid.SetSelectedRevision(headCommitId);
                formAccessor.CommitInfoTabControl.SelectedTab = formAccessor.DiffTabPage;

                RevisionDiffControl.TestAccessor diffAccessor = formAccessor.RevisionDiffControl.GetTestAccessor();
                UITest.ProcessUntil(
                    "Diff file list loaded for HEAD",
                    () => diffAccessor.DiffFiles.GitItemStatuses.Count > 0,
                    maxMilliseconds: StepTimeoutMilliseconds);
                await AsyncTestHelper.JoinPendingOperationsAsync(TimeSpan.FromMilliseconds(StepTimeoutMilliseconds));

                // 4. Switch to CommitInfo tab and wait for commit message
                formAccessor.CommitInfoTabControl.SelectedTab = formAccessor.CommitInfoTabPage;
                UITest.ProcessUntil(
                    "CommitInfo tab text loaded",
                    () => formAccessor.RevisionInfo.GetTestAccessor().CommitMessage.Text.Length > 0,
                    maxMilliseconds: StepTimeoutMilliseconds);
                await AsyncTestHelper.JoinPendingOperationsAsync(TimeSpan.FromMilliseconds(StepTimeoutMilliseconds));

                // 5. Switch back to Diff tab and wait for diff reload
                formAccessor.CommitInfoTabControl.SelectedTab = formAccessor.DiffTabPage;
                UITest.ProcessUntil(
                    "Diff file list reloaded",
                    () => diffAccessor.DiffFiles.GitItemStatuses.Count > 0,
                    maxMilliseconds: StepTimeoutMilliseconds);
                await AsyncTestHelper.JoinPendingOperationsAsync(TimeSpan.FromMilliseconds(StepTimeoutMilliseconds));

                // 6. Settle idle events for 500ms and snapshot CommandLog
                UITest.ProcessEventsFor(IdleSettleMilliseconds);
                recordedCommands = CommandLog.Commands.Where(IsGitCommand).ToArray();
            });

        recordedCommands.Should().NotBeNull("CommandLog snapshot should have been captured before form closed");
        AssertGitProcessBudget(
            recordedCommands!,
            hasStash ? MaxUiThreadGitProcessesWithStash : MaxUiThreadGitProcessesWithoutStash,
            hasStash ? MaxGitProcessesWithStash : MaxGitProcessesWithoutStash);
    }

    private static bool IsGitCommand(CommandLogEntry entry)
    {
        string fileName = entry.FileName;
        return fileName.EndsWith("git", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith("git.exe", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(AppSettings.GitCommand, StringComparison.OrdinalIgnoreCase)
            || (fileName.StartsWith(AppSettings.WslCommand, StringComparison.OrdinalIgnoreCase)
                && fileName.Contains("git", StringComparison.OrdinalIgnoreCase));
    }

    private static ReferenceRepository CreateBudgetTestRepository(bool hasStash)
    {
        ReferenceRepository referenceRepository = new(createCommit: false);

        string commit1 = referenceRepository.CreateCommit("Commit 1", "content 1", "file1.txt");
        referenceRepository.CreateBranch("feature1", commit1);
        referenceRepository.CreateTag("v1.0-light", commit1);

        string commit2 = referenceRepository.CreateCommit("Commit 2", "content 2", "file2.txt");
        referenceRepository.CreateAnnotatedTag("v1.0", commit2, "Release v1.0");

        string commit3 = referenceRepository.CreateCommit("Commit 3 (HEAD)", "head content", "head_file.txt");

        // The remote is never contacted; it only has to exist in the config.
        string dummyRemotePath = Path.Combine(Path.GetTempPath(), "gitext-dummy-remote-" + Guid.NewGuid().ToString("N"));
        referenceRepository.Module.AddRemote("origin", dummyRemotePath);
        referenceRepository.Module.GitExecutable.GetOutput($"update-ref refs/remotes/origin/master {commit1}");

        if (hasStash)
        {
            referenceRepository.Stash("Stash 1", "stashed modification");
        }

        return referenceRepository;
    }

    private static void AssertGitProcessBudget(IReadOnlyList<CommandLogEntry> commands, int maxUiThreadGitProcesses, int maxGitProcesses)
    {
        int totalProcesses = commands.Count;
        int uiThreadProcesses = commands.Count(c => c.IsOnMainThread);

        Console.WriteLine($"=== Git Process Budget Report (Total: {totalProcesses}, UI: {uiThreadProcesses}) ===");
        Console.WriteLine(FormatCommandLog(commands));

        var repeatedGroups = commands
            .GroupBy(c => CommandLogEntry.GetGitArgumentsWithoutConfiguration(c.Arguments))
            .Where(g => g.Count() > MaxProcessesPerCommand)
            .ToList();

        if (repeatedGroups.Count > 0)
        {
            Assert.Fail(FormatViolations(repeatedGroups, commands));
        }

        uiThreadProcesses.Should().BeLessThanOrEqualTo(
            maxUiThreadGitProcesses,
            $"UI thread git process count exceeded budget ({uiThreadProcesses} > {maxUiThreadGitProcesses}).\n{FormatCommandLog(commands)}");

        totalProcesses.Should().BeLessThanOrEqualTo(
            maxGitProcesses,
            $"Total git process count exceeded budget ({totalProcesses} > {maxGitProcesses}).\n{FormatCommandLog(commands)}");
    }

    private static string FormatCommandLog(IReadOnlyList<CommandLogEntry> commands)
    {
        StringBuilder stringBuilder = new();
        stringBuilder.AppendLine("Recorded Git commands:");
        for (int i = 0; i < commands.Count; i++)
        {
            CommandLogEntry entry = commands[i];
            string thread = entry.IsOnMainThread ? "UI" : "BG";
            double durationMilliseconds = entry.Duration?.TotalMilliseconds ?? -1;
            string arguments = entry.Arguments;
            string truncatedArguments = arguments.Length <= TruncatedArgumentLength
                ? arguments
                : string.Concat(arguments.AsSpan(0, TruncatedArgumentLength), "...");
            stringBuilder.AppendLine($"  [{i + 1,2}] {thread} ({durationMilliseconds,6:F1} ms): {truncatedArguments}");
        }

        return stringBuilder.ToString();
    }

    private static string FormatViolations(
        IEnumerable<IGrouping<string, CommandLogEntry>> violations,
        IReadOnlyList<CommandLogEntry> commands)
    {
        StringBuilder stringBuilder = new();
        stringBuilder.AppendLine("Command repetition budget violations:");
        foreach (IGrouping<string, CommandLogEntry> group in violations)
        {
            stringBuilder.AppendLine($"  Command: '{group.Key}' was executed {group.Count()} times:");
            int instanceIndex = 1;
            foreach (CommandLogEntry entry in group)
            {
                string thread = entry.IsOnMainThread ? "UI" : "BG";
                stringBuilder.AppendLine($"    Occurrence {instanceIndex++} ({thread}):");
                if (entry.CallStack is not null)
                {
                    string[] frames = entry.CallStack.ToString()
                        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                    foreach (string frame in frames.Take(TopCallStackFramesCount))
                    {
                        stringBuilder.AppendLine($"      {frame.Trim()}");
                    }
                }
                else
                {
                    stringBuilder.AppendLine("      (CallStack not captured)");
                }
            }
        }

        stringBuilder.AppendLine();
        stringBuilder.Append(FormatCommandLog(commands));
        return stringBuilder.ToString();
    }
}
