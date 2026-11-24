using System.Diagnostics.CodeAnalysis;
using GitExtensions.Extensibility.Git;
using GitUI.UserControls.RevisionGrid;
using GitUI.UserControls.RevisionGrid.Reachability;
using GitUIPluginInterfaces;

namespace GitUI;

/// <summary>
///  Builds a reachability index from the commits read by git log, see <see cref="CommitReachabilityIndex"/>.
/// </summary>
public sealed partial class RevisionGridControl : ICommitReachabilityProvider
{
    private const string _tagSortSetting = "tag.sort";

    private readonly CommitReachabilityTracker _reachabilityTracker = new();

    bool ICommitReachabilityProvider.TryGetIndex([NotNullWhen(true)] out CommitReachabilityIndex? index)
        => _reachabilityTracker.TryGetIndex(out index);

    Task<CommitReachabilityIndex?> ICommitReachabilityProvider.WaitForIndexAsync(CancellationToken cancellationToken)
        => _reachabilityTracker.WaitForIndexAsync(cancellationToken);

    bool ICommitReachabilityProvider.IsCurrent(CommitReachabilityIndex index)
        => _reachabilityTracker.IsCurrent(index);

    private void BeginReachabilityLoad(int dataLoadId, IGitModule module, FilterInfo filterInfo, Lazy<IReadOnlyList<IGitRef>> getRefs)
    {
        _reachabilityTracker.Begin(
            dataLoadId,
            ReachabilityLoadOptions.FromFilter(filterInfo),
            () => RefSnapshot.Read(getRefs.Value, module.WorkingDirGitDir, module.GitCommonDirectory, module.GetEffectiveSetting(_tagSortSetting)),
            module.WorkingDir);
    }

    /// <remarks>
    ///  Called with the revisions as read by git log, before stashes are inserted and their parents are changed.
    /// </remarks>
    private void AddToReachabilityIndex(int dataLoadId, IReadOnlyList<GitRevision> revisions)
    {
        _reachabilityTracker.Add(dataLoadId, revisions);
    }

    /// <remarks>
    ///  Called if the load fails or ends without revisions.
    /// </remarks>
    private void EndReachabilityLoad(int dataLoadId)
    {
        _reachabilityTracker.EndWithoutIndex(dataLoadId);
    }

    private void CompleteReachabilityLoad(int dataLoadId)
    {
        _reachabilityTracker.Complete(dataLoadId);
    }
}
