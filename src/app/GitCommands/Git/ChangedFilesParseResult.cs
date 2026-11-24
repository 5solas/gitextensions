using GitExtensions.Extensibility.Git;

namespace GitCommands.Git;

/// <summary>
/// Represents the result of parsing git status output, including changed files and unmerged entries flag.
/// </summary>
public readonly record struct ChangedFilesParseResult(IReadOnlyList<GitItemStatus> Items, bool HasUnmergedEntries)
{
    public IReadOnlyList<GitItemStatus> Files => Items;
}
