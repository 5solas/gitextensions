using GitExtensions.Extensibility.Git;

namespace GitUI.LeftPanel;

/// <summary>
///  Avoids <c>git worktree list</c> as long as the node of the worktrees is collapsed and there is only the main worktree.
/// </summary>
internal sealed class WorktreeLazyLoader(WorktreeTree tree)
{
    private TreeView? _treeView;
    private volatile bool _isCollapsed;
    private volatile bool _isLazy;

    /// <summary>
    ///  Must be called on the UI thread when a reload is started.
    /// </summary>
    public void OnRefresh()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        _isLazy = false;
        _isCollapsed = !tree.TreeViewNode.IsExpanded;
    }

    /// <summary>
    ///  Creates the node of the only worktree without running git, if possible.
    /// </summary>
    public Nodes? TryLoad(IGitModule module)
    {
        if (!_isCollapsed
            || !MainWorktreeReader.TryRead(module, out GitWorktree? worktree)
            || Path.GetDirectoryName(worktree.Path) is not string parentDir)
        {
            return null;
        }

        Nodes nodes = new(tree);
        nodes.AddNode(new WorktreeNode(tree, worktree, isCurrent: true, Path.GetRelativePath(parentDir, worktree.Path)));
        _isLazy = true;
        return nodes;
    }

    public void Attach()
    {
        Detach();
        _treeView = tree.TreeViewNode.TreeView;
        if (_treeView is not null)
        {
            _treeView.AfterExpand += OnAfterExpand;
        }
    }

    public void Detach()
    {
        if (_treeView is not null)
        {
            _treeView.AfterExpand -= OnAfterExpand;
            _treeView = null;
        }
    }

    /// <summary>
    ///  Loads the worktrees if the node is or has been expanded.
    /// </summary>
    public void LoadIfExpanded()
    {
        if (_isLazy && tree.TreeViewNode.IsExpanded)
        {
            tree.Refresh();
        }
    }

    private void OnAfterExpand(object? sender, TreeViewEventArgs e)
    {
        if (e.Node == tree.TreeViewNode)
        {
            LoadIfExpanded();
        }
    }
}
