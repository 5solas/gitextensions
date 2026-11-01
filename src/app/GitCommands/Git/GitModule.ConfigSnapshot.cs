// Logic for reading remotes and settings via GitConfigSnapshot.
// Separated to minimize upstream merge conflicts in GitModule.cs.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using GitCommands.Settings;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitExtUtils;

namespace GitCommands;

public sealed partial class GitModule
{
    private GitConfigSnapshotProvider? _configSnapshotProvider;

    internal GitConfigSnapshotProvider ConfigSnapshotProvider
    {
        get
        {
            if (_configSnapshotProvider is null)
            {
                lock (_lock)
                {
                    _configSnapshotProvider ??= new GitConfigSnapshotProvider(GitExecutable);
                }
            }

            return _configSnapshotProvider;
        }
    }

    private bool TryGetRemoteNamesFromSnapshot([NotNullWhen(true)] out IReadOnlyList<string>? remoteNames)
    {
        GitConfigSnapshot snapshot = ConfigSnapshotProvider.GetSnapshot();
        if (snapshot.CachedRemoteNames is { } cached)
        {
            remoteNames = cached;
            return true;
        }

        if (HasLegacyRemoteDirs())
        {
            remoteNames = null;
            return false;
        }

        IReadOnlyList<string> computed = snapshot.RemoteNames;
        snapshot.CachedRemoteNames = computed;
        remoteNames = computed;
        return true;
    }

    private bool TryGetRemotesFromSnapshot([NotNullWhen(true)] out IReadOnlyList<Remote>? remotes)
    {
        GitConfigSnapshot snapshot = ConfigSnapshotProvider.GetSnapshot();
        if (snapshot.CachedRemotes is { } cached)
        {
            remotes = cached;
            return true;
        }

        if (snapshot.HasInsteadOf || HasLegacyRemoteDirs())
        {
            remotes = null;
            return false;
        }

        IReadOnlyList<Remote> computedRemotes = ComputeRemotesFromSnapshot(snapshot);
        snapshot.CachedRemotes = computedRemotes;
        remotes = computedRemotes;
        return true;
    }

    private IReadOnlyList<Remote> ComputeRemotesFromSnapshot(GitConfigSnapshot snapshot)
    {
        List<Remote> remotes = [];

        foreach (string remoteName in snapshot.RemoteNames)
        {
            IReadOnlyList<string> fetchUrls = snapshot.GetEffectiveValues($"remote.{remoteName}.url");
            if (fetchUrls.Count == 0)
            {
                continue;
            }

            string fetchUrl = fetchUrls[0];
            if (PathUtil.IsLocalFile(fetchUrl))
            {
                fetchUrl = GetWindowsPath(fetchUrl).ToPosixPath();
            }

            IReadOnlyList<string> pushUrlsFromConfig = snapshot.GetEffectiveValues($"remote.{remoteName}.pushurl");
            List<string> pushUrls;
            if (pushUrlsFromConfig.Count == 0)
            {
                pushUrls = [fetchUrl];
            }
            else
            {
                pushUrls = new List<string>(pushUrlsFromConfig.Count);
                for (int i = 0; i < pushUrlsFromConfig.Count; i++)
                {
                    string pushUrl = pushUrlsFromConfig[i];
                    if (PathUtil.IsLocalFile(pushUrl))
                    {
                        pushUrl = GetWindowsPath(pushUrl).ToPosixPath();
                    }

                    pushUrls.Add(pushUrl);
                }
            }

            Remote remote = new(remoteName, fetchUrl, pushUrls[0]);
            for (int i = 1; i < pushUrls.Count; i++)
            {
                remote.PushUrls.Add(pushUrls[i]);
            }

            remotes.Add(remote);
        }

        return remotes;
    }

    private bool HasLegacyRemoteDirs()
    {
        string gitDir = WorkingDirGitDir;
        if (string.IsNullOrEmpty(gitDir))
        {
            return false;
        }

        try
        {
            string commonDirFile = Path.Combine(gitDir, "commondir");
            string commonDir;
            if (File.Exists(commonDirFile))
            {
                string firstLine = File.ReadLines(commonDirFile).FirstOrDefault()?.Trim() ?? "";
                if (firstLine.Length > 0)
                {
                    commonDir = Path.IsPathRooted(firstLine)
                        ? firstLine
                        : Path.GetFullPath(Path.Combine(gitDir, firstLine));
                }
                else
                {
                    commonDir = gitDir;
                }
            }
            else
            {
                commonDir = gitDir;
            }

            string remotesDir = Path.Combine(commonDir, "remotes");
            if (Directory.Exists(remotesDir) && Directory.EnumerateFileSystemEntries(remotesDir).Any())
            {
                return true;
            }

            string branchesDir = Path.Combine(commonDir, "branches");
            if (Directory.Exists(branchesDir) && Directory.EnumerateFileSystemEntries(branchesDir).Any())
            {
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error checking legacy remote directories: {ex.Message}");
            return false;
        }
    }
}
