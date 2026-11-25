using GitCommands.Settings;

namespace GitCommands;

public static partial class AppSettings
{
    /// <summary>
    ///  Gets or sets whether the <c>git fsmonitor--daemon</c> of repositories which have not been used recently is stopped.
    /// </summary>
    public static BooleanSetting StopIdleFsMonitorDaemons { get; } = new(RootSettingsPath, nameof(StopIdleFsMonitorDaemons), defaultValue: true);

    /// <summary>
    ///  Gets or sets the number of the most recently used repositories whose <c>git fsmonitor--daemon</c> is kept.
    /// </summary>
    public static IntegerSetting MaxFsMonitorDaemons { get; } = new(RootSettingsPath, nameof(MaxFsMonitorDaemons), defaultValue: 5);
}
