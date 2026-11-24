using System.ComponentModel.Design;
using CommonTestUtils;
using GitCommands;
using GitCommands.Git;
using GitCommands.Logging;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitExtUtils;
using GitUI;
using GitUI.CommandsDialogs;
using GitUI.UserControls;
using GitUI.UserControls.RevisionGrid;
using NUnit.Framework;

namespace GitExtensions.UITests.CommandsDialogs;

/// <summary>
///  Counts the git processes of switching back to a repository (A -> B -> A, the commands of the last step only).
/// </summary>
public partial class FormBrowseGitProcessBudgetTests
{
    // Only ever lower these numbers, see the budgets for opening a repository.
    // Before the config and the stashes were reused: 6 (UI 0) / 7 (UI 1), and one more on the UI thread after a garbage collection.
    private const int MaxUiThreadGitProcessesForSwitchBack = 0;
    private const int MaxGitProcessesForSwitchBack = 6;

    [Test]
    public void Switch_back_to_repository_without_stash_should_stay_within_git_process_budget([Values] bool collectGarbage)
    {
        RunSwitchBackScenario(hasStash: false, collectGarbage);
    }

    [Test]
    public void Switch_back_to_repository_with_stash_should_stay_within_git_process_budget([Values] bool collectGarbage)
    {
        RunSwitchBackScenario(hasStash: true, collectGarbage);
    }

    [Test]
    public void Switch_back_to_repository_should_show_changes_made_outside()
    {
        const string settingName = "hj.test";
        ObjectId newStashId = default;

        IReadOnlyList<CommandLogEntry> commands = RunSwitchBack(
            hasStash: true,
            collectGarbage: false,
            beforeSwitchBack: workingDirA =>
            {
                GitModule module = new(new GitExecutorProvider(new GitDirectoryResolver()), workingDirA);
                module.GitExecutable.GetOutput($"config {settingName} changed");
                File.WriteAllText(Path.Combine(workingDirA, "file1.txt"), "stashed outside");
                module.GitExecutable.GetOutput("stash push -m outside");
                newStashId = ObjectId.Parse(module.GitExecutable.GetOutput("rev-parse refs/stash").Trim());
            },
            afterSwitchBack: form =>
            {
                form.Module.GetEffectiveSetting(settingName).Should().Be("changed");
                ((ICheckRefs)form.GetTestAccessor().RevisionGrid).Contains(newStashId).Should().BeTrue();
            },
            out _);

        commands.Count(IsConfigList).Should().BeGreaterThanOrEqualTo(1);
        commands.Count(c => c.Arguments.StartsWith("stash list")).Should().Be(1);
    }

    private static void RunSwitchBackScenario(bool hasStash, bool collectGarbage)
    {
        IReadOnlyList<CommandLogEntry> commands = RunSwitchBack(hasStash, collectGarbage, beforeSwitchBack: null, afterSwitchBack: null, out int activationCount);
        AssertSwitchBackBudget(commands, activationCount);
    }

    private static IReadOnlyList<CommandLogEntry> RunSwitchBack(
        bool hasStash,
        bool collectGarbage,
        Action<string>? beforeSwitchBack,
        Action<FormBrowse>? afterSwitchBack,
        out int activationCount)
    {
        using ReferenceRepository repositoryA = CreateBudgetTestRepository(hasStash);
        using ReferenceRepository repositoryB = CreateBudgetTestRepository(hasStash: false);

        // The commits of both repositories must not have the same ids: the output of some commands is cached by commit
        repositoryB.CreateCommit("Commit B (HEAD)", "other content", "other_file.txt");
        ObjectId headA = ObjectId.Parse(repositoryA.CommitHash!);
        ObjectId headB = ObjectId.Parse(repositoryB.CommitHash!);

        string workingDirA = CopyRepository(repositoryA);
        string workingDirB = CopyRepository(repositoryB);

        // The form is opened with repository B: the module of the repository opened first is kept alive by 'commands'
        IServiceContainer serviceContainer = GlobalServiceContainer.CreateDefaultMockServiceContainer();
        GitModule moduleB = new(serviceContainer.GetRequiredService<IGitExecutorProvider>(), workingDirB);
        GitUICommands commands = new(serviceContainer, moduleB);
        IReadOnlyList<CommandLogEntry>? recordedCommands = null;
        int activationsDuringSwitch = 0;

        using ActivationCounter activationCounter = new();
        using UITestThreadExceptions threadExceptions = new();

        UITest.RunForm(
            showForm: () => commands.StartBrowseDialog(owner: null).Should().BeTrue(),
            async (FormBrowse form) =>
            {
                await WaitForRepositoryAsync(form, headB);

                form.SetWorkingDir(workingDirA);
                await WaitForRepositoryAsync(form, headA);

                form.SetWorkingDir(workingDirB);
                await WaitForRepositoryAsync(form, headB);

                if (collectGarbage)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }

                beforeSwitchBack?.Invoke(workingDirA);

                int activationsBefore = activationCounter.ActivationCount;
                CommandLog.Clear();
                form.SetWorkingDir(workingDirA);
                await WaitForRepositoryAsync(form, headA);

                recordedCommands = [.. CommandLog.Commands.Where(IsGitCommand)];
                activationsDuringSwitch = activationCounter.ActivationCount - activationsBefore;
                afterSwitchBack?.Invoke(form);
            });

        threadExceptions.ThrowIfAny();
        recordedCommands.Should().NotBeNull("CommandLog snapshot should have been captured before form closed");
        activationCount = activationsDuringSwitch;
        return recordedCommands!;
    }

    /// <summary>
    ///  Copies the repository, so that the module of <paramref name="repository"/> does not keep anything alive
    ///  (e.g. the git settings) which is shared by the modules of a working directory.
    /// </summary>
    private static string CopyRepository(ReferenceRepository repository)
    {
        string workingDir = repository.Module.WorkingDir.TrimEnd(Path.DirectorySeparatorChar);
        string copy = $"{workingDir}-copy";
        CopyDirectory(workingDir, copy);
        return copy;

        static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            }

            foreach (string directory in Directory.GetDirectories(source))
            {
                CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
            }
        }
    }

    private static async Task WaitForRepositoryAsync(FormBrowse form, ObjectId headId)
    {
        FormBrowse.TestAccessor formAccessor = form.GetTestAccessor();

        UITest.ProcessUntil(
            "HEAD of the repository selected",
            () => formAccessor.RevisionGrid.GetTestAccessor().IsDataLoadComplete
                && formAccessor.RevisionGrid.LatestSelectedRevision?.ObjectId == headId,
            maxMilliseconds: StepTimeoutMilliseconds);
        await AsyncTestHelper.JoinPendingOperationsAsync(TimeSpan.FromMilliseconds(StepTimeoutMilliseconds));

        NativeTreeView leftTreeView = formAccessor.RepoObjectsTree.GetTestAccessor().TreeView;
        UITest.ProcessUntil(
            "Left panel loaded",
            () => leftTreeView.Nodes.OfType<TreeNode>().FirstOrDefault(n => n.Text == TranslatedStrings.Tags)?.Nodes.Count >= 2,
            maxMilliseconds: StepTimeoutMilliseconds);
        UITest.ProcessUntil(
            "Commit message loaded",
            () => formAccessor.RevisionInfo.GetTestAccessor().CommitMessage.Text.Length > 0,
            maxMilliseconds: StepTimeoutMilliseconds);

        await AsyncTestHelper.JoinPendingOperationsAsync(TimeSpan.FromMilliseconds(StepTimeoutMilliseconds));
        UITest.ProcessEventsFor(IdleSettleMilliseconds);
        await AsyncTestHelper.JoinPendingOperationsAsync(TimeSpan.FromMilliseconds(StepTimeoutMilliseconds));
    }

    private static void AssertSwitchBackBudget(IReadOnlyList<CommandLogEntry> commands, int activationCount)
    {
        // Each activation during the switch (FormBrowse is already open) invalidates the git settings,
        // which reads the config again. Only these config re-reads are allowed in addition to the budgets.
        List<CommandLogEntry> configLists = [.. commands.Where(IsConfigList)];
        int allowance = Math.Min(activationCount, configLists.Count);
        int uiThreadAllowance = Math.Min(allowance, configLists.Count(c => c.IsOnMainThread));
        int totalProcesses = commands.Count;
        int uiThreadProcesses = commands.Count(c => c.IsOnMainThread);

        Console.WriteLine($"=== Git Process Switch Budget Report (Total: {totalProcesses}, UI: {uiThreadProcesses}, Activations: {activationCount}, ConfigList: {configLists.Count}, Allowance: {allowance}, UI allowance: {uiThreadAllowance}) ===");
        Console.WriteLine(FormatCommandLog(commands));

        uiThreadProcesses.Should().BeLessThanOrEqualTo(
            MaxUiThreadGitProcessesForSwitchBack + uiThreadAllowance,
            $"UI thread git process count exceeded budget.\n{FormatCommandLog(commands)}");
        totalProcesses.Should().BeLessThanOrEqualTo(
            MaxGitProcessesForSwitchBack + allowance,
            $"Total git process count exceeded budget.\n{FormatCommandLog(commands)}");
    }
}
