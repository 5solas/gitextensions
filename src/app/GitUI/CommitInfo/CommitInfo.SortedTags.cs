using GitCommands;
using GitCommands.Git;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;

namespace GitUI.CommitInfo;

public partial class CommitInfo
{
    private bool _refsProvidedByHost;
    private Func<RefsFilter, IReadOnlyList<IGitRef>>? _refsSource;

    public void SetRefsProvidedByHost() => _refsProvidedByHost = true;

    public void SetRefs(Func<RefsFilter, IReadOnlyList<IGitRef>>? refsSource)
    {
        _refsSource = refsSource;
        TriggerRefreshSortedTags();
    }

    private IDictionary<string, int> GetSortedTagsOrFromRefs()
    {
        if (_refsSource is not null)
        {
            IDictionary<string, int>? tagsOrderDict = TryGetSortedTagsFromRefs(_refsSource);
            if (tagsOrderDict is not null)
            {
                return tagsOrderDict;
            }
        }

        return GetSortedTags();
    }

    internal static IDictionary<string, int>? TryGetSortedTagsFromRefs(Func<RefsFilter, IReadOnlyList<IGitRef>> refsSource)
    {
        IReadOnlyList<IGitRef> refs = refsSource(RefsFilter.Tags);
        if (refs.Count == 0)
        {
            return new Dictionary<string, int>();
        }

        bool hasTaggerDate = false;
        List<(string Key, DateTimeOffset? Date)> tagItems = new(refs.Count);

        foreach (IGitRef r in refs)
        {
            if (!r.IsTag)
            {
                continue;
            }

            if (r is not GitRef concreteRef)
            {
                return null;
            }

            if (concreteRef.TaggerDate is not null)
            {
                hasTaggerDate = true;
            }

            string completeName = concreteRef.CompleteName;
            string key = completeName.EndsWith(GitRefName.TagDereferenceSuffix, StringComparison.Ordinal)
                ? completeName[..^GitRefName.TagDereferenceSuffix.Length]
                : completeName;

            tagItems.Add((key, concreteRef.TaggerDate));
        }

        if (!hasTaggerDate)
        {
            return null;
        }

        tagItems.Sort(static (a, b) =>
        {
            if (a.Date is not null && b.Date is not null)
            {
                int cmp = b.Date.Value.CompareTo(a.Date.Value);
                if (cmp != 0)
                {
                    return cmp;
                }
            }
            else if (a.Date is not null)
            {
                return -1;
            }
            else if (b.Date is not null)
            {
                return 1;
            }

            return string.Compare(a.Key, b.Key, StringComparison.Ordinal);
        });

        int i = 0;
        Dictionary<string, int> dict = [];
        foreach ((string key, _) in tagItems)
        {
            if (dict.TryAdd(key, i))
            {
                ++i;
            }
        }

        return dict;
    }
}
