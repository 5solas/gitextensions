using GitCommands.Settings;

namespace GitCommands;

public static partial class AppSettings
{
    /// <summary>
    ///  Gets or sets whether the status monitor may let git write the refreshed index (which needs its lock)
    ///  after a slow status update. Otherwise git has to compare the content of touched files each time.
    /// </summary>
    public static BooleanSetting RefreshIndexAfterSlowStatus { get; } = new(RootSettingsPath, nameof(RefreshIndexAfterSlowStatus), defaultValue: true);
}
