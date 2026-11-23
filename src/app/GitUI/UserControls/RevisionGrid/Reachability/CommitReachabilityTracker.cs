using System.Diagnostics.CodeAnalysis;
using GitExtensions.Extensibility.Git;
using GitUIPluginInterfaces;

namespace GitUI.UserControls.RevisionGrid.Reachability;

/// <summary>
///  The conditions of a revision grid load which decide whether its commits can be used as a reachability index.
/// </summary>
/// <param name="IsUsable">
///  <see langword="false"/> if git log may rewrite parents or may not load all refs and their history.
/// </param>
/// <param name="CommitsLimit">The <c>--max-count</c> of the load, 0 if unlimited.</param>
internal readonly record struct ReachabilityLoadOptions(bool IsUsable, int CommitsLimit)
{
    public static ReachabilityLoadOptions FromFilter(FilterInfo filter)
    {
        bool isUsable = !filter.IsRaw

            // Parents rewritten (--parents for path, author, message filters, --no-merges, --simplify-by-decoration)
            && !filter.HasRevisionFilter

            // Only the first parents are loaded
            && !filter.ShowOnlyFirstParent

            // History truncated (--since) or tips missing (--until)
            && !filter.ByDateFrom
            && !filter.ByDateTo

            // Only some refs are loaded
            && !filter.IsShowCurrentBranchOnlyChecked
            && !(filter.IsShowFilteredBranchesChecked && !string.IsNullOrWhiteSpace(filter.BranchFilter))
            && string.IsNullOrWhiteSpace(filter.LastRevisionToDisplayHash)

            // --boundary adds commits which are not part of the history
            && (string.IsNullOrWhiteSpace(filter.Message) || string.IsNullOrWhiteSpace(filter.DiffContent));

        return new ReachabilityLoadOptions(isUsable, filter.CommitsLimit);
    }
}

/// <summary>
///  Builds the <see cref="CommitReachabilityIndex"/> of the revision grid loads, one generation per data load.
/// </summary>
internal sealed class CommitReachabilityTracker : ICommitReachabilityProvider
{
    private readonly object _lock = new();
    private Load? _currentLoad;
    private CommitReachabilityIndex? _index;

    /// <summary>
    ///  Gets or sets how long to wait for a load in progress before git is used instead.
    /// </summary>
    public TimeSpan WaitTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    ///  Starts a new generation, the index of the previous generation is no longer used.
    /// </summary>
    public void Begin(int loadId, ReachabilityLoadOptions options, Func<RefSnapshot> createRefs)
    {
        Load? previousLoad;
        lock (_lock)
        {
            previousLoad = _currentLoad;
            _currentLoad = new Load(loadId, options, createRefs);
            _index = null;
        }

        previousLoad?.Completion.TrySetResult(null);
    }

    public void Add(int loadId, IReadOnlyList<GitRevision> revisions)
    {
        Load? load = Volatile.Read(ref _currentLoad);
        if (load is null || load.Id != loadId || !load.Options.IsUsable)
        {
            return;
        }

        load.Builder.Add(revisions);
    }

    /// <summary>
    ///  Completes the generation: builds and publishes the index if it can be used.
    /// </summary>
    public void Complete(int loadId)
    {
        Load? load = Volatile.Read(ref _currentLoad);
        if (load is null || load.Id != loadId)
        {
            return;
        }

        CommitReachabilityIndex? index = null;
        try
        {
            if (load.Options.IsUsable
                && (load.Options.CommitsLimit <= 0 || load.Builder.AddedCount < load.Options.CommitsLimit))
            {
                index = load.Builder.Build(load.CreateRefs(), loadId);
                if (!index.IsComplete)
                {
                    index = null;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Use git instead
            index = null;
        }

        lock (_lock)
        {
            if (_currentLoad != load)
            {
                return;
            }

            _index = index;
        }

        load.Completion.TrySetResult(index);
    }

    public bool TryGetIndex([NotNullWhen(true)] out CommitReachabilityIndex? index)
    {
        lock (_lock)
        {
            index = _index;
        }

        return index is not null;
    }

    public async Task<CommitReachabilityIndex?> WaitForIndexAsync(CancellationToken cancellationToken)
    {
        Load? load;
        lock (_lock)
        {
            if (_index is not null)
            {
                return _index;
            }

            load = _currentLoad;
        }

        if (load is null || !load.Options.IsUsable)
        {
            return null;
        }

        Task<CommitReachabilityIndex?> completion = load.Completion.Task;
        if (!completion.IsCompleted)
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(WaitTimeout);
            TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using (timeout.Token.Register(() => cancelled.TrySetResult()))
            {
                await Task.WhenAny(completion, cancelled.Task).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!completion.IsCompleted)
            {
                return null;
            }
        }

        return await completion.ConfigureAwait(false);
    }

    public bool IsCurrent(CommitReachabilityIndex index)
    {
        lock (_lock)
        {
            return ReferenceEquals(_index, index);
        }
    }

    private sealed class Load(int id, ReachabilityLoadOptions options, Func<RefSnapshot> createRefs)
    {
        public int Id { get; } = id;

        public ReachabilityLoadOptions Options { get; } = options;

        public Func<RefSnapshot> CreateRefs { get; } = createRefs;

        public CommitReachabilityIndexBuilder Builder { get; } = new();

        public TaskCompletionSource<CommitReachabilityIndex?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
