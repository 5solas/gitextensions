using GitCommands.Settings;

namespace GitCommands;

public static partial class AppSettings
{
    /// <summary>
    ///  Gets or sets the memory limit in MB for the data of recently used repositories, which is reused when switching
    ///  back to them if it is still valid. 0 disables the reuse.
    /// </summary>
    public static IntegerSetting RepoSnapshotMemoryLimit { get; } = new(RootSettingsPath, nameof(RepoSnapshotMemoryLimit), defaultValue: 1024);
}
