using System.Globalization;
using GitCommands.Git;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;

namespace GitCommands;

public partial class GitModule
{
    public IReadOnlyList<IGitRef> GetRefs(RefsFilter getRef, bool withTaggerDate)
    {
        const bool noLocks = true;

        ArgumentString cmd = Commands.GetRefs(getRef, noLocks, AppSettings.RefsSortBy, AppSettings.RefsSortOrder, withTaggerDate: withTaggerDate);
        ExecutionResult result = GitExecutable.Execute(cmd, throwOnErrorExit: false);
        return result.ExitedSuccessfully
            ? ParseRefs(result.StandardOutput)
            : Array.Empty<IGitRef>();
    }

    private static (string RefName, DateTimeOffset? TaggerDate) ParseRefNameAndTaggerDate(string rawRefName)
    {
        int nulIndex = rawRefName.IndexOf('\0');
        if (nulIndex < 0)
        {
            return (rawRefName, null);
        }

        string refName = rawRefName[..nulIndex];
        string taggerDateStr = rawRefName[(nulIndex + 1)..].TrimEnd('\r');
        DateTimeOffset? taggerDate = long.TryParse(taggerDateStr, NumberStyles.None, CultureInfo.InvariantCulture, out long unixSeconds)
            ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds)
            : null;

        return (refName, taggerDate);
    }
}
