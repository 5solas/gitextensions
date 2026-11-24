using GitCommands;
using GitCommands.RepoSnapshot;

namespace GitCommandsTests.RepoSnapshot;

/// <summary>
///  Isolates a test from the stored data of other tests and restores the changed setting.
/// </summary>
internal sealed class RepoSnapshotTestScope : IDisposable
{
    // The stored value: null if the setting is not set, so the settings file does not get an entry
    private readonly string? _originalMemoryLimit = AppSettings.GetString(AppSettings.RepoSnapshotMemoryLimit.FullPath, defaultValue: null);

    public RepoSnapshotTestScope()
    {
        RepoSnapshotStore.Default.Clear();
    }

    public void Dispose()
    {
        AppSettings.SetString(AppSettings.RepoSnapshotMemoryLimit.FullPath, _originalMemoryLimit!);
        RepoSnapshotStore.Default.Clear();
    }
}
