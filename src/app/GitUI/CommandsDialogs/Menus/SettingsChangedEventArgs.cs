using GitCommands;

namespace GitUI.CommandsDialogs.Menus;

public class SettingsChangedEventArgs : EventArgs
{
    public SettingsChangedEventArgs(string oldTranslation, CommitInfoPosition oldCommitInfoPosition)
        : this(oldTranslation, oldCommitInfoPosition, FormBrowse.CaptureConsoleSettings())
    {
    }

    internal SettingsChangedEventArgs(string oldTranslation, CommitInfoPosition oldCommitInfoPosition, FormBrowse.ConsoleSettingsSnapshot oldConsoleSettings)
    {
        OldTranslation = oldTranslation;
        OldCommitInfoPosition = oldCommitInfoPosition;
        OldConsoleSettings = oldConsoleSettings;
    }

    public CommitInfoPosition OldCommitInfoPosition { get; }
    public string OldTranslation { get; }
    internal FormBrowse.ConsoleSettingsSnapshot OldConsoleSettings { get; }
}
