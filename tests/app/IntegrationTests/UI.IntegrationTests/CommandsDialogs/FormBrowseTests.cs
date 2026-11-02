using System.Runtime.CompilerServices;
using CommonTestUtils;
using GitCommands;
using GitCommands.Logging;
using GitExtensions.Extensibility.Git;
using GitUI;
using GitUI.CommandsDialogs;
using GitUIPluginInterfaces;

namespace GitExtensions.UITests.CommandsDialogs;

[Apartment(ApartmentState.STA)]
public class FormBrowseTests
{
    // Created once for the fixture
    private ReferenceRepository _referenceRepository = null!;

    // Created once for each test
    private GitUICommands _commands = null!;

    // Track the original setting value
    private bool _originalShowAuthorAvatarColumn;
    private bool _showAvailableDiffTools;

    [OneTimeSetUp]
    public void SetUpFixture()
    {
        // Remember the current settings...
        _originalShowAuthorAvatarColumn = AppSettings.ShowAuthorAvatarColumn;
        _showAvailableDiffTools = AppSettings.ShowAvailableDiffTools;

        // Stop loading custom diff tools
        AppSettings.ShowAvailableDiffTools = false;

        // We don't want avatars during tests, otherwise we will be attempting to download them from gravatar....
        AppSettings.ShowAuthorAvatarColumn = false;
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        AppSettings.ShowAuthorAvatarColumn = _originalShowAuthorAvatarColumn;
        AppSettings.ShowAvailableDiffTools = _showAvailableDiffTools;
    }

    [SetUp]
    public void SetUp()
    {
        _referenceRepository = new ReferenceRepository();

        _commands = new GitUICommands(GlobalServiceContainer.CreateDefaultMockServiceContainer(), _referenceRepository.Module);
    }

    [TearDown]
    public void TearDown()
    {
        _referenceRepository.Dispose();
    }

    [Test]
    public void Filters_should_behave_as_expected()
    {
        _referenceRepository.CreateCommit("Commit1", "Commit1");
        _referenceRepository.CreateBranch("Branch1", _referenceRepository.CommitHash!);
        _referenceRepository.CreateCommit("Commit2", "Commit2");
        _referenceRepository.CreateBranch("Branch2", _referenceRepository.CommitHash!);

        _referenceRepository.CreateCommit("head commit");

        _referenceRepository.CheckoutBranch("Branch1");

        bool reflogEnabled = AppSettings.ShowReflogReferences;
        bool branchFilterEnabled = AppSettings.BranchFilterEnabled;
        bool showCurrentBranchOnly = AppSettings.ShowCurrentBranchOnly;
        bool revisionGraphShowArtificialCommits = AppSettings.RevisionGraphShowArtificialCommits;
        AppSettings.ShowReflogReferences.Value = false;
        AppSettings.BranchFilterEnabled.Value = false;
        AppSettings.ShowCurrentBranchOnly.Value = false;
        AppSettings.RevisionGraphShowArtificialCommits = false;

        RunFormTest(
            form =>
            {
                try
                {
                    // 1. Cycle between "Show all branches" > "Show current branch" > "Show filterd branches"
                    // ------------------------------------------------------------------------------------------------

                    Console.WriteLine("Scenario 1: set 'Show all branches'");
                    WaitForRevisionsToBeLoaded(form);
                    // Assert
                    form.GetTestAccessor().RevisionGrid.GetTestAccessor().VisibleRevisionCount.Should().Be(4);
                    AppSettings.BranchFilterEnabled.Value.Should().BeFalse();
                    AppSettings.ShowCurrentBranchOnly.Value.Should().BeFalse();

                    Console.WriteLine("Scenario 1: set 'Show current branch'");
                    form.GetTestAccessor().ToolStripFilters.GetTestAccessor().tsmiShowBranchesCurrent.PerformClick();
                    WaitForRevisionsToBeLoaded(form);
                    // Assert
                    AppSettings.BranchFilterEnabled.Value.Should().BeFalse();
                    AppSettings.ShowCurrentBranchOnly.Value.Should().BeTrue();
                    form.GetTestAccessor().RevisionGrid.GetTestAccessor().VisibleRevisionCount.Should().Be(2);

                    Console.WriteLine("Scenario 1: set 'Show filtered branches'");
                    form.GetTestAccessor().ToolStripFilters.GetTestAccessor().tsmiShowBranchesFiltered.PerformClick();
                    WaitForRevisionsToBeLoaded(form);
                    // Assert
                    AppSettings.BranchFilterEnabled.Value.Should().BeTrue();
                    AppSettings.ShowCurrentBranchOnly.Value.Should().BeFalse();
                    form.GetTestAccessor().RevisionGrid.GetTestAccessor().VisibleRevisionCount.Should().Be(4);

                    // 2. Apply a branch filter
                    // ------------------------------------------------------------------------------------------------

                    Console.WriteLine("Scenario 2: apply branch filter 'Branch2'");
                    form.GetTestAccessor().ToolStripFilters.SetBranchFilter("Branch2");
                    WaitForRevisionsToBeLoaded(form);
                    // Assert
                    AppSettings.BranchFilterEnabled.Value.Should().BeTrue();
                    AppSettings.ShowCurrentBranchOnly.Value.Should().BeFalse();
                    form.GetTestAccessor().RevisionGrid.GetTestAccessor().VisibleRevisionCount.Should().Be(3);

                    Console.WriteLine("Scenario 2: set 'Show current branch'");
                    form.GetTestAccessor().ToolStripFilters.GetTestAccessor().tsmiShowBranchesCurrent.PerformClick();
                    WaitForRevisionsToBeLoaded(form);
                    // Assert
                    AppSettings.BranchFilterEnabled.Value.Should().BeFalse();
                    AppSettings.ShowCurrentBranchOnly.Value.Should().BeTrue();
                    form.GetTestAccessor().RevisionGrid.GetTestAccessor().VisibleRevisionCount.Should().Be(2);
                    // The filter text is still present
                    form.GetTestAccessor().ToolStripFilters.GetTestAccessor().tscboBranchFilter.Text.Should().Be("Branch2");

                    // 3. Switch to another repo - "Show current branch" must remain, filter text must be erased
                    // ------------------------------------------------------------------------------------------------

                    Console.WriteLine("Scenario 3: switch repo");
                    using ReferenceRepository repository = new();
                    form.SetWorkingDir(repository.Module.WorkingDir);
                    WaitForRevisionsToBeLoaded(form);
                    // Assert
                    AppSettings.BranchFilterEnabled.Value.Should().BeFalse();
                    AppSettings.ShowCurrentBranchOnly.Value.Should().BeTrue();
                    form.GetTestAccessor().ToolStripFilters.GetTestAccessor().tscboBranchFilter.Text.Should().BeEmpty();
                }
                finally
                {
                    AppSettings.ShowReflogReferences.Value = reflogEnabled;
                    AppSettings.BranchFilterEnabled.Value = branchFilterEnabled;
                    AppSettings.ShowCurrentBranchOnly.Value = showCurrentBranchOnly;
                    AppSettings.RevisionGraphShowArtificialCommits = revisionGraphShowArtificialCommits;
                }
            });
    }

    [TestCase("", "file.txt")]
    [TestCase("", "file with spaces.txt")]
    [TestCase("Dir with spaces", "file.txt")]
    [TestCase("Dir with spaces", "file with spaces.txt")]
    public void File_history_should_behave_as_expected(string fileRelativePath, string fileName)
    {
        using ReferenceRepository referenceRepository = new();
        GitUICommands commands = new(GlobalServiceContainer.CreateDefaultMockServiceContainer(), referenceRepository.Module);

        string revision1 = referenceRepository.CreateCommitRelative(fileRelativePath, fileName, $"Create '{fileName}' in directory '{fileRelativePath}'");
        string revision2 = referenceRepository.CreateCommitRelative(fileRelativePath, fileName, $"Update '{fileName}' in directory '{fileRelativePath}'");
        string revision3 = referenceRepository.CreateCommitRelative(fileRelativePath, fileName, $"Update '{fileName}' in directory '{fileRelativePath}' again");

        bool useBrowseForFileHistory = AppSettings.UseBrowseForFileHistory;

        AppSettings.UseBrowseForFileHistory.Value = true;

        UITest.RunForm(
            showForm: () => commands.GetTestAccessor().ShowFileHistoryDialog(Path.Combine(fileRelativePath, fileName)),
            (FormBrowse form) =>
            {
                try
                {
                    WaitForRevisionsToBeLoaded(form);

                    form.GetTestAccessor().RevisionGrid.GetRevision(ObjectId.Parse(revision1)).Should().NotBeNull();
                    form.GetTestAccessor().RevisionGrid.GetRevision(ObjectId.Parse(revision2)).Should().NotBeNull();
                    form.GetTestAccessor().RevisionGrid.GetRevision(ObjectId.Parse(revision3)).Should().NotBeNull();

                    return Task.CompletedTask;
                }
                finally
                {
                    AppSettings.UseBrowseForFileHistory.Value = useBrowseForFileHistory;
                }
            });
    }

    [Test]
    public void FileSelection_should_be_restored_after_refresh()
    {
        // create a commit with multiple files changed
        const string fileA = "fileA.txt";
        const string fileB = "fileB.txt";
        const string contentA = nameof(contentA);
        const string contentB = nameof(contentB);

        using ReferenceRepository referenceRepository = new();
        GitUICommands commands = new(GlobalServiceContainer.CreateDefaultMockServiceContainer(), referenceRepository.Module);
        referenceRepository.CreateCommit("multiple files",
            $"{contentA}\n{new string('A', 20000)}", fileA,
            $"{contentB}\n{new string('B', 20000)}", fileB);

        const int maxMilliseconds = 3_000;
        RunFormTest(
            async form =>
            {
                FormBrowse.TestAccessor ta = form.GetTestAccessor();
                ((TabControl)ta.DiffTabPage.Parent!).SelectedTab = ta.DiffTabPage;

                RevisionDiffControl.TestAccessor tadiff = ta.RevisionDiffControl.GetTestAccessor();
                FileStatusList fileStatusList = tadiff.DiffFiles;

                WaitForRevisionsToBeLoaded(form);

                IReadOnlyList<GitItemStatus> files = [];
                UITest.ProcessUntil("waiting for files", () => { return (files = fileStatusList.GitItemStatuses).Count == 2; }, maxMilliseconds);
                GitItemStatus fileToSelect = files[1];
                fileStatusList.SelectedGitItems = [fileToSelect];
                await VerifySelectionAsync();
                const int expectedLine = 2;
                tadiff.DiffText.GoToLine(expectedLine);

                // repeat: refresh and check selection
                for (int i = 0; i < 15; ++i)
                {
                    ta.RefreshRevisions();
                    WaitForRevisionsToBeLoaded(form);
                    await VerifySelectionAsync(expectedLine);
                }

                return;

                async Task VerifySelectionAsync(int expectedLine = 1)
                {
                    await Task.Delay(FileStatusList.SelectedIndexChangeThrottleDuration);
                    UITest.ProcessUntil("waiting for selection",
                        () => fileStatusList.SelectedGitItem?.Name == fileB
                                && tadiff.DiffText.GetText().StartsWith(contentB)
                                && tadiff.DiffText.CurrentFileLine == expectedLine,
                        maxMilliseconds);
                }
            },
            commands);
    }

    [Test]
    public void ShowStashes_starting_disabled_should_filter_as_expected()
    {
        using ReferenceRepository referenceRepository = new();
        GitUICommands commands = new(GlobalServiceContainer.CreateDefaultMockServiceContainer(), referenceRepository.Module);

        referenceRepository.CreateCommit("Commit1");
        referenceRepository.Stash("Stash1");
        referenceRepository.CreateCommit("Commit2");
        referenceRepository.Stash("Stash2");
        referenceRepository.CreateCommit("Commit3");

        bool showStashes = AppSettings.ShowStashes;
        bool revisionGraphShowArtificialCommits = AppSettings.RevisionGraphShowArtificialCommits;

        AppSettings.ShowStashes = false;
        AppSettings.RevisionGraphShowArtificialCommits = false;

        RunFormTest(
            form =>
            {
                try
                {
                    // 1. Check with ShowStashes disabled
                    Console.WriteLine("Scenario 1: set 'Show stashes' to false");
                    WaitForRevisionsToBeLoaded(form);
                    // Assert
                    AppSettings.ShowStashes.Should().BeFalse();
                    form.GetTestAccessor().RevisionGrid.GetTestAccessor().VisibleRevisionCount.Should().Be(4);

                    // 2. Change ShowStashes to enabled
                    Console.WriteLine("Scenario 2: change 'Show stashes' to enabled");
                    form.GetTestAccessor().RevisionGrid.ToggleShowStashes();
                    WaitForRevisionsToBeLoaded(form);
                    // Assert
                    AppSettings.ShowStashes.Should().BeTrue();
                    form.GetTestAccessor().RevisionGrid.GetTestAccessor().VisibleRevisionCount.Should().Be(7);
                }
                finally
                {
                    AppSettings.ShowStashes = showStashes;
                    AppSettings.RevisionGraphShowArtificialCommits = revisionGraphShowArtificialCommits;
                }
            },
            commands);
    }

    [Test]
    public void ShowStashes_starting_enabled_should_filter_as_expected()
    {
        using ReferenceRepository referenceRepository = new();
        GitUICommands commands = new(GlobalServiceContainer.CreateDefaultMockServiceContainer(), referenceRepository.Module);

        referenceRepository.CreateCommit("Commit1");
        referenceRepository.Stash("Stash1");
        referenceRepository.CreateCommit("Commit2");
        referenceRepository.Stash("Stash2");
        referenceRepository.CreateCommit("Commit3");

        bool showStashes = AppSettings.ShowStashes;
        bool revisionGraphShowArtificialCommits = AppSettings.RevisionGraphShowArtificialCommits;

        AppSettings.ShowStashes = true;
        AppSettings.RevisionGraphShowArtificialCommits = false;
        RunFormTest(
            form =>
            {
                try
                {
                    // 1. Check with ShowStashes enabled
                    Console.WriteLine("Scenario 1: set 'Show stash' to true");
                    WaitForRevisionsToBeLoaded(form);
                    // Assert
                    AppSettings.ShowStashes.Should().BeTrue();
                    form.GetTestAccessor().RevisionGrid.GetTestAccessor().VisibleRevisionCount.Should().Be(7);

                    // 2. Change ShowStashes to disabled
                    Console.WriteLine("Scenario 2: change 'Show stash' to disabled");
                    form.GetTestAccessor().RevisionGrid.ToggleShowStashes();
                    WaitForRevisionsToBeLoaded(form);
                    // Assert
                    AppSettings.ShowStashes.Should().BeFalse();
                    // https://github.com/gitextensions/gitextensions/issues/10170
                    // This test occasionaly fails with 3 visible revisions
                    form.GetTestAccessor().RevisionGrid.GetTestAccessor().VisibleRevisionCount.Should().Be(4);
                }
                finally
                {
                    AppSettings.ShowStashes = showStashes;
                    AppSettings.RevisionGraphShowArtificialCommits = revisionGraphShowArtificialCommits;
                }
            },
            commands);
    }

    [Test]
    public void Stashes_should_be_visible_in_grid_and_left_panel_when_repository_has_stash()
    {
        using ReferenceRepository referenceRepository = new();
        GitUICommands commands = new(GlobalServiceContainer.CreateDefaultMockServiceContainer(), referenceRepository.Module);

        referenceRepository.CreateCommit("Commit1");
        referenceRepository.Stash("Stash1");

        bool showStashes = AppSettings.ShowStashes;
        bool repoObjectsTreeShowStashes = AppSettings.RepoObjectsTreeShowStashes;

        AppSettings.ShowStashes = true;
        AppSettings.RepoObjectsTreeShowStashes = true;

        RunFormTest(
            form =>
            {
                try
                {
                    WaitForRevisionsToBeLoaded(form);

                    // Verify stash is visible in revision grid
                    form.GetTestAccessor().RevisionGrid.GetTestAccessor().VisibleRevisionCount.Should().BeGreaterThan(1);

                    // Verify stash is visible in left panel
                    UITest.ProcessUntil(
                        "waiting for stash node in left panel",
                        () =>
                        {
                            TreeNode? stashesRoot = form.GetTestAccessor().RepoObjectsTree.GetTestAccessor().TreeView.Nodes
                                .Find(TranslatedStrings.Stashes, searchAllChildren: true)
                                .FirstOrDefault();
                            return stashesRoot is not null && stashesRoot.Nodes.Count > 0;
                        });

                    TreeNode? stashRootNode = form.GetTestAccessor().RepoObjectsTree.GetTestAccessor().TreeView.Nodes
                        .Find(TranslatedStrings.Stashes, searchAllChildren: true)
                        .FirstOrDefault();
                    stashRootNode.Should().NotBeNull();
                    stashRootNode!.Nodes.Count.Should().Be(1);
                    stashRootNode.Nodes[0].Text.Should().Contain("Stash1");
                }
                finally
                {
                    AppSettings.ShowStashes = showStashes;
                    AppSettings.RepoObjectsTreeShowStashes = repoObjectsTreeShowStashes;
                }
            },
            commands);
    }

    [Test]
    public void SetWorkingDir_should_reload_commit_info_for_auto_selected_revision_after_repository_switch()
    {
        using (CommitInfoPanelTestSettingsScope.ForBrowseRepositorySwitch())
        {
            using GitModuleTestHelper repoA = new("formBrowseRepoSwitchA");
            using GitModuleTestHelper repoB = new("formBrowseRepoSwitchB");
            CreateCommitWithSubject(repoA, "REPO_A_HEAD subject");
            CreateCommitWithSubject(repoB, "REPO_B_HEAD subject");

            GitUICommands commandsA = new(GlobalServiceContainer.CreateDefaultMockServiceContainer(), repoA.Module);

            RunFormTest(
                async form =>
                {
                    FormBrowse.TestAccessor ta = form.GetTestAccessor();
                    ta.CommitInfoTabControl.SelectedTab = ta.CommitInfoTabPage;

                    WaitForRevisionsToBeLoaded(form);
                    await AsyncTestHelper.JoinPendingOperationsAsync(AsyncTestHelper.UnexpectedTimeout);
                    WaitForCommitMessageContaining(form, "REPO_A_HEAD");

                    ta.SetWorkingDir(repoB.Module.WorkingDir);
                    WaitForRevisionsToBeLoaded(form);
                    await AsyncTestHelper.JoinPendingOperationsAsync(AsyncTestHelper.UnexpectedTimeout);
                    WaitForCommitMessageAfterRepositorySwitch(form, "REPO_B_HEAD", "REPO_A_HEAD");
                },
                commandsA);
        }
    }

    [Test]
    public void FormBrowse_process_budget_should_not_spawn_duplicate_git_processes_during_tab_switching()
    {
        using (CommitInfoPanelTestSettingsScope.ForBrowseRepositorySwitch())
        {
            using GitModuleTestHelper repo = new("formBrowseProcessBudget");
            CreateCommitWithSubject(repo, "HEAD commit for budget test");

            GitUICommands commands = new(GlobalServiceContainer.CreateDefaultMockServiceContainer(), repo.Module);

            CommandLog.Clear();

            RunFormTest(
                form =>
                {
                    FormBrowse.TestAccessor ta = form.GetTestAccessor();

                    WaitForRevisionsToBeLoaded(form);

                    // 1. CommitInfo tab
                    ta.CommitInfoTabControl.SelectedTab = ta.CommitInfoTabPage;
                    UITest.ProcessUntil("commit info loaded", () => ta.RevisionInfo.GetTestAccessor().CommitMessage.Text.Contains("HEAD commit for budget test", StringComparison.Ordinal), maxMilliseconds: 5000);

                    // 2. Diff tab
                    ta.CommitInfoTabControl.SelectedTab = ta.DiffTabPage;
                    UITest.ProcessUntil("diff tab selected", () => ta.CommitInfoTabControl.SelectedTab == ta.DiffTabPage, maxMilliseconds: 2000);

                    // 3. CommitInfo tab
                    ta.CommitInfoTabControl.SelectedTab = ta.CommitInfoTabPage;
                    UITest.ProcessUntil("commit info selected again", () => ta.CommitInfoTabControl.SelectedTab == ta.CommitInfoTabPage, maxMilliseconds: 2000);

                    List<CommandLogEntry> commandList = CommandLog.Commands.ToList();

                    // 1. branch ... --merged: 1 count
                    int mergedBranchCount = commandList.Count(c => c.Arguments.Contains("branch") && c.Arguments.Contains("--merged"));
                    mergedBranchCount.Should().Be(1, "branch --merged should be called exactly once");

                    // 2. for-each-ref --sort=-taggerdate: 0 count
                    int sortedTagsCount = commandList.Count(c => c.Arguments.Contains("for-each-ref") && c.Arguments.Contains("-taggerdate"));
                    sortedTagsCount.Should().Be(0, "for-each-ref --sort=-taggerdate should not be called (CommitInfo uses host refs)");

                    // 3. ahead/behind for-each-ref ... refs/heads/: 1 count
                    int aheadBehindCount = commandList.Count(c => c.Arguments.Contains("for-each-ref") && c.Arguments.Contains("refs/heads/"));
                    aheadBehindCount.Should().Be(1, "ahead/behind for-each-ref refs/heads/ should be called exactly once");

                    // 4. for-each-ref all refs: 1 count
                    int allRefsCount = commandList.Count(c => c.Arguments.Contains("for-each-ref") && !c.Arguments.Contains("refs/heads/") && !c.Arguments.Contains("-taggerdate"));
                    allRefsCount.Should().Be(1, "for-each-ref for all refs should be called exactly once");
                },
                commands);
        }
    }

    private static void CreateCommitWithSubject(GitModuleTestHelper helper, string subject)
    {
        helper.Module.GitExecutable.GetOutput($@"commit --allow-empty -m ""{subject}""");
    }

    private static void RunFormTest(Action<FormBrowse> testDriver, GitUICommands commands)
    {
        UITest.RunForm(
            showForm: () => commands.StartBrowseDialog(owner: null).Should().BeTrue(),
            (FormBrowse form) =>
            {
                testDriver(form);
                return Task.CompletedTask;
            });
    }

    private void RunFormTest(Action<FormBrowse> testDriver)
    {
        RunFormTest(
            form =>
            {
                testDriver(form);
                return Task.CompletedTask;
            });
    }

    private void RunFormTest(Func<FormBrowse, Task> testDriverAsync, GitUICommands? commands = null)
    {
        UITest.RunForm(
            showForm: () => (commands ?? _commands).StartBrowseDialog(owner: null).Should().BeTrue(),
            testDriverAsync);
    }

    private static void WaitForRevisionsToBeLoaded(FormBrowse form, [CallerMemberName] string caller = "")
    {
        UITest.ProcessUntil($"{caller} loading revisions", () => form.GetTestAccessor().RevisionGrid.GetTestAccessor().IsDataLoadComplete, maxMilliseconds: 10_000);
    }

    private static void WaitForCommitMessageContaining(FormBrowse form, string subjectFragment, [CallerMemberName] string caller = "")
    {
        GitUI.CommitInfo.CommitInfo.TestAccessor commitInfo = form.GetTestAccessor().RevisionInfo.GetTestAccessor();
        UITest.ProcessUntil(
            $"{caller} commit message contains '{subjectFragment}'",
            () => commitInfo.CommitMessage.Text.Contains(subjectFragment, StringComparison.Ordinal),
            maxMilliseconds: 10_000);
    }

    private static void WaitForCommitMessageAfterRepositorySwitch(FormBrowse form, string expectedSubjectFragment, string staleSubjectFragment)
    {
        GitUI.CommitInfo.CommitInfo.TestAccessor commitInfo = form.GetTestAccessor().RevisionInfo.GetTestAccessor();
        UITest.ProcessUntil(
            "commit message after repository switch",
            () =>
            {
                string text = commitInfo.CommitMessage.Text;
                return text.Contains(expectedSubjectFragment, StringComparison.Ordinal)
                    && !text.Contains(staleSubjectFragment, StringComparison.Ordinal);
            },
            maxMilliseconds: 10_000);
    }
}
