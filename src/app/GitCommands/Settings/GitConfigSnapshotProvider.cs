using System.Diagnostics;
using GitCommands.Git;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitUI;

namespace GitCommands.Settings;

public sealed class GitConfigSnapshotProvider(IExecutable gitExecutable)
{
    private const int BackgroundReloadDelayMs = 250;

    public IExecutable GitExecutable { get; } = gitExecutable;

    private readonly Lock _lock = new();
    private volatile GitConfigSnapshot? _currentSnapshot;
    private volatile bool _isValid;
    private int _generation;

    public GitConfigSnapshot CurrentSnapshot => GetSnapshot();

    public GitConfigSnapshot GetSnapshot()
    {
        if (_isValid && _currentSnapshot is not null)
        {
            return _currentSnapshot;
        }

        lock (_lock)
        {
            if (_isValid && _currentSnapshot is not null)
            {
                return _currentSnapshot;
            }

            // If Invalidate() runs while git is reading the config, the result may predate the change:
            // return it to this caller but do not publish it as valid, so the next read loads again.
            int generation = Volatile.Read(ref _generation);
            GitConfigSnapshot snapshot = LoadSnapshot();
            if (generation == Volatile.Read(ref _generation))
            {
                _currentSnapshot = snapshot;
                _isValid = true;
            }

            return snapshot;
        }
    }

    public void Invalidate()
    {
        int generation = Interlocked.Increment(ref _generation);
        _isValid = false;
        _currentSnapshot = null;

        ThreadHelper.FileAndForget(async () =>
        {
            await Task.Delay(BackgroundReloadDelayMs);

            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }

            if (_isValid && _currentSnapshot is not null)
            {
                return;
            }

            if (Path.Exists(GitExecutable.WorkingDir))
            {
                try
                {
                    GetSnapshot();
                }
                catch (ExternalOperationException ex)
                {
                    Trace.WriteLine($"{nameof(GitConfigSnapshotProvider)}: Background update failed:\n{ex}");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"{nameof(GitConfigSnapshotProvider)}: Background update failed:\n{ex}");
                }
            }
        });
    }

    private GitConfigSnapshot LoadSnapshot()
    {
        if (GitExecutable.SupportConfigShowScope())
        {
            string output = GitExecutable.GetGitSettingsWithScope();
            return GitConfigSnapshot.Parse(output);
        }

        string effectiveOutput = GitExecutable.GetGitSettings(GitSettingLevel.Effective);
        string localOutput = GitExecutable.GetGitSettings(GitSettingLevel.Local);
        return GitConfigSnapshot.CreateFromLegacy(effectiveOutput, localOutput);
    }
}
