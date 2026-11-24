using GitCommands.Git.Broker;
using GitExtensions.Extensibility.Git;

namespace GitUI.Editor;

public partial class FileViewer
{
    /// <summary>
    ///  Sets the tree id of a file in a commit without starting a git process.
    /// </summary>
    private bool TryUpdateTreeIdFromBroker(GitItemStatus file, ObjectId commitId, CancellationToken cancellationToken)
    {
        if (!GitBroker.TryGetFileTreeId(Module, commitId, file.Name, out ObjectId treeId, out bool isSubmodule, cancellationToken))
        {
            return false;
        }

        file.IsSubmodule = isSubmodule;
        file.TreeId = treeId;
        CacheTreeId(Module.WorkingDir, commitId, file.Name, treeId, isSubmodule);
        return true;
    }
}
