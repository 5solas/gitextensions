using System.Collections.Concurrent;
using GitExtensions.Extensibility.Git;
using GitUI.UserControls;

namespace GitUI.Editor;

public partial class FileViewer
{
    private static readonly ConcurrentDictionary<(string WorkingDir, ObjectId CommitId, string FileName), (ObjectId TreeId, bool IsSubmodule)> _treeCache = new();

    internal static bool TryGetCachedTreeId(string workingDir, ObjectId commitId, string fileName, out ObjectId treeId, out bool isSubmodule)
    {
        if (string.IsNullOrEmpty(workingDir) || commitId.IsZeroOrArtificial || string.IsNullOrEmpty(fileName))
        {
            treeId = default;
            isSubmodule = false;
            return false;
        }

        if (_treeCache.TryGetValue((workingDir, commitId, fileName), out (ObjectId TreeId, bool IsSubmodule) entry))
        {
            treeId = entry.TreeId;
            isSubmodule = entry.IsSubmodule;
            return true;
        }

        treeId = default;
        isSubmodule = false;
        return false;
    }

    internal static void CacheTreeId(string workingDir, ObjectId commitId, string fileName, ObjectId treeId, bool isSubmodule)
    {
        if (!string.IsNullOrEmpty(workingDir) && !commitId.IsZeroOrArtificial && !string.IsNullOrEmpty(fileName))
        {
            _treeCache[(workingDir, commitId, fileName)] = (treeId, isSubmodule);
        }
    }

    private async Task ViewGitItemBackgroundAsync(
        GitItemStatus file,
        ObjectId objectId,
        FileStatusItem? item,
        int? line,
        Action? openWithDifftool,
        CancellationToken cancellationToken)
    {
        await Task.Run(() => GetUpdateTreeId(file, objectId, cancellationToken), cancellationToken);

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        await ViewGitItemAsync(file, objectId, item, line, openWithDifftool, cancellationToken, allowBackgroundFetch: false);
    }
}
