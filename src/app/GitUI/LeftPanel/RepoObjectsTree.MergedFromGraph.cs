using GitCommands;
using GitExtensions.Extensibility.Git;
using GitUI.UserControls.RevisionGrid.Reachability;
using GitUIPluginInterfaces;
using Microsoft.VisualStudio.Threading;

namespace GitUI.LeftPanel;

/// <summary>
///  Marks the merged branches using the reachability index of the revision grid instead of
///  running <c>git branch -a --merged</c>.
/// </summary>
/// <remarks>
///  The index is built from the refs of the grid load: refs hidden by GE are not part of the tree anyway,
///  refs changed outside GE are seen after the next grid refresh.
/// </remarks>
public sealed partial class RepoObjectsTree
{
    /// <summary>
    ///  Updates the merged branches from the reachability index of the revision grid if the index of the current
    ///  grid load is available now and can answer for the selected commit (the left panel does not wait for it).
    /// </summary>
    /// <returns><see langword="false"/> if the merged branches must be updated by <see cref="SelectionChanged"/> using git.</returns>
    private bool TryApplyMergedBranchesFromGraph(MergedBranchesCacheKey key, IReadOnlyList<GitRevision> selectedRevisions, CancellationToken cancellationToken)
    {
        if (_revisionGridInfo is not ICommitReachabilityProvider provider
            || selectedRevisions.Count == 0
            || key.CommitId is not ObjectId commitId
            || !provider.TryGetIndex(out CommitReachabilityIndex? index)
            || !provider.IsCurrent(index)
            || !index.CanGetMergedBranches(commitId))
        {
            return false;
        }

        GitRevision selectedRevision = selectedRevisions[0];

        ThreadHelper.FileAndForget(async () =>
        {
            await TaskScheduler.Default;
            cancellationToken.ThrowIfCancellationRequested();

            // Not null, see CanGetMergedBranches()
            HashSet<string> mergedBranches = new(index.GetMergedBranches(commitId)!, StringComparer.Ordinal);

            // Same as SelectionChanged()
            selectedRevision.Refs.ForEach(gitRef => mergedBranches.Remove(gitRef.CompleteName));

            await treeMain.SwitchToMainThreadAsync(cancellationToken);

            try
            {
                treeMain.BeginUpdate();
                cancellationToken.ThrowIfCancellationRequested();
                _branchesTree?.DepthEnumerator<BaseBranchLeafNode>().ForEach(node => node.IsMerged = mergedBranches.Contains(GitRefName.RefsHeadsPrefix + node.FullPath));
                cancellationToken.ThrowIfCancellationRequested();
                _remotesTree?.DepthEnumerator<BaseBranchLeafNode>().ForEach(node => node.IsMerged = mergedBranches.Contains(GitRefName.RefsRemotesPrefix + node.FullPath));
            }
            finally
            {
                treeMain.EndUpdate();
            }
        });

        return true;
    }
}
