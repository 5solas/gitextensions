using System.Diagnostics;
using GitCommands.RepoSnapshot;
using GitExtensions.Extensibility.Git;
using GitUIPluginInterfaces;

namespace GitUI;

public sealed partial class RevisionGridControl
{
    private static IReadOnlyCollection<GitRevision> GetStashesIfRefExists(
        IGitModule capturedModule,
        Lazy<IReadOnlyList<IGitRef>> getUnfilteredRefs,
        CancellationToken cancellationToken)
    {
        try
        {
            return StashListReader.GetStashes(capturedModule, getUnfilteredRefs.Value, cancellationToken);
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex.Message);
            return [];
        }
    }
}
