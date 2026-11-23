using System.Globalization;
using GitCommands;
using GitExtensions.Extensibility.Git;

namespace GitUI.UserControls.RevisionGrid.Reachability;

/// <summary>
///  An immutable reachability index of the commits of a revision grid load (parents and children as CSR arrays)
///  with the refs of the load. Computes the results of <c>git branch --contains</c>, <c>git tag --contains</c>,
///  <c>git branch -a --merged</c> and <c>git describe --tags --first-parent</c> in memory.
/// </summary>
/// <remarks>
///  The queries return <see langword="null"/> when the result cannot be proven to be the same as the git output,
///  the caller must then run git. The results are based on the refs of the grid load: refs which GE hides are not
///  reported and refs changed outside GE since the load are not seen.
/// </remarks>
internal sealed class CommitReachabilityIndex
{
    private readonly ObjectId[] _ids;
    private readonly Dictionary<ObjectId, int> _indexById;
    private readonly int[] _parentStart;
    private readonly int[] _parents;
    private readonly int[] _childStart;
    private readonly int[] _children;
    private readonly int[] _firstParent;
    private readonly Dictionary<int, List<RefSnapshot.TagRef>> _tagsByCommit;
    private readonly bool _isLocalBranchTipMissing;
    private readonly bool _isRemoteBranchTipMissing;
    private readonly bool _isTaggedCommitMissing;

    private CachedMask? _lastDescendants;
    private CachedMask? _lastAncestors;

    private CommitReachabilityIndex(
        ObjectId[] ids,
        Dictionary<ObjectId, int> indexById,
        int[] parentStart,
        int[] parents,
        int[] childStart,
        int[] children,
        int[] firstParent,
        int unresolvedParentCount,
        RefSnapshot refs,
        int generation)
    {
        _ids = ids;
        _indexById = indexById;
        _parentStart = parentStart;
        _parents = parents;
        _childStart = childStart;
        _children = children;
        _firstParent = firstParent;
        UnresolvedParentCount = unresolvedParentCount;
        Refs = refs;
        Generation = generation;

        _tagsByCommit = [];
        foreach (RefSnapshot.TagRef tag in refs.Tags)
        {
            if (_indexById.TryGetValue(tag.Target, out int commit))
            {
                if (!_tagsByCommit.TryGetValue(commit, out List<RefSnapshot.TagRef>? tags))
                {
                    tags = [];
                    _tagsByCommit.Add(commit, tags);
                }

                tags.Add(tag);
            }
            else
            {
                // The target is a commit which was not loaded. The target of an annotated tag may also be another object
                // (e.g. a tree), which git does not list, but this cannot be distinguished.
                _isTaggedCommitMissing = true;
            }
        }

        // The refs and the commits are read separately: a ref may have been moved by another tool in between.
        foreach ((string completeName, ObjectId tip) in refs.Branches)
        {
            if (!_indexById.ContainsKey(tip))
            {
                if (completeName.StartsWith(GitRefName.RefsHeadsPrefix, StringComparison.Ordinal))
                {
                    _isLocalBranchTipMissing = true;
                }
                else
                {
                    _isRemoteBranchTipMissing = true;
                }
            }
        }
    }

    /// <summary>
    ///  Gets the generation (grid data load id) of the index.
    /// </summary>
    public int Generation { get; }

    public int CommitCount => _ids.Length;

    /// <summary>
    ///  Gets the number of parent ids which are not commits of the index (the history was truncated or filtered).
    /// </summary>
    public int UnresolvedParentCount { get; }

    /// <summary>
    ///  Gets whether the ancestors of all commits are part of the index.
    /// </summary>
    public bool IsComplete => UnresolvedParentCount == 0;

    public RefSnapshot Refs { get; }

    public bool Contains(ObjectId objectId) => _indexById.ContainsKey(objectId);

    /// <summary>
    ///  Returns the branches containing <paramref name="commit"/>, formatted like
    ///  <see cref="IGitModule.GetAllBranchesWhichContainGivenCommit"/>, or <see langword="null"/> if git must be used.
    /// </summary>
    public IReadOnlyList<string>? GetBranchesContaining(ObjectId commit, bool getLocal, bool getRemote)
    {
        if (!getLocal && !getRemote)
        {
            return [];
        }

        if (!IsComplete || !Refs.CanReproduceBranches || !Refs.Head.IsKnown
            || (getLocal && _isLocalBranchTipMissing)
            || (getRemote && _isRemoteBranchTipMissing)
            || !TryGetCommit(commit, out int index))
        {
            return null;
        }

        bool[] descendants = GetDescendants(index);

        // git lists "(HEAD detached at ...)" first if the detached HEAD contains the commit, the text cannot be reproduced.
        // "git branch -r" does not list it.
        if (getLocal
            && Refs.Head.DetachedAt is ObjectId detachedAt
            && (!TryGetCommit(detachedAt, out int headIndex) || descendants[headIndex]))
        {
            return null;
        }

        List<string> branches = [];
        foreach ((string completeName, ObjectId tip) in Refs.Branches)
        {
            bool isLocal = completeName.StartsWith(GitRefName.RefsHeadsPrefix, StringComparison.Ordinal);
            if ((isLocal ? !getLocal : !getRemote)
                || !_indexById.TryGetValue(tip, out int tipIndex)
                || !descendants[tipIndex])
            {
                continue;
            }

            // "git branch -a" prefixes remote branches with "remotes/", "git branch -r" does not.
            branches.Add(isLocal
                ? completeName[GitRefName.RefsHeadsPrefix.Length..]
                : getLocal
                    ? $"remotes/{completeName[GitRefName.RefsRemotesPrefix.Length..]}"
                    : completeName[GitRefName.RefsRemotesPrefix.Length..]);
        }

        return branches;
    }

    /// <summary>
    ///  Returns the tags containing <paramref name="commit"/> in git order, or <see langword="null"/> if git must be used.
    /// </summary>
    public IReadOnlyList<string>? GetTagsContaining(ObjectId commit)
    {
        if (!IsComplete || !Refs.CanReproduceTagOrder || _isTaggedCommitMissing || !TryGetCommit(commit, out int index))
        {
            return null;
        }

        bool[] descendants = GetDescendants(index);
        List<string> tags = [];
        foreach (RefSnapshot.TagRef tag in Refs.Tags)
        {
            // Tags of other objects than commits (e.g. trees) are not part of the index, git does not list them either.
            if (_indexById.TryGetValue(tag.Target, out int tagIndex) && descendants[tagIndex])
            {
                tags.Add(tag.Name);
            }
        }

        return tags;
    }

    /// <summary>
    ///  Gets whether <see cref="GetMergedBranches"/> returns a result for <paramref name="commit"/>,
    ///  without computing it.
    /// </summary>
    public bool CanGetMergedBranches(ObjectId commit) => IsComplete && TryGetCommit(commit, out _);

    /// <summary>
    ///  Returns the complete names of the local and remote branches merged into <paramref name="commit"/>
    ///  (<c>git branch -a --merged</c>), or <see langword="null"/> if git must be used.
    /// </summary>
    public IReadOnlyCollection<string>? GetMergedBranches(ObjectId commit)
    {
        if (!CanGetMergedBranches(commit) || !TryGetCommit(commit, out int index))
        {
            return null;
        }

        bool[] ancestors = GetAncestors(index);
        HashSet<string> merged = new(StringComparer.Ordinal);
        foreach ((string completeName, ObjectId tip) in Refs.Branches)
        {
            if (_indexById.TryGetValue(tip, out int tipIndex) && ancestors[tipIndex])
            {
                merged.Add(completeName);
            }
        }

        return merged;
    }

    /// <summary>
    ///  Returns the result of <see cref="GitCommands.Git.IGitDescribeProvider.Get"/> (<c>git describe --tags --first-parent</c>),
    ///  or <see langword="null"/> if git must be used.
    /// </summary>
    /// <remarks>
    ///  git walks the first parents and uses the nearest tagged commit, the count is the first-parent distance.
    ///  On a commit with several tags an annotated tag is preferred over a lightweight one; lightweight tags
    ///  are preferred by refname. Several annotated tags are chosen by tagger date, which is not known here.
    /// </remarks>
    public (string PrecedingTag, string CommitCount)? Describe(ObjectId commit)
    {
        if (!IsComplete || !TryGetCommit(commit, out int index))
        {
            return null;
        }

        for (int distance = 0; index >= 0; distance++, index = _firstParent[index])
        {
            if (!_tagsByCommit.TryGetValue(index, out List<RefSnapshot.TagRef>? tags))
            {
                continue;
            }

            RefSnapshot.TagRef? chosen = null;
            int annotatedCount = 0;
            foreach (RefSnapshot.TagRef tag in tags)
            {
                if (tag.IsAnnotated)
                {
                    annotatedCount++;
                    chosen = tag;
                }
            }

            if (annotatedCount > 1)
            {
                return null;
            }

            chosen ??= tags[0];
            return (chosen.Name, distance == 0 ? string.Empty : distance.ToString(CultureInfo.InvariantCulture));
        }

        // git describe fails, no tag can describe the commit
        return (string.Empty, string.Empty);
    }

    internal static CommitReachabilityIndex Create(
        IReadOnlyList<ObjectId> ids,
        IReadOnlyDictionary<ObjectId, int> indexById,
        IReadOnlyList<ObjectId[]> parentIds,
        RefSnapshot refs,
        int generation)
    {
        int count = ids.Count;
        ObjectId[] idArray = [.. ids];
        Dictionary<ObjectId, int> indexByIdCopy = new(indexById);

        int[] parentStart = new int[count + 1];
        List<int> parents = new(capacity: count + (count / 4));
        int[] firstParent = new int[count];
        int[] childCount = new int[count];
        int unresolvedParentCount = 0;

        for (int i = 0; i < count; i++)
        {
            parentStart[i] = parents.Count;
            firstParent[i] = -1;
            ObjectId[] commitParents = parentIds[i];
            for (int p = 0; p < commitParents.Length; p++)
            {
                if (!indexByIdCopy.TryGetValue(commitParents[p], out int parent))
                {
                    unresolvedParentCount++;
                    continue;
                }

                if (p == 0)
                {
                    firstParent[i] = parent;
                }

                parents.Add(parent);
                childCount[parent]++;
            }
        }

        parentStart[count] = parents.Count;
        int[] parentArray = [.. parents];

        int[] childStart = new int[count + 1];
        for (int i = 0; i < count; i++)
        {
            childStart[i + 1] = childStart[i] + childCount[i];
        }

        int[] children = new int[parentArray.Length];
        int[] fill = new int[count];
        for (int i = 0; i < count; i++)
        {
            for (int p = parentStart[i]; p < parentStart[i + 1]; p++)
            {
                int parent = parentArray[p];
                children[childStart[parent] + fill[parent]++] = i;
            }
        }

        return new CommitReachabilityIndex(idArray, indexByIdCopy, parentStart, parentArray, childStart, children, firstParent, unresolvedParentCount, refs, generation);
    }

    private bool TryGetCommit(ObjectId objectId, out int index)
    {
        index = -1;
        return !objectId.IsZeroOrArtificial && _indexById.TryGetValue(objectId, out index);
    }

    private bool[] GetDescendants(int commit)
        => GetReachable(commit, _childStart, _children, ref _lastDescendants);

    private bool[] GetAncestors(int commit)
        => GetReachable(commit, _parentStart, _parents, ref _lastAncestors);

    private bool[] GetReachable(int commit, int[] start, int[] edges, ref CachedMask? cache)
    {
        CachedMask? cached = Volatile.Read(ref cache);
        if (cached?.Commit == commit)
        {
            return cached.Mask;
        }

        bool[] mask = new bool[_ids.Length];
        int[] stack = new int[_ids.Length];
        int top = 0;
        mask[commit] = true;
        stack[top++] = commit;
        while (top > 0)
        {
            int current = stack[--top];
            for (int e = start[current]; e < start[current + 1]; e++)
            {
                int next = edges[e];
                if (!mask[next])
                {
                    mask[next] = true;
                    stack[top++] = next;
                }
            }
        }

        Volatile.Write(ref cache, new CachedMask(commit, mask));
        return mask;
    }

    private sealed record CachedMask(int Commit, bool[] Mask);
}
