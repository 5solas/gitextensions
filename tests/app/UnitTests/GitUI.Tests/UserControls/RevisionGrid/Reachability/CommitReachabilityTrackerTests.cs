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
    public void WaitTimeout_should_be_short()
    {
        // git runs only after the wait, a long wait delays the info on large repositories
        new CommitReachabilityTracker().WaitTimeout.Should().Be(TimeSpan.FromMilliseconds(500));
    }

    [Test]
    public async Task WaitForIndexAsync_should_not_wait_for_a_truncated_load()
    {
        CommitReachabilityTracker tracker = new() { WaitTimeout = TimeSpan.FromMinutes(1) };
        tracker.Begin(1, new ReachabilityLoadOptions(IsUsable: true, CommitsLimit: 2), CreateRefs);
        tracker.Add(1, Revisions());

        Task<CommitReachabilityIndex?> wait = tracker.WaitForIndexAsync(CancellationToken.None);

        wait.IsCompleted.Should().BeTrue();
        (await wait).Should().BeNull();
    }

    [Test]
    public async Task WaitForIndexAsync_should_stop_waiting_when_the_load_gets_truncated()
    {
        CommitReachabilityTracker tracker = new() { WaitTimeout = TimeSpan.FromMinutes(1) };
        tracker.Begin(1, new ReachabilityLoadOptions(IsUsable: true, CommitsLimit: 2), CreateRefs);
        tracker.Add(1, [new GitRevision(_b) { ParentIds = [_a] }]);
        Task<CommitReachabilityIndex?> wait = tracker.WaitForIndexAsync(CancellationToken.None);
        wait.IsCompleted.Should().BeFalse();

        tracker.Add(1, [new GitRevision(_a)]);

        (await wait.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeNull();
    }

    [Test]
    public async Task WaitForIndexAsync_should_stop_waiting_when_the_load_ends_without_index()
    {
        CommitReachabilityTracker tracker = new() { WaitTimeout = TimeSpan.FromMinutes(1) };
        tracker.Begin(1, _usable, CreateRefs);
        Task<CommitReachabilityIndex?> wait = tracker.WaitForIndexAsync(CancellationToken.None);

        // E.g. no revisions at all: the grid is loaded without CompleteReachabilityLoad()
        tracker.EndWithoutIndex(1);

        (await wait.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeNull();
        tracker.TryGetIndex(out _).Should().BeFalse();
        (await tracker.WaitForIndexAsync(CancellationToken.None)).Should().BeNull();
    }

    [Test]
    public void EndWithoutIndex_should_not_drop_a_published_index()
    {
        CommitReachabilityTracker tracker = new();
        tracker.Begin(1, _usable, CreateRefs);
        tracker.Add(1, Revisions());
        tracker.Complete(1);

        tracker.EndWithoutIndex(1);

        tracker.TryGetIndex(out _).Should().BeTrue();
    }

    [Test]
    public async Task EndWithoutIndex_should_ignore_another_generation()
    {
        CommitReachabilityTracker tracker = new() { WaitTimeout = TimeSpan.FromMinutes(1) };
        tracker.Begin(1, _usable, CreateRefs);
        tracker.Begin(2, _usable, CreateRefs);
        Task<CommitReachabilityIndex?> wait = tracker.WaitForIndexAsync(CancellationToken.None);

        tracker.EndWithoutIndex(1);

        wait.IsCompleted.Should().BeFalse();
        tracker.HoldsBuilder.Should().BeTrue();
        tracker.Add(2, Revisions());
        tracker.Complete(2);
        (await wait).Should().NotBeNull();
    }

    [Test]
    public async Task EndWithoutIndex_should_release_the_builder()
    {
        CommitReachabilityTracker tracker = new();
        tracker.Begin(1, _usable, CreateRefs);

        tracker.EndWithoutIndex(1);

        tracker.HoldsBuilder.Should().BeFalse();
        (await tracker.WaitForIndexAsync(CancellationToken.None)).Should().BeNull();
    }

    [Test]
    public async Task Complete_should_end_the_load_if_it_is_cancelled()
    {
        CommitReachabilityTracker tracker = new() { WaitTimeout = TimeSpan.FromMinutes(1) };
        tracker.Begin(1, _usable, () => throw new OperationCanceledException());
        tracker.Add(1, Revisions());
        Task<CommitReachabilityIndex?> wait = tracker.WaitForIndexAsync(CancellationToken.None);

        ((Action)(() => tracker.Complete(1))).Should().Throw<OperationCanceledException>();

        (await wait.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeNull();
        tracker.HoldsBuilder.Should().BeFalse();
    }

    [Test]
    public void Load_after_a_truncated_load_of_the_same_repository_should_not_be_waited_for()
    {
        CommitReachabilityTracker tracker = new() { WaitTimeout = TimeSpan.FromMinutes(1) };
        ReachabilityLoadOptions truncated = new(IsUsable: true, CommitsLimit: 2);
        tracker.Begin(1, truncated, CreateRefs, "repo");
        CompleteLoad(tracker, 1);

        tracker.Begin(2, truncated, CreateRefs, "repo");
        IsNotWaitedFor(tracker).Should().BeTrue();

        // Not waited for, but still truncated
        CompleteLoad(tracker, 2);
        tracker.Begin(3, truncated, CreateRefs, "repo");
        IsNotWaitedFor(tracker).Should().BeTrue();
    }

    [Test]
    public void Load_after_a_slow_load_of_the_same_repository_should_not_be_waited_for()
    {
        long now = 0;
        CommitReachabilityTracker tracker = new() { WaitTimeout = TimeSpan.FromMinutes(1), GetTickCount = () => now };
        tracker.Begin(1, _usable, CreateRefs, "repo");
        IsNotWaitedFor(tracker).Should().BeFalse();
        now += 60_001;
        CompleteLoad(tracker, 1);

        // The skipped wait does not hide that this load is slow, too
        tracker.Begin(2, _usable, CreateRefs, "repo");
        IsNotWaitedFor(tracker).Should().BeTrue();
        now += 60_001;
        CompleteLoad(tracker, 2);
        tracker.TryGetIndex(out _).Should().BeTrue("the index is used as soon as it is available");

        // Superseded before it has been completed, but after the timeout
        tracker.Begin(3, _usable, CreateRefs, "repo");
        IsNotWaitedFor(tracker).Should().BeTrue();
        now += 60_001;

        tracker.Begin(4, _usable, CreateRefs, "repo");
        IsNotWaitedFor(tracker).Should().BeTrue();
    }

    [Test]
    public void Load_should_be_waited_for_again_after_a_fast_load()
    {
        long now = 0;
        CommitReachabilityTracker tracker = new() { WaitTimeout = TimeSpan.FromMinutes(1), GetTickCount = () => now };
        tracker.Begin(1, _usable, CreateRefs, "repo");
        IsNotWaitedFor(tracker).Should().BeFalse();
        now += 60_001;
        CompleteLoad(tracker, 1);

        tracker.Begin(2, _usable, CreateRefs, "repo");
        IsNotWaitedFor(tracker).Should().BeTrue();
        now += 10;
        CompleteLoad(tracker, 2);

        tracker.Begin(3, _usable, CreateRefs, "repo");
        IsNotWaitedFor(tracker).Should().BeFalse();
    }

    [Test]
    public void Load_should_keep_the_state_of_the_previous_load_if_that_tells_nothing()
    {
        long now = 0;
        CommitReachabilityTracker tracker = new() { WaitTimeout = TimeSpan.FromMinutes(1), GetTickCount = () => now };
        tracker.Begin(1, _usable, CreateRefs, "repo");
        IsNotWaitedFor(tracker).Should().BeFalse();
        now += 60_001;
        CompleteLoad(tracker, 1);

        // Superseded at once, then not usable (e.g. filtered)
        tracker.Begin(2, _usable, CreateRefs, "repo");
        tracker.Begin(3, new ReachabilityLoadOptions(IsUsable: false, CommitsLimit: 0), CreateRefs, "repo");
        tracker.Complete(3);

        tracker.Begin(4, _usable, CreateRefs, "repo");
        IsNotWaitedFor(tracker).Should().BeTrue();
    }

    [Test]
    public void Load_of_another_repository_should_be_waited_for()
    {
        CommitReachabilityTracker tracker = new() { WaitTimeout = TimeSpan.FromMinutes(1) };
        tracker.Begin(1, new ReachabilityLoadOptions(IsUsable: true, CommitsLimit: 2), CreateRefs, "repo");
        CompleteLoad(tracker, 1);

        tracker.Begin(2, _usable, CreateRefs, "other repo");
        IsNotWaitedFor(tracker).Should().BeFalse();
    }

    [Test]
    public void Complete_should_release_the_builder()
    {
        CommitReachabilityTracker tracker = new();
        tracker.Begin(1, _usable, CreateRefs);
        tracker.Add(1, Revisions());
        tracker.HoldsBuilder.Should().BeTrue();

        tracker.Complete(1);

        tracker.HoldsBuilder.Should().BeFalse();
        tracker.TryGetIndex(out _).Should().BeTrue();
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

        ReachabilityLoadOptions.FromFilter(new FilterInfo { IsRaw = true }).IsUsable.Should().BeFalse();
        ReachabilityLoadOptions.FromFilter(new FilterInfo { ByCommitter = true, Committer = "me" }).IsUsable.Should().BeFalse();
        ReachabilityLoadOptions.FromFilter(new FilterInfo { ByMessage = true, Message = "fix" }).IsUsable.Should().BeFalse();
        ReachabilityLoadOptions.FromFilter(new FilterInfo { ByDiffContent = true, DiffContent = "x" }).IsUsable.Should().BeFalse();

        // --boundary
        ReachabilityLoadOptions.FromFilter(new FilterInfo { ByMessage = true, Message = "fix", ByDiffContent = true, DiffContent = "x" }).IsUsable.Should().BeFalse();
    }

    [Test]
    public void FromFilter_should_reject_no_merges_and_simplify_by_decoration()
    {
        bool hideMergeCommits = AppSettings.HideMergeCommits;
        bool showSimplifyByDecoration = AppSettings.ShowSimplifyByDecoration;
        try
        {
            FilterInfo filter = new();

            filter.HideMergeCommits = true;
            ReachabilityLoadOptions.FromFilter(filter).IsUsable.Should().BeFalse();
            filter.HideMergeCommits = false;

            filter.ShowSimplifyByDecoration = true;
            ReachabilityLoadOptions.FromFilter(filter).IsUsable.Should().BeFalse();
            filter.ShowSimplifyByDecoration = false;

            ReachabilityLoadOptions.FromFilter(filter).IsUsable.Should().BeTrue();
        }
        finally
        {
            AppSettings.HideMergeCommits = hideMergeCommits;
            AppSettings.ShowSimplifyByDecoration = showSimplifyByDecoration;
        }
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

    private static bool IsNotWaitedFor(CommitReachabilityTracker tracker)
        => tracker.WaitForIndexAsync(CancellationToken.None).IsCompleted;

    private void CompleteLoad(CommitReachabilityTracker tracker, int loadId)
    {
        tracker.Add(loadId, Revisions());
        tracker.Complete(loadId);
    }

    private RefSnapshot CreateRefs()
        => new([new KeyValuePair<string, ObjectId>("refs/heads/main", _b)], HeadState.Attached);

    private GitRevision[] Revisions()
        => [new GitRevision(_b) { ParentIds = [_a] }, new GitRevision(_a)];
}
