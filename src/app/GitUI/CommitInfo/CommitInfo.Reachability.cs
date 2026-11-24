using System.Net;
using System.Text;
using GitCommands;
using GitExtensions.Extensibility.Git;
using GitUI.UserControls.RevisionGrid.Reachability;
using ResourceManager;

namespace GitUI.CommitInfo;

/// <summary>
///  Gets the branches and tags containing the revision and the tag it derives from from the reachability index of the
///  revision grid instead of running <c>git branch --contains</c>, <c>git tag --contains</c> and <c>git describe</c>.
/// </summary>
/// <remarks>
///  The index is built from the refs of the grid load: refs hidden by GE (e.g. session refs) are not listed,
///  unlike in the git output, and refs changed outside GE are seen after the next grid refresh.
/// </remarks>
public partial class CommitInfo
{
    private ICommitReachabilityProvider? _reachabilityProvider;

    /// <summary>
    ///  Sets the provider of the reachability index of the revision grid showing the revisions of this control.
    /// </summary>
    internal void SetReachabilityProvider(ICommitReachabilityProvider? provider)
    {
        _reachabilityProvider = provider;
    }

    private async Task<bool> TryLoadTagsFromGraphAsync(ObjectId objectId, CancellationToken cancellationToken)
    {
        CommitReachabilityIndex? index = await GetReachabilityIndexAsync(cancellationToken);
        IReadOnlyList<string>? tags = index?.GetTagsContaining(objectId);
        if (tags is null || !IsCurrentReachabilityIndex(index!))
        {
            return false;
        }

        await this.SwitchToMainThreadAsync(cancellationToken);
        _tags = [.. tags];
        return true;
    }

    private async Task<bool> TryLoadBranchesFromGraphAsync(ObjectId objectId, CancellationToken cancellationToken)
    {
        // Same as LoadBranchInfoAsync()
        bool getLocal = AppSettings.CommitInfoShowContainedInBranchesLocal ||
                        AppSettings.CommitInfoShowContainedInBranchesRemoteIfNoLocal;
        bool getRemote = AppSettings.CommitInfoShowContainedInBranchesRemote ||
                         AppSettings.CommitInfoShowContainedInBranchesRemoteIfNoLocal;

        CommitReachabilityIndex? index = await GetReachabilityIndexAsync(cancellationToken);
        IReadOnlyList<string>? branches = index?.GetBranchesContaining(objectId, getLocal, getRemote);
        if (branches is null || !IsCurrentReachabilityIndex(index!))
        {
            return false;
        }

        await this.SwitchToMainThreadAsync(cancellationToken);
        _branches = [.. branches];
        return true;
    }

    private async Task<bool> TryLoadDescribeFromGraphAsync(ObjectId commitId, ILinkFactory linkFactory, CancellationToken cancellationToken)
    {
        CommitReachabilityIndex? index = await GetReachabilityIndexAsync(cancellationToken);
        (string PrecedingTag, string CommitCount)? describe = index?.Describe(commitId);
        if (describe is not (string precedingTag, string commitCount) || !IsCurrentReachabilityIndex(index!))
        {
            return false;
        }

        // Same as GetDescribeInfoForRevision()
        StringBuilder gitDescribeInfo = new();
        if (!string.IsNullOrEmpty(precedingTag))
        {
            string tagString = ShowBranchesAsLinks ? linkFactory.CreateTagLink(precedingTag) : WebUtility.HtmlEncode(precedingTag);
            gitDescribeInfo.Append(WebUtility.HtmlEncode(_derivesFromTag.Text)).Append(' ').Append(tagString);
            if (!string.IsNullOrEmpty(commitCount))
            {
                gitDescribeInfo.Append(" + ").Append(commitCount).Append(' ').Append(WebUtility.HtmlEncode(_plusCommits.Text));
            }
        }
        else
        {
            gitDescribeInfo.Append(WebUtility.HtmlEncode(_derivesFromNoTag.Text));
        }

        await this.SwitchToMainThreadAsync(cancellationToken);
        _gitDescribeInfo = gitDescribeInfo.ToString();
        return true;
    }

    private async Task<CommitReachabilityIndex?> GetReachabilityIndexAsync(CancellationToken cancellationToken)
    {
        ICommitReachabilityProvider? provider = _reachabilityProvider;
        if (provider is null)
        {
            return null;
        }

        if (provider.TryGetIndex(out CommitReachabilityIndex? index))
        {
            return index;
        }

        // The grid is still loading: wait for a limited time, then use git.
        return await provider.WaitForIndexAsync(cancellationToken);
    }

    private bool IsCurrentReachabilityIndex(CommitReachabilityIndex index)
        => _reachabilityProvider?.IsCurrent(index) is true;
}
