using GitExtensions.Extensibility.Git;
using GitUIPluginInterfaces;

namespace GitUI.UserControls.RevisionGrid.Reachability;

/// <summary>
///  Collects the commits and parents read by <c>git log</c> for a revision grid load.
/// </summary>
/// <remarks>
///  Revisions are normally added by a single thread (the git log reader); a lock protects against concurrent use.
///  A commit added again (e.g. listed twice by the reader) is ignored, the first occurrence is kept.
/// </remarks>
internal sealed class CommitReachabilityIndexBuilder
{
    private readonly object _lock = new();
    private readonly Dictionary<ObjectId, int> _indexById = [];
    private readonly List<ObjectId> _ids = [];
    private readonly List<ObjectId[]> _parentIds = [];

    /// <summary>
    ///  Gets the number of distinct commits added.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _ids.Count;
            }
        }
    }

    /// <summary>
    ///  Gets the number of revisions added, including revisions which were added more than once.
    /// </summary>
    public int AddedCount { get; private set; }

    public void Add(IReadOnlyList<GitRevision> revisions)
    {
        lock (_lock)
        {
            foreach (GitRevision revision in revisions)
            {
                // Artificial commits and the autostash commit are not part of the git log output.
                if (revision.IsArtificial || revision.IsAutostash)
                {
                    continue;
                }

                AddCore(revision.ObjectId, revision.ParentIds);
            }
        }
    }

    public void Add(ObjectId objectId, IReadOnlyList<ObjectId>? parentIds)
    {
        lock (_lock)
        {
            AddCore(objectId, parentIds);
        }
    }

    public CommitReachabilityIndex Build(RefSnapshot refs, int generation = 0)
    {
        lock (_lock)
        {
            return CommitReachabilityIndex.Create(_ids, _indexById, _parentIds, refs, generation);
        }
    }

    private void AddCore(ObjectId objectId, IReadOnlyList<ObjectId>? parentIds)
    {
        if (objectId.IsZeroOrArtificial)
        {
            return;
        }

        AddedCount++;
        if (!_indexById.TryAdd(objectId, _ids.Count))
        {
            return;
        }

        _ids.Add(objectId);
        _parentIds.Add(parentIds is null || parentIds.Count == 0 ? [] : [.. parentIds]);
    }
}
