using GitCommands;
using GitCommands.Git.Broker;
using GitCommands.Settings;
using GitExtensions.Extensibility.Settings;
using GitExtUtils.GitUI;

namespace GitUI.CommandsDialogs.SettingsDialog.Pages;

/// <summary>
///  The settings of the performance features, created without designer.
/// </summary>
internal sealed partial class PerformanceSettingsPage : SettingsPageWithHeader
{
    private const int IndentationWidth = 20;
    private const int HintWidth = 700;
    private const int NumberWidth = 80;

    private readonly GroupBox _gbGitProcesses = new() { Name = nameof(_gbGitProcesses), Text = "Git processes" };
    private readonly GroupBox _gbMemory = new() { Name = nameof(_gbMemory), Text = "Memory" };
    private readonly GroupBox _gbStatus = new() { Name = nameof(_gbStatus), Text = "Status" };
    private readonly GroupBox _gbFsMonitor = new() { Name = nameof(_gbFsMonitor), Text = "File system monitor" };
    private readonly CheckBox _chkUseDirectGitBinary = new() { Name = nameof(_chkUseDirectGitBinary), Text = "Start the git binary directly instead of its launcher" };
    private readonly Label _lblUseDirectGitBinaryHint = new() { Name = nameof(_lblUseDirectGitBinaryHint), Text = "Saves one or two processes per git command. Disable this if git behaves differently than on the command line, e.g. if hooks do not find their tools." };
    private readonly CheckBox _chkUseGitBroker = new() { Name = nameof(_chkUseGitBroker), Text = "Read file contents with a persistent git process" };
    private readonly Label _lblUseGitBrokerHint = new() { Name = nameof(_lblUseGitBrokerHint), Text = "Saves git processes when viewing files. The process is ended if it is not used for 15 seconds. Disable this if it hinders other programs from changing the repository." };
    private readonly CheckBox _chkUseGitBrokerForAttributes = new() { Name = nameof(_chkUseGitBrokerForAttributes), Text = "Read git attributes with a persistent git process, too" };
    private readonly Label _lblUseGitBrokerForAttributesHint = new() { Name = nameof(_lblUseGitBrokerForAttributesHint), Text = "Saves a git process per viewed file. The process is ended if it is not used for 3 seconds. The folder of the repository cannot be moved or deleted while it is running." };
    private readonly Label _lblRepoSnapshotMemoryLimit = new() { Name = nameof(_lblRepoSnapshotMemoryLimit), Text = "Limit for the config and the stashes of recent repositories (MB, 0 = off)" };
    private readonly Label _lblRepoSnapshotMemoryLimitHint = new() { Name = nameof(_lblRepoSnapshotMemoryLimitHint), Text = "Saves git processes when switching back to a repository. The data is used only if it is still up to date. Set this to 0 in order to read it again each time." };
    private readonly Label _lblGitBrokerMemoryLimit = new() { Name = nameof(_lblGitBrokerMemoryLimit), Text = "Limit for file contents read by the persistent git process (MB, 0 = off)" };
    private readonly Label _lblGitBrokerMemoryLimitHint = new() { Name = nameof(_lblGitBrokerMemoryLimitHint), Text = "Files which are viewed again need not be read again. A lower limit takes effect when a file is viewed the next time." };
    private readonly CheckBox _chkRefreshIndexAfterSlowStatus = new() { Name = nameof(_chkRefreshIndexAfterSlowStatus), Text = "Allow the status update to refresh the index if it was slow" };
    private readonly Label _lblRefreshIndexAfterSlowStatusHint = new() { Name = nameof(_lblRefreshIndexAfterSlowStatusHint), Text = "Saves git from comparing the contents of touched files with each status update. Done at most every 5 minutes and only if no other git command is running. A git command which is started at the same moment in another window can fail because of index.lock. Disable this if that happens." };
    private readonly CheckBox _chkStopIdleFsMonitorDaemons = new() { Name = nameof(_chkStopIdleFsMonitorDaemons), Text = "Stop the daemons of repositories which have not been used recently" };
    private readonly Label _lblStopIdleFsMonitorDaemonsHint = new() { Name = nameof(_lblStopIdleFsMonitorDaemonsHint), Text = "Saves about 9 MB of memory per daemon. Only for repositories opened in this session. If another program uses the repository, its next git command starts the daemon again and takes longer once. Disable this if that is a problem." };
    private readonly Label _lblMaxFsMonitorDaemons = new() { Name = nameof(_lblMaxFsMonitorDaemons), Text = "Number of daemons to keep" };
    private readonly NumericUpDown _numRepoSnapshotMemoryLimit = new() { Name = nameof(_numRepoSnapshotMemoryLimit), Maximum = 65536 };
    private readonly NumericUpDown _numGitBrokerMemoryLimit = new() { Name = nameof(_numGitBrokerMemoryLimit), Maximum = 65536 };
    private readonly NumericUpDown _numMaxFsMonitorDaemons = new() { Name = nameof(_numMaxFsMonitorDaemons), Maximum = 100 };

    private readonly TableLayoutPanel _panel = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Dock = DockStyle.Top };
    private readonly (CheckBox Control, BooleanSetting Setting)[] _booleans;
    private readonly (NumericUpDown Control, IntegerSetting Setting)[] _integers;

    public PerformanceSettingsPage(IServiceProvider serviceProvider)
        : base(serviceProvider)
    {
        _booleans =
        [
            (_chkUseDirectGitBinary, AppSettings.UseDirectGitBinary),
            (_chkUseGitBroker, AppSettings.UseGitBroker),
            (_chkUseGitBrokerForAttributes, AppSettings.UseGitBrokerForAttributes),
            (_chkRefreshIndexAfterSlowStatus, AppSettings.RefreshIndexAfterSlowStatus),
            (_chkStopIdleFsMonitorDaemons, AppSettings.StopIdleFsMonitorDaemons),
        ];
        _integers =
        [
            (_numRepoSnapshotMemoryLimit, AppSettings.RepoSnapshotMemoryLimit),
            (_numGitBrokerMemoryLimit, AppSettings.GitBrokerMemoryLimit),
            (_numMaxFsMonitorDaemons, AppSettings.MaxFsMonitorDaemons),
        ];

        InitializeLayout();
        InitializeComplete();

        _chkUseGitBroker.CheckedChanged += (_, _) => UpdateEnabled();
        _chkStopIdleFsMonitorDaemons.CheckedChanged += (_, _) => UpdateEnabled();
    }

    public static SettingsPageReference GetPageReference()
    {
        return new SettingsPageReferenceByType(typeof(PerformanceSettingsPage));
    }

    protected override void SettingsToPage()
    {
        foreach ((CheckBox control, BooleanSetting setting) in _booleans)
        {
            control.Checked = setting.Value;
        }

        foreach ((NumericUpDown control, IntegerSetting setting) in _integers)
        {
            control.Value = GetValidValue(control, setting);
        }

        UpdateEnabled();

        base.SettingsToPage();
    }

    protected override void PageToSettings()
    {
        bool isPersistentProcessDisabled = IsDisabled(_chkUseGitBroker, AppSettings.UseGitBroker)
            || IsDisabled(_chkUseGitBrokerForAttributes, AppSettings.UseGitBrokerForAttributes);

        // Keep the defaults as well as values out of range unless they have been changed
        foreach ((CheckBox control, BooleanSetting setting) in _booleans)
        {
            if (control.Checked != setting.Value)
            {
                setting.Value = control.Checked;
            }
        }

        foreach ((NumericUpDown control, IntegerSetting setting) in _integers)
        {
            if (control.Value != GetValidValue(control, setting))
            {
                setting.Value = (int)control.Value;
            }
        }

        if (isPersistentProcessDisabled)
        {
            GitBroker.ReleaseAll();
        }

        base.PageToSettings();

        return;

        static bool IsDisabled(CheckBox control, BooleanSetting setting) => setting.Value && !control.Checked;
    }

    private static decimal GetValidValue(NumericUpDown control, IntegerSetting setting)
        => Math.Clamp(setting.Value, control.Minimum, control.Maximum);

    private void UpdateEnabled()
    {
        _chkUseGitBrokerForAttributes.Enabled = _chkUseGitBroker.Checked;
        _numMaxFsMonitorDaemons.Enabled = _chkStopIdleFsMonitorDaemons.Checked;
    }

    private void InitializeLayout()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScroll = true;
        Name = nameof(PerformanceSettingsPage);
        Padding = new Padding(8);
        Text = "Performance";

        AddGroup(_gbGitProcesses, [
            new(_chkUseDirectGitBinary, Input: null, _lblUseDirectGitBinaryHint, Indentation: 0),
            new(_chkUseGitBroker, Input: null, _lblUseGitBrokerHint, Indentation: 0),
            new(_chkUseGitBrokerForAttributes, Input: null, _lblUseGitBrokerForAttributesHint, Indentation: 1),
        ]);
        AddGroup(_gbMemory, [
            new(_lblRepoSnapshotMemoryLimit, _numRepoSnapshotMemoryLimit, _lblRepoSnapshotMemoryLimitHint, Indentation: 0),
            new(_lblGitBrokerMemoryLimit, _numGitBrokerMemoryLimit, _lblGitBrokerMemoryLimitHint, Indentation: 0),
        ]);
        AddGroup(_gbStatus, [
            new(_chkRefreshIndexAfterSlowStatus, Input: null, _lblRefreshIndexAfterSlowStatusHint, Indentation: 0),
        ]);
        AddGroup(_gbFsMonitor, [
            new(_chkStopIdleFsMonitorDaemons, Input: null, _lblStopIdleFsMonitorDaemonsHint, Indentation: 0),
            new(_lblMaxFsMonitorDaemons, _numMaxFsMonitorDaemons, null, Indentation: 1),
        ]);

        Controls.Add(_panel);
        ResumeLayout(performLayout: true);
    }

    private void AddGroup(GroupBox groupBox, Row[] rows)
    {
        TableLayoutPanel table = new()
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Dock = DockStyle.Fill
        };

        foreach (Row row in rows)
        {
            int left = DpiUtil.Scale(IndentationWidth) * row.Indentation;
            row.Caption.AutoSize = true;
            row.Caption.Anchor = AnchorStyles.Left;
            row.Caption.Margin = new Padding(left + 3, 6, 3, 3);
            table.Controls.Add(row.Caption, column: 0, table.RowCount);
            if (row.Input is null)
            {
                table.SetColumnSpan(row.Caption, 2);
            }
            else
            {
                row.Input.Anchor = AnchorStyles.Left;
                row.Input.Width = DpiUtil.Scale(NumberWidth);
                table.Controls.Add(row.Input, column: 1, table.RowCount);
            }

            table.RowCount++;
            if (row.Hint is null)
            {
                continue;
            }

            row.Hint.AutoSize = true;
            row.Hint.ForeColor = SystemColors.GrayText;
            row.Hint.Margin = new Padding(left + DpiUtil.Scale(IndentationWidth), 0, 3, 3);
            row.Hint.MaximumSize = new Size(DpiUtil.Scale(HintWidth), 0);
            table.Controls.Add(row.Hint, column: 0, table.RowCount);
            table.SetColumnSpan(row.Hint, 2);
            table.RowCount++;
        }

        groupBox.AutoSize = true;
        groupBox.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        groupBox.Dock = DockStyle.Top;
        groupBox.Controls.Add(table);
        _panel.Controls.Add(groupBox, column: 0, _panel.RowCount++);
    }

    private sealed record Row(Control Caption, Control? Input, Label? Hint, int Indentation);
}
