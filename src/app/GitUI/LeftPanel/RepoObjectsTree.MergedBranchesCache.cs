using GitCommands;
using GitExtensions.Extensibility.Git;
using GitUIPluginInterfaces;

namespace GitUI.LeftPanel;

public sealed partial class RepoObjectsTree
{
    // Skips SelectionChanged when the same commit is selected again with the same refs (e.g. switching
    // the tabs below the grid re-raises the selection). Cancellation only happens when another commit is
    // selected or the module changes, which already changes or clears the key, so it needs no extra reset.
    private readonly record struct MergedBranchesCacheKey(ObjectId? CommitId, int RefsVersion);

    private MergedBranchesCacheKey? _lastMergedBranchesKey;
    private int _refsVersion;

    private bool TryBeginMergedBranchesUpdate(IReadOnlyList<GitRevision> selectedRevisions, out MergedBranchesCacheKey key)
    {
        ObjectId? commitId = null;
        if (selectedRevisions.Count > 0)
        {
            GitRevision revision = selectedRevisions[0];
            if (revision.IsArtificial)
            {
                ObjectId currentCheckout = _revisionGridInfo?.CurrentCheckout is { IsZero: false } id ? id : Module.GetCurrentCheckout();
                commitId = currentCheckout.IsZero ? null : currentCheckout;
            }
            else
            {
                commitId = revision.ObjectId.IsZero ? null : revision.ObjectId;
            }
        }

        key = new(commitId, _refsVersion);
        if (_lastMergedBranchesKey == key)
        {
            return false;
        }

        _lastMergedBranchesKey = key;
        return true;
    }

    private void ResetMergedBranchesCache()
    {
        _refsVersion++;
        _lastMergedBranchesKey = null;
    }

    private void ResetMergedBranchesKey(MergedBranchesCacheKey key)
    {
        if (_lastMergedBranchesKey == key)
        {
            _lastMergedBranchesKey = null;
        }
    }
}
