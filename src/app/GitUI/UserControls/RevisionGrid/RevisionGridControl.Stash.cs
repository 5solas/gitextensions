using System.Diagnostics;
using GitCommands;
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
            if (!getUnfilteredRefs.Value.Any(r => r.CompleteName == GitRefName.RefsStashPrefix))
            {
                return [];
            }

            return new RevisionReader(capturedModule).GetStashes(cancellationToken);
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex.Message);
            return [];
        }
    }
}
