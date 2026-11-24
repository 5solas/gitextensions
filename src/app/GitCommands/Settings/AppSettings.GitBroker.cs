using GitCommands.Settings;

namespace GitCommands;

public static partial class AppSettings
{
    /// <summary>
    ///  Gets or sets whether file contents are read by a persistent <c>git cat-file</c> process instead of one process per file.
    /// </summary>
    public static BooleanSetting UseGitBroker { get; } = new(RootSettingsPath, nameof(UseGitBroker), defaultValue: true);

    /// <summary>
    ///  Gets or sets the memory limit in MB for file contents read by the persistent git process. 0 disables the cache.
    /// </summary>
    public static IntegerSetting GitBrokerMemoryLimit { get; } = new(RootSettingsPath, nameof(GitBrokerMemoryLimit), defaultValue: 512);
}
