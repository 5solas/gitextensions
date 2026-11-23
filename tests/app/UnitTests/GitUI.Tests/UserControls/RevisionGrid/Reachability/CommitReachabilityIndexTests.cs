using GitExtensions.Extensibility.Git;
using GitUI.UserControls.RevisionGrid.Reachability;
using GitUIPluginInterfaces;

namespace GitUITests.UserControls.RevisionGrid.Reachability;

[TestFixture]
public class CommitReachabilityIndexTests
{
    private readonly Dictionary<string, ObjectId> _ids = [];

    [SetUp]
    public void SetUp()
    {
        _ids.Clear();
    }

    [Test]
    public void Contains_and_merged_should_follow_all_parents_of_merges()
    {
        // A <- B <- M, A <- C <- M (M merges C into B)
        CommitReachabilityIndexBuilder builder = new();
        Add(builder, "M", "B", "C");
        Add(builder, "B", "A");
        Add(builder, "C", "A");
        Add(builder, "A");
        CommitReachabilityIndex index = builder.Build(Refs(("refs/heads/main", "M"), ("refs/heads/feature", "C")));

        index.IsComplete.Should().BeTrue();
        index.GetBranchesContaining(Id("B"), getLocal: true, getRemote: false).Should().Equal("main");
        index.GetBranchesContaining(Id("C"), getLocal: true, getRemote: false).Should().Equal("feature", "main");
        index.GetBranchesContaining(Id("A"), getLocal: true, getRemote: false).Should().Equal("feature", "main");
        index.GetMergedBranches(Id("M")).Should().BeEquivalentTo("refs/heads/main", "refs/heads/feature");
        index.GetMergedBranches(Id("C")).Should().BeEquivalentTo("refs/heads/feature");
        index.GetMergedBranches(Id("B")).Should().BeEmpty();
        index.CanGetMergedBranches(Id("B")).Should().BeTrue();
    }

    [Test]
    public void Contains_should_handle_multiple_roots()
    {
        CommitReachabilityIndexBuilder builder = new();
        Add(builder, "Y", "R2");
        Add(builder, "X", "R1");
        Add(builder, "R2");
        Add(builder, "R1");
        CommitReachabilityIndex index = builder.Build(Refs(("refs/heads/b1", "X"), ("refs/heads/b2", "Y"), ("refs/tags/t2", "R2")));

        index.IsComplete.Should().BeTrue();
        index.GetBranchesContaining(Id("R1"), getLocal: true, getRemote: true).Should().Equal("b1");
        index.GetBranchesContaining(Id("R2"), getLocal: true, getRemote: true).Should().Equal("b2");
        index.GetMergedBranches(Id("Y")).Should().BeEquivalentTo("refs/heads/b2");
        index.GetTagsContaining(Id("R1")).Should().BeEmpty();
        index.Describe(Id("Y")).Should().Be(("t2", "1"));
        index.Describe(Id("X")).Should().Be((string.Empty, string.Empty));
    }

    [Test]
    public void Unresolved_parent_should_make_the_index_incomplete()
    {
        CommitReachabilityIndexBuilder builder = new();
        Add(builder, "B", "A");
        CommitReachabilityIndex index = builder.Build(Refs(("refs/heads/main", "B"), ("refs/tags/t", "B")));

        index.UnresolvedParentCount.Should().Be(1);
        index.IsComplete.Should().BeFalse();
        index.GetBranchesContaining(Id("B"), getLocal: true, getRemote: true).Should().BeNull();
        index.GetTagsContaining(Id("B")).Should().BeNull();
        index.GetMergedBranches(Id("B")).Should().BeNull();
        index.CanGetMergedBranches(Id("B")).Should().BeFalse();
        index.Describe(Id("B")).Should().BeNull();
    }

    [Test]
    public void Revision_added_twice_should_keep_the_first_occurrence()
    {
        CommitReachabilityIndexBuilder builder = new();
        Add(builder, "S", "B");
        Add(builder, "B", "A");
        Add(builder, "A");

        // E.g. a stash commit listed again after its parent, with other parents
        Add(builder, "S", "A");
        Add(builder, "B", "A");

        builder.Count.Should().Be(3);
        builder.AddedCount.Should().Be(5);
        CommitReachabilityIndex index = builder.Build(Refs(("refs/heads/main", "B"), ("refs/heads/s", "S")));

        index.IsComplete.Should().BeTrue();
        index.CommitCount.Should().Be(3);
        index.GetBranchesContaining(Id("B"), getLocal: true, getRemote: false).Should().Equal("main", "s");
        index.GetMergedBranches(Id("S")).Should().BeEquivalentTo("refs/heads/main", "refs/heads/s");
        index.Describe(Id("S")).Should().Be((string.Empty, string.Empty));
    }

    [Test]
    public void Artificial_and_autostash_revisions_should_be_ignored()
    {
        CommitReachabilityIndexBuilder builder = new();
        builder.Add(
        [
            new GitRevision(ObjectId.WorkTreeId) { ParentIds = [ObjectId.IndexId] },
            new GitRevision(ObjectId.IndexId) { ParentIds = [Id("B")] },
            new GitRevision(Id("AUTOSTASH")) { ParentIds = [Id("MISSING")], IsAutostash = true },
            new GitRevision(Id("B")) { ParentIds = [Id("A")] },
            new GitRevision(Id("A")),
        ]);
        CommitReachabilityIndex index = builder.Build(Refs(("refs/heads/main", "B")));

        index.IsComplete.Should().BeTrue();
        index.CommitCount.Should().Be(2);
        index.GetBranchesContaining(ObjectId.WorkTreeId, getLocal: true, getRemote: true).Should().BeNull();
        index.GetBranchesContaining(ObjectId.IndexId, getLocal: true, getRemote: true).Should().BeNull();
        index.GetMergedBranches(ObjectId.IndexId).Should().BeNull();
        index.CanGetMergedBranches(ObjectId.IndexId).Should().BeFalse();
        index.Describe(ObjectId.WorkTreeId).Should().BeNull();
    }

    [Test]
    public void Commit_not_in_index_should_not_be_answered()
    {
        CommitReachabilityIndexBuilder builder = new();
        Add(builder, "A");
        CommitReachabilityIndex index = builder.Build(Refs(("refs/heads/main", "A")));

        index.GetBranchesContaining(Id("OTHER"), getLocal: true, getRemote: true).Should().BeNull();
        index.GetTagsContaining(Id("OTHER")).Should().BeNull();
        index.GetMergedBranches(Id("OTHER")).Should().BeNull();
        index.CanGetMergedBranches(Id("OTHER")).Should().BeFalse();
        index.Describe(Id("OTHER")).Should().BeNull();
    }

    [TestCase(true, true, new[] { "local", "remotes/origin/HEAD", "remotes/origin/master" })]
    [TestCase(true, false, new[] { "local" })]
    [TestCase(false, true, new[] { "origin/HEAD", "origin/master" })]
    [TestCase(false, false, new string[0])]
    public void Branches_should_be_formatted_like_git_branch(bool getLocal, bool getRemote, string[] expected)
    {
        CommitReachabilityIndexBuilder builder = new();
        Add(builder, "B", "A");
        Add(builder, "A");
        RefSnapshot refs = new(
            Pairs(("refs/remotes/origin/master", "B"), ("refs/heads/local", "A")),
            HeadState.Attached,
            additionalBranches: Pairs(("refs/remotes/origin/HEAD", "B")));
        CommitReachabilityIndex index = builder.Build(refs);

        index.GetBranchesContaining(Id("A"), getLocal, getRemote).Should().Equal(expected);
    }

    [Test]
    public void Branches_should_not_be_answered_if_a_branch_tip_is_missing()
    {
        // E.g. the branch was moved by another tool between reading the refs and git log
        CommitReachabilityIndexBuilder builder = new();
        Add(builder, "B", "A");
        Add(builder, "A");
        RefSnapshot refs = new(
            Pairs(("refs/heads/main", "B"), ("refs/remotes/origin/moved", "MISSING")),
            HeadState.Attached);
        CommitReachabilityIndex index = builder.Build(refs);

        index.GetBranchesContaining(Id("A"), getLocal: true, getRemote: true).Should().BeNull();
        index.GetBranchesContaining(Id("A"), getLocal: false, getRemote: true).Should().BeNull();

        // Only the requested kind of branches matters
        index.GetBranchesContaining(Id("A"), getLocal: true, getRemote: false).Should().Equal("main");

        // An ancestor cannot be outside a complete index
        index.GetMergedBranches(Id("B")).Should().BeEquivalentTo("refs/heads/main");
    }

    [Test]
    public void Tags_should_not_be_answered_if_a_lightweight_tag_target_is_missing()
    {
        // GE lists lightweight tags only for commits (no author date otherwise), so the commit is missing
        CommitReachabilityIndexBuilder builder = new();
        Add(builder, "A");
        CommitReachabilityIndex index = builder.Build(Refs(("refs/tags/v1", "A"), ("refs/tags/moved", "MISSING")));

        index.GetTagsContaining(Id("A")).Should().BeNull();
    }

    [Test]
    public void Branches_should_use_git_refname_order_for_non_ascii_names()
    {
        // git compares the UTF-8 bytes: U+E000 (EE 80 80) is before U+1F600 (F0 9F 98 80), unlike in UTF-16
        CommitReachabilityIndexBuilder builder = new();
        Add(builder, "A");
        RefSnapshot refs = new(
            Pairs(("refs/heads/😁", "A"), ("refs/heads/😀", "A"), ("refs/heads/", "A"), ("refs/heads/é", "A"), ("refs/heads/z", "A")),
            HeadState.Attached);
        CommitReachabilityIndex index = builder.Build(refs);

        index.GetBranchesContaining(Id("A"), getLocal: true, getRemote: false)
            .Should().Equal("z", "é", "", "😀", "😁");
    }

    [Test]
    public void Ref_name_comparison_should_not_allocate()
    {
        RefSnapshot.GitRefNameComparer comparer = RefSnapshot.GitRefNameComparer.Instance;
        comparer.Compare("refs/heads/feature/a", "refs/heads/feature/b");
        comparer.Compare("refs/heads/😀", "refs/heads/");

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            comparer.Compare("refs/heads/feature/a", "refs/heads/feature/b");
            comparer.Compare("refs/heads/😀", "refs/heads/");
        }

        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
    }

    [Test]
    public void Branches_should_use_git_refname_order()
    {
        CommitReachabilityIndexBuilder builder = new();
        Add(builder, "A");
        RefSnapshot refs = new(
            Pairs(("refs/remotes/origin/a", "A"), ("refs/heads/b", "A"), ("refs/heads/B", "A"), ("refs/heads/remotes/x", "A"), ("refs/heads/a-b", "A"), ("refs/heads/a/b", "A")),
            HeadState.Attached);
        CommitReachabilityIndex index = builder.Build(refs);

        index.GetBranchesContaining(Id("A"), getLocal: true, getRemote: true)
            .Should().Equal("B", "a-b", "a/b", "b", "remotes/x", "remotes/origin/a");
    }

    [Test]
    public void Detached_head_containing_the_commit_should_not_be_answered()
    {
        CommitReachabilityIndexBuilder builder = new();
        Add(builder, "C", "B");
        Add(builder, "B", "A");
        Add(builder, "A");
        CommitReachabilityIndex index = builder.Build(new RefSnapshot(Pairs(("refs/heads/main", "C")), HeadState.Detached(Id("B"))));

        index.GetBranchesContaining(Id("A"), getLocal: true, getRemote: false).Should().BeNull();
        index.GetBranchesContaining(Id("B"), getLocal: true, getRemote: false).Should().BeNull();
        index.GetBranchesContaining(Id("C"), getLocal: true, getRemote: false).Should().Equal("main");

        // "git branch -r" does not list the detached HEAD
        index.GetBranchesContaining(Id("A"), getLocal: false, getRemote: true).Should().BeEmpty();

        // Not affected by the detached HEAD
        index.GetTagsContaining(Id("A")).Should().BeEmpty();
        index.GetMergedBranches(Id("C")).Should().BeEquivalentTo("refs/heads/main");
    }

    [Test]
    public void Unknown_head_or_branches_should_not_be_answered()
    {
        CommitReachabilityIndexBuilder builder = new();
        Add(builder, "A");

        builder.Build(new RefSnapshot(Pairs(("refs/heads/main", "A")), HeadState.Unknown))
            .GetBranchesContaining(Id("A"), getLocal: true, getRemote: false).Should().BeNull();
        builder.Build(new RefSnapshot(Pairs(("refs/heads/main", "A")), HeadState.Attached, canReproduceBranches: false))
            .GetBranchesContaining(Id("A"), getLocal: true, getRemote: false).Should().BeNull();
    }

    [Test]
    public void Tags_should_be_in_refname_order_and_ignore_non_commits()
    {
        CommitReachabilityIndexBuilder builder = new();
        Add(builder, "B", "A");
        Add(builder, "A");
        RefSnapshot refs = new(
            Pairs(("refs/tags/v2", "B"), ("refs/tags/V1^{}", "A"), ("refs/tags/v1.0", "A"), ("refs/tags/tree^{}", "TREE"), ("refs/heads/main", "B")),
            HeadState.Attached);
        CommitReachabilityIndex index = builder.Build(refs);

        index.GetTagsContaining(Id("A")).Should().Equal("V1", "v1.0", "v2");
        index.GetTagsContaining(Id("B")).Should().Equal("v2");
    }

    [Test]
    public void Tags_should_not_be_answered_if_the_tag_order_is_configured()
    {
        CommitReachabilityIndexBuilder builder = new();
        Add(builder, "A");
        CommitReachabilityIndex index = builder.Build(new RefSnapshot(Pairs(("refs/tags/v1", "A")), HeadState.Attached, canReproduceTagOrder: false));

        index.GetTagsContaining(Id("A")).Should().BeNull();
        index.Describe(Id("A")).Should().Be(("v1", string.Empty));
    }

    [Test]
    public void Describe_should_follow_the_first_parent_chain()
    {
        // A <- B <- M (first parent), A <- T <- M (second parent, tagged)
        CommitReachabilityIndexBuilder builder = new();
        Add(builder, "M", "B", "T");
        Add(builder, "T", "A");
        Add(builder, "B", "A");
        Add(builder, "A");
        CommitReachabilityIndex index = builder.Build(Refs(("refs/tags/second", "T"), ("refs/tags/root", "A")));

        index.Describe(Id("M")).Should().Be(("root", "2"));
        index.Describe(Id("B")).Should().Be(("root", "1"));
        index.Describe(Id("T")).Should().Be(("second", string.Empty));
        index.Describe(Id("A")).Should().Be(("root", string.Empty));
    }

    [Test]
    public void Describe_should_prefer_annotated_tags_then_refname()
    {
        CommitReachabilityIndexBuilder builder = new();
        Add(builder, "C", "B");
        Add(builder, "B", "A");
        Add(builder, "A");
        CommitReachabilityIndex index = builder.Build(Refs(
            ("refs/tags/light-z", "C"),
            ("refs/tags/light-a", "C"),
            ("refs/tags/z-annotated^{}", "B"),
            ("refs/tags/a-light", "B"),
            ("refs/tags/two^{}", "A"),
            ("refs/tags/one^{}", "A")));

        index.Describe(Id("C")).Should().Be(("light-a", string.Empty));
        index.Describe(Id("B")).Should().Be(("z-annotated", string.Empty));

        // The tagger dates decide, they are not known
        index.Describe(Id("A")).Should().BeNull();
    }

    private void Add(CommitReachabilityIndexBuilder builder, string commit, params string[] parents)
    {
        builder.Add(Id(commit), parents.Select(Id).ToArray());
    }

    private RefSnapshot Refs(params (string Name, string Commit)[] refs)
        => new(Pairs(refs), HeadState.Attached);

    private IEnumerable<KeyValuePair<string, ObjectId>> Pairs(params (string Name, string Commit)[] refs)
        => refs.Select(r => new KeyValuePair<string, ObjectId>(r.Name, Id(r.Commit))).ToList();

    private ObjectId Id(string name)
    {
        if (!_ids.TryGetValue(name, out ObjectId id))
        {
            id = ObjectId.Random();
            _ids.Add(name, id);
        }

        return id;
    }
}
