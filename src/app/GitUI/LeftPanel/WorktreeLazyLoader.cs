using System.Runtime.CompilerServices;
using GitExtensions.Extensibility.Git;

namespace GitUI.LeftPanel;

/// <summary>
///  Avoids <c>git worktree list</c> as long as the node of the worktrees is collapsed and there is only the main worktree.
/// </summary>
internal sealed class WorktreeLazyLoader(WorktreeTree tree)
{
    // A superseded load can end late, so do not remember the result of a load but which nodes have not been read by git
    private readonly ConditionalWeakTable<Node, object?> _lazyNodes = [];
    private TreeView? _treeView;
    private volatile bool _isCollapsed;

    /// <summary>
    ///  Gets whether the displayed worktree has not been read by git, unless this has already been requested.
    ///  Must be called on the UI thread.
    /// </summary>
    internal bool IsLazy => _isCollapsed && tree.Nodes.Any(node => _lazyNodes.TryGetValue(node, out _));

    /// <summary>
    ///  Must be called on the UI thread when a reload is started.
    /// </summary>
    public void OnRefresh()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

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
        WorktreeNode node = new(tree, worktree, isCurrent: true, Path.GetRelativePath(parentDir, worktree.Path));
        _lazyNodes.Add(node, null);
        nodes.AddNode(node);
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
        if (IsLazy && tree.TreeViewNode.IsExpanded)
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
