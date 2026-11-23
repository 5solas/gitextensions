using System.ComponentModel.Design;
using CommonTestUtils;
using GitCommands;
using GitCommands.Logging;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitUI;
using GitUI.UserControls.RevisionGrid;
using GitUI.UserControls.RevisionGrid.Reachability;
using GitUIPluginInterfaces;
using NSubstitute;

namespace GitExtensions.UITests.UserControls.CommitInfo;

/// <summary>
///  CommitInfo computes the branches and tags containing the commit and the describe info from the
///  reachability index of the revision grid instead of running git.
/// </summary>
[Apartment(ApartmentState.STA)]
[NonParallelizable]
public class CommitInfoReachabilityTests
{
    private static readonly string[] _replacedCommands = ["branch -a --contains", "tag --contains", "describe --tags"];

    private ReferenceRepository _referenceRepository = null!;
    private GitUICommands _commands = null!;
    private bool _showContainedInBranchesLocal;
    private bool _showContainedInBranchesRemote;
    private bool _showContainedInBranchesRemoteIfNoLocal;
    private bool _showContainedInTags;
    private bool _showTagThisCommitDerivesFrom;
    private bool _showAnnotatedTagsMessages;

    [SetUp]
    public void SetUp()
    {
        _showContainedInBranchesLocal = AppSettings.CommitInfoShowContainedInBranchesLocal;
        _showContainedInBranchesRemote = AppSettings.CommitInfoShowContainedInBranchesRemote;
        _showContainedInBranchesRemoteIfNoLocal = AppSettings.CommitInfoShowContainedInBranchesRemoteIfNoLocal;
        _showContainedInTags = AppSettings.CommitInfoShowContainedInTags;
        _showTagThisCommitDerivesFrom = AppSettings.CommitInfoShowTagThisCommitDerivesFrom;
        _showAnnotatedTagsMessages = AppSettings.ShowAnnotatedTagsMessages;

        AppSettings.CommitInfoShowContainedInBranchesLocal = true;
        AppSettings.CommitInfoShowContainedInBranchesRemote = true;
        AppSettings.CommitInfoShowContainedInBranchesRemoteIfNoLocal = false;
        AppSettings.CommitInfoShowContainedInTags = true;
        AppSettings.CommitInfoShowTagThisCommitDerivesFrom = true;
        AppSettings.ShowAnnotatedTagsMessages = false;

        _referenceRepository = new ReferenceRepository(createCommit: false);
        string commit1 = _referenceRepository.CreateCommit("Commit 1", "content 1", "file1.txt");
        _referenceRepository.CreateTag("v1.0-light", commit1);
        _referenceRepository.CreateBranch("feature1", commit1);
        string commit2 = _referenceRepository.CreateCommit("Commit 2", "content 2", "file2.txt");
        _referenceRepository.CreateAnnotatedTag("v1.0", commit2, "Release v1.0");
        _referenceRepository.CreateCommit("Commit 3", "content 3", "file3.txt");
        _referenceRepository.Module.GitExecutable.GetOutput($"update-ref refs/remotes/origin/master {commit1}");

        ServiceContainer serviceContainer = GlobalServiceContainer.CreateDefaultMockServiceContainer();
        _commands = new GitUICommands(serviceContainer, _referenceRepository.Module);
    }

    [TearDown]
    public void TearDown()
    {
        AppSettings.CommitInfoShowContainedInBranchesLocal = _showContainedInBranchesLocal;
        AppSettings.CommitInfoShowContainedInBranchesRemote = _showContainedInBranchesRemote;
        AppSettings.CommitInfoShowContainedInBranchesRemoteIfNoLocal = _showContainedInBranchesRemoteIfNoLocal;
        AppSettings.CommitInfoShowContainedInTags = _showContainedInTags;
        AppSettings.CommitInfoShowTagThisCommitDerivesFrom = _showTagThisCommitDerivesFrom;
        AppSettings.ShowAnnotatedTagsMessages = _showAnnotatedTagsMessages;
        _referenceRepository.Dispose();
    }

    [Test]
    public void CommitInfo_should_show_the_same_info_without_git_when_the_index_is_available()
    {
        GitModule module = _referenceRepository.Module;
        CommitReachabilityTracker tracker = BuildTracker(module, usable: true);
        tracker.TryGetIndex(out _).Should().BeTrue();

        foreach (string commit in module.GitExecutable.GetOutput("rev-list --all").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            GitRevision revision = new RevisionReader(module).GetRevision(commit, hasNotes: false, throwOnError: true, CancellationToken.None)!;

            (string gitText, IReadOnlyList<string> gitCommands) = ShowRevisionInfo(revision, provider: null);
            gitCommands.Should().Contain(c => c.Contains("tag --contains"), "the reference run uses git");

            (string indexText, IReadOnlyList<string> indexCommands) = ShowRevisionInfo(revision, provider: tracker);

            indexText.Should().Be(gitText, $"commit {commit}");
            indexCommands.Should().NotContain(c => _replacedCommands.Any(c.Contains));
        }
    }

    [Test]
    public void CommitInfo_should_use_git_when_the_index_is_not_usable()
    {
        GitModule module = _referenceRepository.Module;
        CommitReachabilityTracker tracker = BuildTracker(module, usable: false);
        GitRevision revision = new RevisionReader(module).GetRevision(module.GetCurrentCheckout().ToString(), hasNotes: false, throwOnError: true, CancellationToken.None)!;

        (_, IReadOnlyList<string> commands) = ShowRevisionInfo(revision, provider: tracker);

        foreach (string replacedCommand in _replacedCommands)
        {
            commands.Should().Contain(c => c.Contains(replacedCommand));
        }
    }

    private static CommitReachabilityTracker BuildTracker(GitModule module, bool usable)
    {
        CommitReachabilityTracker tracker = new();
        tracker.Begin(1, new ReachabilityLoadOptions(usable, CommitsLimit: 0), () => RefSnapshot.Read(module.GetRefs(RefsFilter.NoFilter), module.WorkingDirGitDir, module.GitCommonDirectory, module.GetEffectiveSetting("tag.sort")));
        FilterInfo filter = new();
        new RevisionReader(module).GetLog(
            new RevisionObserver(revisions => tracker.Add(1, revisions)),
            filter.GetRevisionFilter(new Lazy<ObjectId>(module.GetCurrentCheckout)),
            pathFilter: "",
            hasNotes: false,
            autostashLabel: "autostash",
            CancellationToken.None);
        tracker.Complete(1);
        return tracker;
    }

    private (string Text, IReadOnlyList<string> Commands) ShowRevisionInfo(GitRevision revision, ICommitReachabilityProvider? provider)
    {
        IGitUICommandsSource uiCommandsSource = Substitute.For<IGitUICommandsSource>();
        uiCommandsSource.UICommands.Returns(_ => _commands);
        string text = "";
        IReadOnlyList<string> commands = [];

        UITest.RunControl(
            createControl: form =>
            {
                form.Size = new(600, 480);
                return new GitUI.CommitInfo.CommitInfo
                {
                    Dock = DockStyle.Fill,
                    Parent = form,
                    ShowBranchesAsLinks = true,
                    UICommandsSource = uiCommandsSource,
                };
            },
            runTestAsync: async commitInfo =>
            {
                await AsyncTestHelper.JoinPendingOperationsAsync(AsyncTestHelper.UnexpectedTimeout);
                commitInfo.SetReachabilityProvider(provider);
                CommandLog.Clear();

                commitInfo.SetRevisionWithChildren(revision, children: null);
                await AsyncTestHelper.JoinPendingOperationsAsync(AsyncTestHelper.UnexpectedTimeout);

                text = commitInfo.GetTestAccessor().RevisionInfo.Text;
                commands = [.. CommandLog.Commands.Select(c => c.Arguments)];
            });

        text.Should().NotBeEmpty();
        return (text, commands);
    }

    private sealed class RevisionObserver(Action<IReadOnlyList<GitRevision>> onNext) : IObserver<IReadOnlyList<GitRevision>>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error) => throw error;

        public void OnNext(IReadOnlyList<GitRevision> value) => onNext(value);
    }
}
