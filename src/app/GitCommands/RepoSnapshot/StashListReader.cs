using System.Diagnostics.CodeAnalysis;
using System.Text;
using GitCommands.Settings;
using GitExtensions.Extensibility.Git;
using GitUIPluginInterfaces;

namespace GitCommands.RepoSnapshot;

/// <summary>
///  Reads the stashes for the revision grid. The stashes of the previous time are reused if git would list the same again.
/// </summary>
/// <remarks>
///  The stash commits are immutable, so the output of "git stash list" depends on the reflog of refs/stash,
///  on the files and refs which modify how commits are read, and on the config. All of them are compared.
/// </remarks>
public static class StashListReader
{
    private const string ReplaceRefsPrefix = "refs/replace/";

    private static readonly string[] _configSections = ["core.", "extensions.", "format.", "gpg.", "i18n.", "log.", "mailmap.", "notes.", "stash.", "user."];

    /// <summary>
    ///  Gets the same as <see cref="RevisionReader.GetStashes"/>.
    /// </summary>
    /// <param name="module">The git module.</param>
    /// <param name="refs">The unfiltered refs of the repository.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>New instances, which can be changed by the caller.</returns>
    public static IReadOnlyCollection<GitRevision> GetStashes(IGitModule module, IReadOnlyList<IGitRef> refs, CancellationToken cancellationToken)
    {
        IGitRef? stashRef = refs.FirstOrDefault(r => r.CompleteName == GitRefName.RefsStashPrefix);
        if (stashRef is null)
        {
            return [];
        }

        // Reads the config if it is not loaded
        RevisionReader reader = new(module);

        RepoSnapshotStore store = RepoSnapshotStore.Default;
        bool hasKey = RepoSnapshotStore.TryGetKey(module.WorkingDir, out string? repoKey);
        if (!store.IsEnabled || !hasKey || module is not GitModule gitModule)
        {
            return reader.GetStashes(cancellationToken);
        }

        State? state = TryReadState(gitModule, stashRef, refs);
        if (state is not null && store.TryGet(repoKey!, out StashSnapshotPart? part) && part.State.Equals(state))
        {
            return part.CloneStashes();
        }

        IReadOnlyCollection<GitRevision> stashes = reader.GetStashes(cancellationToken);

        // The output is not complete if git was killed or failed, do not store it
        if (state is not null
            && !cancellationToken.IsCancellationRequested
            && stashes.Count == state.StashCount
            && state.Equals(TryReadState(gitModule, stashRef, refs)))
        {
            store.Set(repoKey!, new StashSnapshotPart(state, stashes));
        }

        return stashes;
    }

    /// <summary>
    ///  Reads everything which the list of the stashes depends on, without running git.
    /// </summary>
    /// <returns><see langword="null"/> if it cannot be determined.</returns>
    private static State? TryReadState(GitModule module, IGitRef stashRef, IReadOnlyList<IGitRef> refs)
    {
        if (!module.ConfigSnapshotProvider.TryGetVerifiedPart(out ConfigSnapshotPart? config)
            || !GitConfigFiles.TryResolve(module.WorkingDir, out GitConfigFiles? directories)
            || !TryGetFiles(directories, config.Snapshot, out List<string>? paths, out string? stashLog)
            || FileFingerprint.TryRead(paths) is not FileFingerprint files

            // e.g. the reftable backend
            || !files.IsExisting(stashLog)
            || !TryCountLines(stashLog, out int stashCount))
        {
            return null;
        }

        StringBuilder key = new();
        key.Append(config.Environment).Append('\n').Append(stashRef.ObjectId).Append('\n');
        foreach (IGitRef replaceRef in refs.Where(r => r.CompleteName.StartsWith(ReplaceRefsPrefix, StringComparison.Ordinal)))
        {
            key.Append(replaceRef.CompleteName).Append(' ').Append(replaceRef.ObjectId).Append('\n');
        }

        foreach (GitConfigEntry entry in config.Snapshot.Entries)
        {
            if (_configSections.Any(section => entry.Key.StartsWith(section, StringComparison.OrdinalIgnoreCase)))
            {
                key.Append(entry.Key).Append('=').Append(entry.Value).Append('\n');
            }
        }

        return new State(key.ToString(), files, stashCount);
    }

    private static bool TryGetFiles(
        GitConfigFiles directories,
        GitConfigSnapshot config,
        [NotNullWhen(true)] out List<string>? paths,
        [NotNullWhen(true)] out string? stashLog)
    {
        stashLog = Path.Combine(directories.CommonDir, "logs", "refs", "stash");
        paths =
        [
            stashLog,
            Path.Combine(directories.CommonDir, "info", "grafts"),
            Path.Combine(directories.CommonDir, "shallow"),
            Path.Combine(directories.WorkingDir, ".mailmap")
        ];

        if (config.GetEffectiveValue("mailmap.blob") is not null)
        {
            return false;
        }

        if (config.GetEffectiveValue("mailmap.file") is string mailmap)
        {
            if (!GitConfigFiles.TryGetFullPath(mailmap, directories.WorkingDir, out string? mailmapFile))
            {
                return false;
            }

            paths.Add(mailmapFile);
        }

        return true;
    }

    private static bool TryCountLines(string path, out int count)
    {
        try
        {
            count = File.ReadLines(path).Count();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            count = 0;
            return false;
        }
    }

    private sealed record State(string Key, FileFingerprint Files, int StashCount)
    {
        public bool Equals(State? other)
            => other is not null && Key == other.Key && StashCount == other.StashCount && Files.IsSame(other.Files);

        public override int GetHashCode() => Key.GetHashCode();
    }

    private sealed class StashSnapshotPart : IRepoSnapshotPart
    {
        // The grid changes the revisions (parents, refs, ...): keep own copies
        private readonly GitRevision[] _stashes;

        public StashSnapshotPart(State state, IEnumerable<GitRevision> stashes)
        {
            State = state;
            _stashes = [.. stashes.Select(stash => stash.Clone())];
            EstimatedBytes = (2L * state.Key.Length) + state.Files.EstimatedBytes + _stashes.Sum(EstimateBytes);
        }

        public State State { get; }

        public long EstimatedBytes { get; }

        public GitRevision[] CloneStashes() => [.. _stashes.Select(stash => stash.Clone())];

        private static long EstimateBytes(GitRevision stash)
            => 500L + (2L * ((stash.Subject?.Length ?? 0) + (stash.Body?.Length ?? 0) + (stash.Notes?.Length ?? 0)));
    }
}
