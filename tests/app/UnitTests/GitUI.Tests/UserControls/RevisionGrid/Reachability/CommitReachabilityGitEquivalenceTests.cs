using System.Diagnostics;
using System.Text;
using CommonTestUtils;
using GitCommands;
using GitCommands.Git;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitUI;
using GitUI.UserControls.RevisionGrid;
using GitUI.UserControls.RevisionGrid.Reachability;
using GitUIPluginInterfaces;

namespace GitUITests.UserControls.RevisionGrid.Reachability;

/// <summary>
///  Compares the in-memory results of <see cref="CommitReachabilityIndex"/> with the git commands they replace.
/// </summary>
[TestFixture]
[NonParallelizable]
public class CommitReachabilityGitEquivalenceTests
{
    private static readonly (bool GetLocal, bool GetRemote)[] _branchQueries = [(true, true), (true, false), (false, true)];

    private GitModuleTestHelper _helper = null!;
    private Dictionary<string, string> _commits = null!;
    private int _time;

    [SetUp]
    public void SetUp()
    {
        _helper = new GitModuleTestHelper();
        _commits = [];
        _time = 1_700_000_000;
        CreateRepository();
    }

    [TearDown]
    public void TearDown()
    {
        _helper.Dispose();
    }

    [Test]
    public void Attached_head_results_should_equal_git()
    {
        CommitReachabilityIndex index = BuildIndex(_helper.Module);

        index.Refs.Head.Should().Be(HeadState.Attached);
        index.Refs.Branches.Select(b => b.Key).Should().Contain("refs/remotes/origin/HEAD", "origin/HEAD is removed by GE but listed by git");

        HashSet<string> describeFallbacks = AssertEqualToGit(_helper.Module, index, expectBranchFallbackFor: []);

        // The nearest tagged commit has two annotated tags (f1; m1 with rel-1 and the nested tag), the tagger dates decide.
        describeFallbacks.Should().BeEquivalentTo([_commits["f1"], _commits["f2"], _commits["m1"], _commits["c3"], _commits["oc"]]);
        index.Describe(ObjectId.Parse(_commits["s2"])).Should().Be(("v1", "3"));
    }

    [Test]
    public void Packed_refs_results_should_equal_git()
    {
        Git("update-ref --no-deref refs/remotes/fork/HEAD " + _commits["c2"]);
        Git("update-ref refs/remotes/fork/dev " + _commits["c2"]);
        Git("pack-refs --all");

        CommitReachabilityIndex index = BuildIndex(_helper.Module);

        index.Refs.Branches.Select(b => b.Key).Should().Contain(["refs/remotes/origin/HEAD", "refs/remotes/fork/HEAD"]);
        AssertEqualToGit(_helper.Module, index, expectBranchFallbackFor: []);
    }

    [TestCase("^{tree}")]
    [TestCase(":blob.txt")]
    public void Annotated_tag_of_another_object_should_fall_back_for_tags(string target)
    {
        File.WriteAllText(Path.Combine(_helper.Module.WorkingDir, "blob.txt"), "blob");
        Git("add blob.txt");
        Commit("with-blob");
        Git($"tag -a -m other other-object {_commits["with-blob"]}{target}");

        CommitReachabilityIndex index = BuildIndex(_helper.Module);

        // Everything else is answered and equal to git
        AssertEqualToGit(_helper.Module, index, expectBranchFallbackFor: [], expectTagFallback: true);
    }

    [Test]
    public void Detached_head_should_fall_back_for_branches_containing_head()
    {
        Git("checkout --detach " + _commits["c2"]);

        CommitReachabilityIndex index = BuildIndex(_helper.Module);

        index.Refs.Head.Should().Be(HeadState.Detached(ObjectId.Parse(_commits["c2"])));
        AssertEqualToGit(_helper.Module, index, expectBranchFallbackFor: [_commits["c1"], _commits["c2"]]);
    }

    [Test]
    public void Shallow_clone_results_should_equal_git()
    {
        string clonePath = Path.Combine(_helper.TemporaryPath, "shallow");
        Git($"clone --depth 2 --no-single-branch \"file:///{_helper.Module.WorkingDir.TrimEnd('/', '\\').ToPosixPath()}\" \"{clonePath.ToPosixPath()}\"");
        GitModule clone = new(new GitExecutorProvider(new GitDirectoryResolver()), clonePath);

        File.Exists(Path.Combine(clone.WorkingDirGitDir, "shallow")).Should().BeTrue();
        CommitReachabilityIndex index = BuildIndex(clone);

        AssertEqualToGit(clone, index, expectBranchFallbackFor: []);
    }

    [Test]
    public void Replaced_commit_results_should_equal_git()
    {
        // c2 gets f2 as additional parent
        Git($"replace --graft {_commits["c2"]} {_commits["c1"]} {_commits["f2"]}");

        CommitReachabilityIndex index = BuildIndex(_helper.Module);

        AssertEqualToGit(_helper.Module, index, expectBranchFallbackFor: []);
    }

    private static CommitReachabilityIndex BuildIndex(GitModule module)
    {
        FilterInfo filter = new();
        ReachabilityLoadOptions options = ReachabilityLoadOptions.FromFilter(filter);
        options.IsUsable.Should().BeTrue();

        CommitReachabilityTracker tracker = new();
        tracker.Begin(1, options, () => RefSnapshot.Read(module.GetRefs(RefsFilter.NoFilter), module.WorkingDirGitDir, module.GitCommonDirectory, module.GetEffectiveSetting("tag.sort")));

        RevisionObserver observer = new(revisions => tracker.Add(1, revisions));
        new RevisionReader(module).GetLog(
            observer,
            filter.GetRevisionFilter(new Lazy<ObjectId>(module.GetCurrentCheckout)),
            pathFilter: "",
            hasNotes: false,
            autostashLabel: "autostash",
            CancellationToken.None);
        observer.IsCompleted.Should().BeTrue();

        tracker.Complete(1);
        tracker.TryGetIndex(out CommitReachabilityIndex? index).Should().BeTrue();
        return index!;
    }

    private static HashSet<string> AssertEqualToGit(GitModule module, CommitReachabilityIndex index, string[] expectBranchFallbackFor, bool expectTagFallback = false)
    {
        List<string> commits = module.GitExecutable.GetOutput("rev-list --all")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        commits.Should().HaveCount(index.CommitCount);

        GitDescribeProvider describeProvider = new(() => module);
        HashSet<string> describeFallbacks = [];
        HashSet<string> branchFallbacks = [];
        foreach (string sha in commits)
        {
            ObjectId commit = ObjectId.Parse(sha);

            foreach ((bool getLocal, bool getRemote) in _branchQueries)
            {
                IReadOnlyList<string> expected = module.GetAllBranchesWhichContainGivenCommit(commit, getLocal, getRemote, CancellationToken.None);
                IReadOnlyList<string>? actual = index.GetBranchesContaining(commit, getLocal, getRemote);
                if (actual is null)
                {
                    branchFallbacks.Add(sha);
                    expected.Should().Contain(b => b.StartsWith("(HEAD detached"), $"fallback for {sha} must be needed");
                    continue;
                }

                actual.Should().Equal(expected, $"branches containing {sha} (local={getLocal}, remote={getRemote})");
            }

            IReadOnlyList<string>? tags = index.GetTagsContaining(commit);
            if (expectTagFallback)
            {
                tags.Should().BeNull($"git must be used for the tags containing {sha}");
            }
            else
            {
                tags.Should().Equal(module.GetAllTagsWhichContainGivenCommit(commit, CancellationToken.None), $"tags containing {sha}");
            }

            string[] gitMerged = ThreadHelper.JoinableTaskFactory.Run(
                () => module.GetMergedBranchesAsync(includeRemote: true, fullRefname: true, commit: sha, CancellationToken.None));

            // RepoObjectsTree only matches the refs/heads/ and refs/remotes/ names, not "(HEAD detached at ...)"
            index.GetMergedBranches(commit).Should().BeEquivalentTo(gitMerged.Where(b => b.StartsWith("refs/")), $"branches merged into {sha}");

            (string PrecedingTag, string CommitCount)? describe = index.Describe(commit);
            if (describe is null)
            {
                describeFallbacks.Add(sha);
            }
            else
            {
                describe.Should().Be(describeProvider.Get(commit), $"describe {sha}");
            }
        }

        branchFallbacks.Should().BeEquivalentTo(expectBranchFallbackFor);
        return describeFallbacks;
    }

    private void CreateRepository()
    {
        // master: c1, c2, m1 (merges feature f1, f2 forked from c1), c3; origin/master and origin/HEAD at m1
        // octo: octopus merge oc of m1, o1 and o2 (o1 and o2 forked from c1)
        // side: s1 (from c1), merge sm of o1 (tagged, nearer than v1 at c1 on the first-parent chain), s2
        // orphan: second root r1, r2 without tags
        Commit("c1");
        Commit("c2");
        Git("branch wt-branch");
        Git("checkout -b feature " + _commits["c1"]);
        Commit("f1");
        Commit("f2");
        Git("checkout master");
        Merge("m1", "feature");
        Git("checkout -b o1 " + _commits["c1"]);
        Commit("o1");
        Git("checkout -b o2 " + _commits["c1"]);
        Commit("o2");
        Git("checkout -b octo " + _commits["m1"]);
        Merge("oc", "o1 o2");
        Git("checkout -b side " + _commits["c1"]);
        Commit("s1");
        Merge("sm", "o1");
        Commit("s2");
        Git("checkout --orphan orphan");
        Commit("r1");
        Commit("r2");
        Git("checkout master");
        Commit("c3");
        Git("branch remotes/foo " + _commits["f1"]);

        // Remote branches, origin/HEAD is a symbolic ref
        Git("update-ref refs/remotes/origin/master " + _commits["m1"]);
        Git("update-ref refs/remotes/origin/feature " + _commits["f2"]);
        Git("symbolic-ref refs/remotes/origin/HEAD refs/remotes/origin/master");
        Git("update-ref refs/remotes/upstream/dev " + _commits["o1"]);

        // Branch checked out in another worktree ("+ wt-branch")
        Git($"worktree add \"{Path.Combine(_helper.TemporaryPath, "wt").ToPosixPath()}\" wt-branch");

        // Tags
        Tag("v1", "c1", annotated: true);
        Tag("f1-older", "f1", annotated: true);
        Tag("f1-newer", "f1", annotated: true);
        Tag("lw-b", "c2", annotated: false);
        Tag("lw-a", "c2", annotated: false);
        Tag("rel-1", "m1", annotated: true);
        Tag("rel-1-lw", "m1", annotated: false);
        Tag("o-light", "o1", annotated: false);
        Tag("o-annotated", "o1", annotated: true);
        Git("tag -a -m nested nested rel-1");

        // Only a tag of a (deleted) tag points to o2
        Git("tag -a -m inner inner-o2 " + _commits["o2"]);
        Git("tag -a -m outer outer-o2 inner-o2");
        Git("tag -d inner-o2");
        Git("tag tree-light " + _commits["c3"] + "^{tree}");
    }

    private void Commit(string name)
    {
        Git($"commit --allow-empty -m {name}", Dates());
        _commits[name] = Git("rev-parse HEAD").Trim();
    }

    private void Merge(string name, string branches)
    {
        Git($"merge --no-ff --no-edit -m {name} {branches}", Dates());
        _commits[name] = Git("rev-parse HEAD").Trim();
    }

    private void Tag(string tag, string commit, bool annotated)
    {
        Git(annotated ? $"tag -a -m {tag} {tag} {_commits[commit]}" : $"tag {tag} {_commits[commit]}", Dates());
    }

    private Dictionary<string, string> Dates()
    {
        string date = $"@{_time += 60} +0000";
        return new Dictionary<string, string> { ["GIT_AUTHOR_DATE"] = date, ["GIT_COMMITTER_DATE"] = date };
    }

    private string Git(string arguments, Dictionary<string, string>? environment = null)
    {
        ProcessStartInfo startInfo = new(AppSettings.GitCommand, arguments)
        {
            WorkingDirectory = _helper.Module.WorkingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach ((string key, string value) in environment ?? [])
        {
            startInfo.Environment[key] = value;
        }

        StringBuilder error = new();
        using Process process = new() { StartInfo = startInfo };
        process.ErrorDataReceived += (_, e) => error.AppendLine(e.Data);
        process.Start();
        process.BeginErrorReadLine();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, $"git {arguments}: {error}");
        return output;
    }

    private sealed class RevisionObserver(Action<IReadOnlyList<GitRevision>> onNext) : IObserver<IReadOnlyList<GitRevision>>
    {
        public bool IsCompleted { get; private set; }

        public void OnCompleted() => IsCompleted = true;

        public void OnError(Exception error) => throw error;

        public void OnNext(IReadOnlyList<GitRevision> value) => onNext(value);
    }
}
