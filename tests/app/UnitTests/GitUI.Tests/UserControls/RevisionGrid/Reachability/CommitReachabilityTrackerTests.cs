using GitCommands;
using GitExtensions.Extensibility.Git;
using GitUI.UserControls.RevisionGrid;
using GitUI.UserControls.RevisionGrid.Reachability;
using GitUIPluginInterfaces;

namespace GitUITests.UserControls.RevisionGrid.Reachability;

[TestFixture]
public class CommitReachabilityTrackerTests
{
    private static readonly ReachabilityLoadOptions _usable = new(IsUsable: true, CommitsLimit: 100);

    private readonly ObjectId _a = ObjectId.Random();
    private readonly ObjectId _b = ObjectId.Random();
    private readonly ObjectId _c = ObjectId.Random();

    [Test]
    public void Completed_load_should_publish_its_index()
    {
        CommitReachabilityTracker tracker = new();
        tracker.Begin(1, _usable, CreateRefs);
        tracker.TryGetIndex(out _).Should().BeFalse();

        tracker.Add(1, Revisions());
        tracker.Complete(1);

        tracker.TryGetIndex(out CommitReachabilityIndex? index).Should().BeTrue();
        index!.Generation.Should().Be(1);
        index.CommitCount.Should().Be(2);
        tracker.IsCurrent(index).Should().BeTrue();
        index.GetBranchesContaining(_a, getLocal: true, getRemote: false).Should().Equal("main");
    }

    [Test]
    public void New_generation_should_invalidate_the_previous_index_and_ignore_the_previous_load()
    {
        CommitReachabilityTracker tracker = new();
        tracker.Begin(1, _usable, CreateRefs);
        tracker.Add(1, Revisions());
        tracker.Complete(1);
        tracker.TryGetIndex(out CommitReachabilityIndex? first).Should().BeTrue();

        tracker.Begin(2, _usable, CreateRefs);
        tracker.TryGetIndex(out _).Should().BeFalse();
        tracker.IsCurrent(first!).Should().BeFalse();

        // Superseded load
        tracker.Add(1, [new GitRevision(_c) { ParentIds = [_b] }]);
        tracker.Complete(1);
        tracker.TryGetIndex(out _).Should().BeFalse();

        tracker.Add(2, Revisions());
        tracker.Complete(2);
        tracker.TryGetIndex(out CommitReachabilityIndex? second).Should().BeTrue();
        second!.Generation.Should().Be(2);
        second.Contains(_c).Should().BeFalse();
    }

    [Test]
    public void Unusable_load_should_not_publish_an_index()
    {
        CommitReachabilityTracker tracker = new();
        tracker.Begin(1, new ReachabilityLoadOptions(IsUsable: false, CommitsLimit: 100), CreateRefs);
        tracker.Add(1, Revisions());
        tracker.Complete(1);

        tracker.TryGetIndex(out _).Should().BeFalse();
    }

    [Test]
    public void Truncated_load_should_not_publish_an_index()
    {
        CommitReachabilityTracker tracker = new();
        tracker.Begin(1, new ReachabilityLoadOptions(IsUsable: true, CommitsLimit: 2), CreateRefs);
        tracker.Add(1, Revisions());
        tracker.Complete(1);

        tracker.TryGetIndex(out _).Should().BeFalse();
    }

    [Test]
    public void Incomplete_history_should_not_publish_an_index()
    {
        CommitReachabilityTracker tracker = new();
        tracker.Begin(1, _usable, CreateRefs);
        tracker.Add(1, [new GitRevision(_b) { ParentIds = [_a] }]);
        tracker.Complete(1);

        tracker.TryGetIndex(out _).Should().BeFalse();
    }

    [Test]
    public void Failing_refs_should_not_publish_an_index()
    {
        CommitReachabilityTracker tracker = new();
        tracker.Begin(1, _usable, () => throw new IOException());
        tracker.Add(1, Revisions());
        tracker.Complete(1);

        tracker.TryGetIndex(out _).Should().BeFalse();
    }

    [Test]
    public async Task WaitForIndexAsync_should_return_null_without_load_or_for_unusable_load()
    {
        CommitReachabilityTracker tracker = new() { WaitTimeout = TimeSpan.FromMinutes(1) };
        (await tracker.WaitForIndexAsync(CancellationToken.None)).Should().BeNull();

        tracker.Begin(1, new ReachabilityLoadOptions(IsUsable: false, CommitsLimit: 0), CreateRefs);
        (await tracker.WaitForIndexAsync(CancellationToken.None)).Should().BeNull();
    }

    [Test]
    public async Task WaitForIndexAsync_should_wait_for_the_load_in_progress()
    {
        CommitReachabilityTracker tracker = new() { WaitTimeout = TimeSpan.FromMinutes(1) };
        tracker.Begin(1, _usable, CreateRefs);
        tracker.Add(1, Revisions());

        Task<CommitReachabilityIndex?> wait = tracker.WaitForIndexAsync(CancellationToken.None);
        wait.IsCompleted.Should().BeFalse();

        tracker.Complete(1);
        CommitReachabilityIndex? index = await wait;
        index.Should().NotBeNull();
        index!.Generation.Should().Be(1);
    }

    [Test]
    public async Task WaitForIndexAsync_should_give_up_after_the_timeout()
    {
        CommitReachabilityTracker tracker = new() { WaitTimeout = TimeSpan.FromMilliseconds(50) };
        tracker.Begin(1, _usable, CreateRefs);

        (await tracker.WaitForIndexAsync(CancellationToken.None)).Should().BeNull();
    }

    [Test]
    public async Task WaitForIndexAsync_should_return_null_if_the_load_is_superseded()
    {
        CommitReachabilityTracker tracker = new() { WaitTimeout = TimeSpan.FromMinutes(1) };
        tracker.Begin(1, _usable, CreateRefs);
        Task<CommitReachabilityIndex?> wait = tracker.WaitForIndexAsync(CancellationToken.None);

        tracker.Begin(2, _usable, CreateRefs);

        (await wait).Should().BeNull();
    }

    [Test]
    public async Task WaitForIndexAsync_should_be_cancelled_with_the_selection()
    {
        CommitReachabilityTracker tracker = new() { WaitTimeout = TimeSpan.FromMinutes(1) };
        tracker.Begin(1, _usable, CreateRefs);
        using CancellationTokenSource cts = new();
        Task<CommitReachabilityIndex?> wait = tracker.WaitForIndexAsync(cts.Token);

        await cts.CancelAsync();

        bool cancelled = false;
        try
        {
            await wait;
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        cancelled.Should().BeTrue();
    }

    [Test]
    public void FromFilter_should_accept_the_default_filter()
    {
        ReachabilityLoadOptions options = ReachabilityLoadOptions.FromFilter(new FilterInfo());
        options.IsUsable.Should().BeTrue();
        options.CommitsLimit.Should().Be(AppSettings.MaxRevisionGraphCommits);
    }

    [Test]
    public void FromFilter_should_reject_filters_which_rewrite_parents_or_truncate_the_history()
    {
        ReachabilityLoadOptions.FromFilter(new FilterInfo { ByPathFilter = true, PathFilter = "src" }).IsUsable.Should().BeFalse();
        ReachabilityLoadOptions.FromFilter(new FilterInfo { ByAuthor = true, Author = "me" }).IsUsable.Should().BeFalse();
        ReachabilityLoadOptions.FromFilter(new FilterInfo { ByDateFrom = true }).IsUsable.Should().BeFalse();
        ReachabilityLoadOptions.FromFilter(new FilterInfo { ByDateTo = true }).IsUsable.Should().BeFalse();

        FilterInfo lastRevision = new();
        lastRevision.LastRevisionToDisplayHash = _a.ToString();
        ReachabilityLoadOptions.FromFilter(lastRevision).IsUsable.Should().BeFalse();
    }

    [Test]
    public void FromFilter_should_reject_first_parent_and_branch_filters()
    {
        bool showOnlyFirstParent = AppSettings.ShowOnlyFirstParent;
        bool showCurrentBranchOnly = AppSettings.ShowCurrentBranchOnly;
        bool branchFilterEnabled = AppSettings.BranchFilterEnabled;
        try
        {
            FilterInfo filter = new();

            filter.ShowOnlyFirstParent = true;
            ReachabilityLoadOptions.FromFilter(filter).IsUsable.Should().BeFalse();
            filter.ShowOnlyFirstParent = false;

            filter.ShowCurrentBranchOnly = true;
            ReachabilityLoadOptions.FromFilter(filter).IsUsable.Should().BeFalse();
            filter.ShowCurrentBranchOnly = false;

            filter.SetBranchFilter("main");
            ReachabilityLoadOptions.FromFilter(filter).IsUsable.Should().BeFalse();
        }
        finally
        {
            AppSettings.ShowOnlyFirstParent = showOnlyFirstParent;
            AppSettings.ShowCurrentBranchOnly.Value = showCurrentBranchOnly;
            AppSettings.BranchFilterEnabled.Value = branchFilterEnabled;
        }
    }

    private RefSnapshot CreateRefs()
        => new([new KeyValuePair<string, ObjectId>("refs/heads/main", _b)], HeadState.Attached);

    private GitRevision[] Revisions()
        => [new GitRevision(_b) { ParentIds = [_a] }, new GitRevision(_a)];
}
