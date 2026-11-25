namespace GitUI.CommandsDialogs.BrowseDialog;

internal enum StatusUpdateState
{
    /// <summary>No status can be expected in the near future.</summary>
    None,

    /// <summary>The command will be started soon.</summary>
    Scheduled,

    /// <summary>The command is running.</summary>
    Running
}
