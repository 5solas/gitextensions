using GitExtensions.Extensibility.Git;

namespace GitUI.CommandsDialogs.BrowseDialog;

public class GitWorkingDirectoryStatusEventArgs : EventArgs
{
    private readonly IReadOnlyList<GitItemStatus> _itemStatuses = [];

    public GitWorkingDirectoryStatusEventArgs(IReadOnlyList<GitItemStatus> itemStatuses, bool hasUnmergedEntries = false)
    {
        _itemStatuses = itemStatuses;
        HasUnmergedEntries = hasUnmergedEntries;
    }

    public GitWorkingDirectoryStatusEventArgs()
    {
    }

    /// <summary>
    /// Gets a value indicating whether unmerged entries (merge conflicts) exist.
    /// </summary>
    public bool HasUnmergedEntries { get; }

    /// <summary>
    /// the status of all modified files/submodules or null if the previous information is invalidated.
    /// </summary>
    public IReadOnlyList<GitItemStatus>? ItemStatuses => _itemStatuses ?? [];
}
