using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using CommonTestUtils;
using GitCommands;
using GitCommands.Git.Broker;
using GitCommands.Logging;
using GitExtensions.Extensibility.Git;
using GitUI;
using GitUI.CommandsDialogs;
using GitUI.UserControls;
using GitUI.UserControls.RevisionGrid.Reachability;
using NUnit.Framework;

namespace GitExtensions.UITests.CommandsDialogs;

[Apartment(ApartmentState.STA)]
[Category("GitProcessBudget")]
[NonParallelizable]
public partial class FormBrowseGitProcessBudgetTests
{
    // Budgets for opening a repository and selecting HEAD. Only ever lower these numbers;
    // a failure means a change added git processes (or moved one onto the UI thread).
    // Without stashes no git process may run on the UI thread. With stashes 'git stash list' is
    // still read on the UI thread (moving it off caused a selection race; see the perf notes).
    private const int MaxUiThreadGitProcessesWithoutStash = 0;
    private const int MaxUiThreadGitProcessesWithStash = 1;
    // Measured 10 / 11 in 20 of 20 runs: selecting a commit computes 'branch --contains', 'tag --contains',
    // 'describe' and 'branch --merged' from the revision grid's reachability index instead of running git.
    // Viewing the file starts one persistent 'cat-file --batch-command' (it remains for the next files) instead of
    // 'ls-tree' and 'cat-file blob', and 'check-attr'.
    private const int MaxGitProcessesWithoutStash = 10;
    private const int MaxGitProcessesWithStash = 11;

    // 'git status' legitimately runs twice: once for the status monitor and once for the worktree diff.
    private const int MaxProcessesPerCommand = 2;

    private const int StepTimeoutMilliseconds = 30_000;
    private const int IdleSettleMilliseconds = 500;
    private const int TruncatedArgumentLength = 120;
    private const int TopCallStackFramesCount = 5;

    private bool _originalShowAuthorAvatarColumn;
    private bool _originalShowAvailableDiffTools;
    private bool _originalLogCaptureCallStacks;
    private TimeSpan _originalReachabilityWaitTimeout;

    [OneTimeSetUp]
    public void SetUpFixture()
    {
        _originalShowAuthorAvatarColumn = AppSettings.ShowAuthorAvatarColumn;
        _originalShowAvailableDiffTools = AppSettings.ShowAvailableDiffTools;
        _originalLogCaptureCallStacks = AppSettings.LogCaptureCallStacks;
        _originalReachabilityWaitTimeout = CommitReachabilityTracker.DefaultWaitTimeout;

        AppSettings.ShowAvailableDiffTools = false;
        AppSettings.ShowAuthorAvatarColumn = false;
        AppSettings.LogCaptureCallStacks = true;

        // The budgets must not depend on how fast the revision grid is loaded
        CommitReachabilityTracker.DefaultWaitTimeout = TimeSpan.FromMilliseconds(StepTimeoutMilliseconds);
    }

    [SetUp]
    public void SetUp()
    {
        GitBroker.Cache.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        GitBroker.ReleaseAll();
        UITest.ProcessUntil("Persistent processes ended", () => GitBrokerStats.RunningProcesses == 0, maxMilliseconds: StepTimeoutMilliseconds);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        AppSettings.ShowAuthorAvatarColumn = _originalShowAuthorAvatarColumn;
        AppSettings.ShowAvailableDiffTools = _originalShowAvailableDiffTools;
        AppSettings.LogCaptureCallStacks = _originalLogCaptureCallStacks;
        CommitReachabilityTracker.DefaultWaitTimeout = _originalReachabilityWaitTimeout;
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
        int activationCount = 0;
        int activationsBeforeTestBody = 0;

        // Count the activations of FormBrowse from before the form is created: an activation can arrive
        // before the test body runs, e.g. while the form is shown.
        using ActivationCounter activationCounter = new();

        UITest.RunForm(
            showForm: () =>
            {
                CommandLog.Clear();
                commands.StartBrowseDialog(owner: null).Should().BeTrue();
            },
            async (FormBrowse form) =>
            {
                activationsBeforeTestBody = activationCounter.ActivationCount;
                try
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

                    // Wait until initial selection has been processed
                    formAccessor.CommitInfoTabControl.SelectedTab.Should().Be(formAccessor.CommitInfoTabPage);
                    UITest.ProcessUntil(
                        "Initial selection commit message loaded",
                        () => formAccessor.RevisionInfo.GetTestAccessor().CommitMessage.Text.Length > 0,
                        maxMilliseconds: StepTimeoutMilliseconds);
                    await AsyncTestHelper.JoinPendingOperationsAsync(TimeSpan.FromMilliseconds(StepTimeoutMilliseconds));

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
                }
                finally
                {
                    activationCount = activationCounter.ActivationCount;
                }
            });

        recordedCommands.Should().NotBeNull("CommandLog snapshot should have been captured before form closed");
        AssertGitProcessBudget(
            recordedCommands!,
            hasStash ? MaxUiThreadGitProcessesWithStash : MaxUiThreadGitProcessesWithoutStash,
            hasStash ? MaxGitProcessesWithStash : MaxGitProcessesWithoutStash,
            activationCount,
            activationsBeforeTestBody);
    }

    private static bool IsConfigList(CommandLogEntry entry)
        => entry.Arguments.StartsWith("config list", StringComparison.OrdinalIgnoreCase);

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

    private static void AssertGitProcessBudget(
        IReadOnlyList<CommandLogEntry> commands,
        int maxUiThreadGitProcesses,
        int maxGitProcesses,
        int activationCount,
        int activationsBeforeTestBody)
    {
        int totalProcesses = commands.Count;
        int uiThreadProcesses = commands.Count(c => c.IsOnMainThread);
        List<CommandLogEntry> configLists = [.. commands.Where(IsConfigList)];

        // The first activation of the form does not invalidate the git settings (GitModuleForm.OnApplicationActivated),
        // each further activation does, which reads the config again (in the background or on the UI thread).
        // Only these config re-reads are allowed in addition to the budgets.
        int invalidatingActivations = Math.Max(0, activationCount - 1);
        int allowance = Math.Max(0, Math.Min(invalidatingActivations, configLists.Count - 1));
        int uiThreadAllowance = Math.Min(allowance, configLists.Skip(1).Count(c => c.IsOnMainThread));

        Console.WriteLine($"=== Git Process Budget Report (Total: {totalProcesses}, UI: {uiThreadProcesses}, Activations: {activationCount} (before test body: {activationsBeforeTestBody}), ConfigList: {configLists.Count}, Allowance: {allowance}, UI allowance: {uiThreadAllowance}) ===");
        Console.WriteLine(FormatCommandLog(commands));

        var repeatedGroups = commands
            .GroupBy(c => CommandLogEntry.GetGitArgumentsWithoutConfiguration(c.Arguments))
            .Where(g => g.Count() > MaxProcessesPerCommand + (IsConfigList(g.First()) ? allowance : 0))
            .ToList();

        if (repeatedGroups.Count > 0)
        {
            Assert.Fail(FormatViolations(repeatedGroups, commands));
        }

        uiThreadProcesses.Should().BeLessThanOrEqualTo(
            maxUiThreadGitProcesses + uiThreadAllowance,
            $"UI thread git process count exceeded budget ({uiThreadProcesses} > {maxUiThreadGitProcesses} + allowance {uiThreadAllowance}).\n{FormatCommandLog(commands)}");

        totalProcesses.Should().BeLessThanOrEqualTo(
            maxGitProcesses + allowance,
            $"Total git process count exceeded budget ({totalProcesses} > {maxGitProcesses} + allowance {allowance}).\n{FormatCommandLog(commands)}");
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

    /// <summary>
    ///  Counts the WM_ACTIVATEAPP (activating) messages sent to FormBrowse windows of the current thread.
    /// </summary>
    private sealed class ActivationCounter : IDisposable
    {
        private const int WhCallWndProc = 4;
        private const int WmActivateApp = 0x001C;

        // Keeps the delegate alive while the hook is installed
        private readonly HookProc _hookProc;
        private readonly IntPtr _hook;

        public ActivationCounter()
        {
            _hookProc = OnCallWndProc;
            _hook = SetWindowsHookEx(WhCallWndProc, _hookProc, IntPtr.Zero, GetCurrentThreadId());
            _hook.Should().NotBe(IntPtr.Zero, $"the activation hook must be installed (error {Marshal.GetLastWin32Error()})");
        }

        private delegate IntPtr HookProc(int code, IntPtr messageParam, IntPtr messagePointer);

        public int ActivationCount { get; private set; }

        public void Dispose()
        {
            UnhookWindowsHookEx(_hook);
        }

        private IntPtr OnCallWndProc(int code, IntPtr messageParam, IntPtr messagePointer)
        {
            if (code >= 0)
            {
                CallWndProcMessage message = Marshal.PtrToStructure<CallWndProcMessage>(messagePointer);
                if (message.Message == WmActivateApp && message.WParam != IntPtr.Zero && Control.FromHandle(message.HWnd) is FormBrowse)
                {
                    ActivationCount++;
                }
            }

            return CallNextHookEx(_hook, code, messageParam, messagePointer);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int hookId, HookProc hookProc, IntPtr module, uint threadId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr messageParam, IntPtr messagePointer);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [StructLayout(LayoutKind.Sequential)]
        private struct CallWndProcMessage
        {
            public IntPtr LParam;
            public IntPtr WParam;
            public uint Message;
            public IntPtr HWnd;
        }
    }
}
