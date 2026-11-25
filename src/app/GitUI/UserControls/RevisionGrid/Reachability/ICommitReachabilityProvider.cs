using System.Diagnostics.CodeAnalysis;

namespace GitUI.UserControls.RevisionGrid.Reachability;

/// <summary>
///  Provides the <see cref="CommitReachabilityIndex"/> of the current revision grid load.
/// </summary>
internal interface ICommitReachabilityProvider
{
    /// <summary>
    ///  Gets the index of the current load if the load has completed and the index can be used.
    /// </summary>
    bool TryGetIndex([NotNullWhen(true)] out CommitReachabilityIndex? index);

    /// <summary>
    ///  Waits (for a limited time) until the current load has completed.
    /// </summary>
    /// <returns>The index, or <see langword="null"/> if it cannot be used or the wait timed out.</returns>
    Task<CommitReachabilityIndex?> WaitForIndexAsync(CancellationToken cancellationToken);

    /// <summary>
    ///  Gets whether <paramref name="index"/> is still the index of the current load.
    /// </summary>
    bool IsCurrent(CommitReachabilityIndex index);
}
