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
    ///  git runs only after the wait, so this delays the info if the index is not available in time.
    /// </summary>
    public TimeSpan WaitTimeout { get; set; } = TimeSpan.FromMilliseconds(500);

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
        if (load is null || load.Id != loadId || !load.Options.IsUsable || load.Builder is not CommitReachabilityIndexBuilder builder)
        {
            return;
        }

        builder.Add(revisions);

        // The history is truncated by --max-count: no index, do not let anybody wait for it.
        if (load.Options.CommitsLimit > 0 && builder.AddedCount >= load.Options.CommitsLimit)
        {
            load.Completion.TrySetResult(null);
        }
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
                && load.Builder is CommitReachabilityIndexBuilder builder
                && (load.Options.CommitsLimit <= 0 || builder.AddedCount < load.Options.CommitsLimit))
            {
                index = builder.Build(load.CreateRefs(), loadId);
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
        finally
        {
            // The index has its own copy of the data
            load.Builder = null;
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

    /// <summary>
    ///  Ends the current generation without index if it has not been completed, e.g. the grid load ended without
    ///  revisions. Waiting callers use git immediately instead of waiting for the timeout.
    /// </summary>
    public void EndWithoutIndex()
    {
        Load? load;
        lock (_lock)
        {
            if (_index is not null)
            {
                return;
            }

            load = _currentLoad;
        }

        if (load is not null)
        {
            load.Builder = null;
            load.Completion.TrySetResult(null);
        }
    }

    internal bool HoldsBuilder => Volatile.Read(ref _currentLoad)?.Builder is not null;

    public bool IsCurrent(CommitReachabilityIndex index)
    {
        lock (_lock)
        {
            return ReferenceEquals(_index, index);
        }
    }

    private sealed class Load(int id, ReachabilityLoadOptions options, Func<RefSnapshot> createRefs)
    {
        private CommitReachabilityIndexBuilder? _builder = new();

        public int Id { get; } = id;

        public ReachabilityLoadOptions Options { get; } = options;

        public Func<RefSnapshot> CreateRefs { get; } = createRefs;

        /// <summary>
        ///  Gets or sets the builder, <see langword="null"/> after the load has ended.
        /// </summary>
        public CommitReachabilityIndexBuilder? Builder
        {
            get => Volatile.Read(ref _builder);
            set => Volatile.Write(ref _builder, value);
        }

        public TaskCompletionSource<CommitReachabilityIndex?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
