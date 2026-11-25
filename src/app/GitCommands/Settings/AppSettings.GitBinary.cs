using GitCommands.Settings;

namespace GitCommands;

public static partial class AppSettings
{
    /// <summary>
    ///  Gets or sets whether git is started via the real binary of the installation (for example
    ///  <c>ucrt64\bin\git.exe</c>) instead of the launcher in <c>cmd\git.exe</c>, <c>bin\git.exe</c> or a scoop shim.
    /// </summary>
    public static BooleanSetting UseDirectGitBinary { get; } = new(RootSettingsPath, nameof(UseDirectGitBinary), defaultValue: true);
}
