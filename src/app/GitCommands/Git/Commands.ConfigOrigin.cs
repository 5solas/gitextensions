using GitCommands.RepoSnapshot;
using GitCommands.Settings;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitExtUtils;

namespace GitCommands.Git;

public static partial class Commands
{
    /// <summary>
    ///  Get all effective config settings from git with their scope and the file they are from.
    /// </summary>
    /// <param name="gitExecutable">The <see cref="IGitExecutor.GitExecutable"/> for the affected repo.</param>
    /// <returns>The settings, <see langword="null"/> if the command failed.</returns>
    internal static IReadOnlyList<GitConfigEntryOrigin>? GetGitSettingsWithOrigin(this IExecutable gitExecutable)
    {
        GitArgumentBuilder args = new("config")
        {
            gitExecutable.SupportNewGitConfigSyntax() ? "list" : "--list",
            "--show-scope",
            "--show-origin",
            "--includes",
            "--null"
        };
        ExecutionResult result = gitExecutable.Execute(args, throwOnErrorExit: false);
        return result.ExitedSuccessfully ? ParseGitSettingsWithOrigin(result.StandardOutput) : null;
    }

    internal static IReadOnlyList<GitConfigEntryOrigin> ParseGitSettingsWithOrigin(string output)
    {
        List<GitConfigEntryOrigin> entries = [];
        ReadOnlySpan<char> span = output.AsSpan().TrimEnd('\0');
        if (span.IsEmpty)
        {
            return entries;
        }

        string? scope = null;
        string? origin = null;
        foreach (Range range in span.Split('\0'))
        {
            ReadOnlySpan<char> token = span[range];
            if (scope is null)
            {
                scope = token.ToString();
            }
            else if (origin is null)
            {
                origin = token.ToString();
            }
            else
            {
                // Same as GitConfigSnapshot.Parse()
                int linefeedIndex = token.IndexOf('\n');
                string key = (linefeedIndex >= 0 ? token[..linefeedIndex] : token).ToString();
                string value = linefeedIndex >= 0 ? token[(linefeedIndex + 1)..].ToString() : "true";
                entries.Add(new GitConfigEntryOrigin(new GitConfigEntry(scope, key, value), origin));
                scope = null;
                origin = null;
            }
        }

        return entries;
    }
}
