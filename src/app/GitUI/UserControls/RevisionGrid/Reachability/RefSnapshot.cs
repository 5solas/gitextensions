using System.Text;
using GitCommands;
using GitExtensions.Extensibility.Git;

namespace GitUI.UserControls.RevisionGrid.Reachability;

/// <summary>
///  The state of <c>HEAD</c> as git reports it in <c>git branch --contains</c>.
/// </summary>
/// <param name="IsKnown">Whether the state could be determined without running git.</param>
/// <param name="DetachedAt">The commit of a detached <c>HEAD</c>, <see langword="null"/> if <c>HEAD</c> is attached to a branch.</param>
internal readonly record struct HeadState(bool IsKnown, ObjectId? DetachedAt)
{
    public static HeadState Unknown => new(IsKnown: false, DetachedAt: null);

    public static HeadState Attached => new(IsKnown: true, DetachedAt: null);

    public static HeadState Detached(ObjectId commit) => new(IsKnown: true, DetachedAt: commit);
}

/// <summary>
///  An immutable snapshot of the branches and tags of a revision grid load, ordered like git lists them.
/// </summary>
/// <remarks>
///  The refs are the ones of the grid (<c>git for-each-ref</c> of GE): refs which GE hides (e.g. the session refs
///  patterns) are not part of the snapshot and are therefore not reported, unlike the git commands this replaces.
/// </remarks>
internal sealed class RefSnapshot
{
    private const string _symbolicRefPrefix = "ref:";
    private const string _headFileName = "HEAD";

    public RefSnapshot(
        IEnumerable<IGitRef> refs,
        HeadState head,
        IEnumerable<KeyValuePair<string, ObjectId>>? additionalBranches = null,
        bool canReproduceBranches = true,
        bool canReproduceTagOrder = true)
        : this(refs.Select(r => new KeyValuePair<string, ObjectId>(r.CompleteName, r.ObjectId)), head, additionalBranches, canReproduceBranches, canReproduceTagOrder)
    {
    }

    public RefSnapshot(
        IEnumerable<KeyValuePair<string, ObjectId>> refs,
        HeadState head,
        IEnumerable<KeyValuePair<string, ObjectId>>? additionalBranches = null,
        bool canReproduceBranches = true,
        bool canReproduceTagOrder = true)
    {
        Dictionary<string, ObjectId> branches = new(StringComparer.Ordinal);
        Dictionary<string, TagRef> tags = new(StringComparer.Ordinal);

        foreach ((string completeName, ObjectId objectId) in refs)
        {
            if (objectId.IsZero)
            {
                continue;
            }

            if (completeName.StartsWith(GitRefName.RefsHeadsPrefix, StringComparison.Ordinal)
                || completeName.StartsWith(GitRefName.RefsRemotesPrefix, StringComparison.Ordinal))
            {
                branches.TryAdd(completeName, objectId);
            }
            else if (completeName.StartsWith(GitRefName.RefsTagsPrefix, StringComparison.Ordinal))
            {
                // GE lists an annotated tag (or any tag pointing to a tag object) once, as "refs/tags/name^{}"
                // with the id of the peeled object, see the format of Commands.GetRefs().
                bool isAnnotated = completeName.EndsWith(GitRefName.TagDereferenceSuffix, StringComparison.Ordinal);
                string name = completeName[GitRefName.RefsTagsPrefix.Length..(isAnnotated ? completeName.Length - GitRefName.TagDereferenceSuffix.Length : completeName.Length)];
                tags.TryAdd(name, new TagRef(name, objectId, isAnnotated));
            }
        }

        if (additionalBranches is not null)
        {
            foreach ((string completeName, ObjectId objectId) in additionalBranches)
            {
                if (!objectId.IsZero)
                {
                    branches.TryAdd(completeName, objectId);
                }
            }
        }

        Branches = [.. branches.OrderBy(b => b.Key, GitRefNameComparer.Instance)];
        Tags = [.. tags.Values.OrderBy(t => t.Name, GitRefNameComparer.Instance)];
        Head = head;
        CanReproduceBranches = canReproduceBranches;
        CanReproduceTagOrder = canReproduceTagOrder;
    }

    /// <summary>
    ///  Gets the local and remote branches (complete names) with their tip commits, in git order (refname, byte-wise).
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, ObjectId>> Branches { get; }

    /// <summary>
    ///  Gets the tags with their peeled target, in git order (refname, byte-wise).
    /// </summary>
    public IReadOnlyList<TagRef> Tags { get; }

    public HeadState Head { get; }

    /// <summary>
    ///  Gets whether the output of <c>git branch --contains</c> can be reproduced (e.g. not for the reftable backend).
    /// </summary>
    public bool CanReproduceBranches { get; }

    /// <summary>
    ///  Gets whether the order of <c>git tag --contains</c> can be reproduced (not if <c>tag.sort</c> is configured).
    /// </summary>
    public bool CanReproduceTagOrder { get; }

    /// <summary>
    ///  Creates the snapshot for the refs of a grid load, reading <c>HEAD</c> and the remote <c>HEAD</c> symbolic refs
    ///  from the repository files (no git process is started).
    /// </summary>
    /// <param name="refs">The refs of the grid load (unfiltered).</param>
    /// <param name="gitDirectory">The git directory of the worktree (contains <c>HEAD</c>).</param>
    /// <param name="commonDirectory">The common git directory (contains <c>refs/</c> and <c>packed-refs</c>).</param>
    /// <param name="tagSortSetting">The effective value of <c>tag.sort</c>.</param>
    public static RefSnapshot Read(IReadOnlyList<IGitRef> refs, string gitDirectory, string commonDirectory, string? tagSortSetting)
    {
        bool canReproduceTagOrder = string.IsNullOrWhiteSpace(tagSortSetting);

        // With the reftable backend neither HEAD nor the remote HEADs can be read from files.
        if (string.IsNullOrEmpty(gitDirectory) || string.IsNullOrEmpty(commonDirectory)
            || Directory.Exists(Path.Combine(commonDirectory, "reftable")))
        {
            return new RefSnapshot(refs, HeadState.Unknown, canReproduceBranches: false, canReproduceTagOrder: canReproduceTagOrder);
        }

        HeadState head = ReadHead(gitDirectory);
        Dictionary<string, ObjectId>? remoteHeads = ReadRemoteHeads(refs, commonDirectory, out bool remoteHeadsKnown);
        return new RefSnapshot(refs, head, remoteHeads, canReproduceBranches: head.IsKnown && remoteHeadsKnown, canReproduceTagOrder: canReproduceTagOrder);
    }

    private static HeadState ReadHead(string gitDirectory)
    {
        try
        {
            string content = File.ReadAllText(Path.Combine(gitDirectory, _headFileName)).Trim();
            if (content.StartsWith(_symbolicRefPrefix, StringComparison.Ordinal))
            {
                return HeadState.Attached;
            }

            return ObjectId.TryParse(content, out ObjectId objectId) ? HeadState.Detached(objectId) : HeadState.Unknown;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return HeadState.Unknown;
        }
    }

    /// <summary>
    ///  GE removes "refs/remotes/{remote}/HEAD" from its refs if a branch of the remote points to the same commit
    ///  (see GitModule.ParseRefs()), but git lists it. Find these refs in the files of the files backend.
    /// </summary>
    private static Dictionary<string, ObjectId>? ReadRemoteHeads(IReadOnlyList<IGitRef> refs, string commonDirectory, out bool isKnown)
    {
        isKnown = true;
        Dictionary<string, ObjectId> tipByName = new(StringComparer.Ordinal);
        HashSet<string> candidateHeads = new(StringComparer.Ordinal);
        foreach (IGitRef gitRef in refs)
        {
            string name = gitRef.CompleteName;
            if (!name.StartsWith(GitRefName.RefsRemotesPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            tipByName.TryAdd(name, gitRef.ObjectId);
            for (int slash = name.IndexOf('/', GitRefName.RefsRemotesPrefix.Length); slash >= 0; slash = name.IndexOf('/', slash + 1))
            {
                candidateHeads.Add($"{name[..slash]}/{_headFileName}");
            }
        }

        Dictionary<string, ObjectId> result = new(StringComparer.Ordinal);
        try
        {
            foreach (string candidate in candidateHeads)
            {
                if (tipByName.ContainsKey(candidate))
                {
                    continue;
                }

                string path = Path.Combine(commonDirectory, candidate.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path))
                {
                    continue;
                }

                string content = File.ReadAllText(path).Trim();
                if (content.StartsWith(_symbolicRefPrefix, StringComparison.Ordinal))
                {
                    string target = content[_symbolicRefPrefix.Length..].Trim();

                    // A symbolic ref to a missing branch is a broken ref, git ignores it.
                    if (tipByName.TryGetValue(target, out ObjectId targetTip))
                    {
                        result[candidate] = targetTip;
                    }
                }
                else if (ObjectId.TryParse(content, out ObjectId objectId))
                {
                    result[candidate] = objectId;
                }
                else
                {
                    isKnown = false;
                }
            }

            // Symbolic refs are never packed, a packed remote HEAD is a regular ref.
            string packedRefsPath = Path.Combine(commonDirectory, "packed-refs");
            if (File.Exists(packedRefsPath))
            {
                foreach (string line in File.ReadLines(packedRefsPath))
                {
                    int separator = line.IndexOf(' ');
                    if (separator < 0 || line.StartsWith('#') || line.StartsWith('^'))
                    {
                        continue;
                    }

                    string name = line[(separator + 1)..];
                    if (candidateHeads.Contains(name)
                        && !tipByName.ContainsKey(name)
                        && !result.ContainsKey(name)
                        && !File.Exists(Path.Combine(commonDirectory, name.Replace('/', Path.DirectorySeparatorChar)))
                        && ObjectId.TryParse(line[..separator], out ObjectId objectId))
                    {
                        result[name] = objectId;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            isKnown = false;
        }

        return result;
    }

    /// <summary>
    ///  A tag with its peeled target.
    /// </summary>
    /// <param name="Name">The tag name without "refs/tags/".</param>
    /// <param name="Target">The id of the peeled object, usually a commit.</param>
    /// <param name="IsAnnotated">Whether the ref points to a tag object.</param>
    internal sealed record TagRef(string Name, ObjectId Target, bool IsAnnotated);

    /// <summary>
    ///  Compares ref names like git does (strcmp of the UTF-8 bytes).
    /// </summary>
    internal sealed class GitRefNameComparer : IComparer<string>
    {
        public static GitRefNameComparer Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            return Encoding.UTF8.GetBytes(x).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(y));
        }
    }
}
