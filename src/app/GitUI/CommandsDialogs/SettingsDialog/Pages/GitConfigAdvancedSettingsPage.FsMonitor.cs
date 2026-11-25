using GitExtensions.Extensibility.Git;
using GitExtUtils.GitUI;
using GitUI.FsMonitor;
using Microsoft.VisualStudio.Threading;
using ResourceManager;

namespace GitUI.CommandsDialogs.SettingsDialog.Pages;

public partial class GitConfigAdvancedSettingsPage
{
    private readonly TranslationString _fsMonitorRecommended = new("Recommended: {0}");
    private readonly TranslationString _fsMonitorNotRecommended = new("Not recommended: {0}");
    private readonly TranslationString _fsMonitorNotSupported = new("Not supported: {0}");
    private readonly TranslationString _fsMonitorLarge = new("large repository on a local drive (index {0:N1} MB).");
    private readonly TranslationString _fsMonitorSmall = new("small repository (index {0:N1} MB), the daemon would need about 9 MB of memory for little effect.");
    private readonly TranslationString _fsMonitorSubmodules = new("many submodules, the daemon also has to handle their changes.");
    private readonly TranslationString _fsMonitorNotFixedDrive = new("not a fixed drive.");
    private readonly TranslationString _fsMonitorNetworkDrive = new("network drive or WSL.");
    private readonly TranslationString _fsMonitorBare = new("bare repository.");

    private Label? _fsMonitorAdviceLabel;

    /// <summary>
    ///  Shows whether a file system monitor is suitable for the current repository.
    /// </summary>
    public override void OnPageShown()
    {
        base.OnPageShown();

        if (Module is not IGitModule module || !module.IsValidGitWorkingDir())
        {
            return;
        }

        this.InvokeAndForget(async () =>
        {
            await TaskScheduler.Default;
            (FsMonitorAdvice Advice, long IndexBytes)? result = FsMonitorAdvisor.Evaluate(module.WorkingDir, module.WorkingDirGitDir, isBare: module.IsBareRepository());
            await this.SwitchToMainThreadAsync();
            ShowFsMonitorAdvice(result);
        });
    }

    private void ShowFsMonitorAdvice((FsMonitorAdvice Advice, long IndexBytes)? result)
    {
        if (_fsMonitorAdviceLabel is null)
        {
            _fsMonitorAdviceLabel = new Label
            {
                Anchor = AnchorStyles.Left,
                AutoSize = true,
                MaximumSize = new Size(DpiUtil.Scale(400), 0),
                Name = "_NO_TRANSLATE_lblFsMonitorAdvice"
            };
            tlpnlMain.ColumnCount++;
            tlpnlMain.ColumnStyles.Add(new ColumnStyle());
            tlpnlMain.Controls.Add(_fsMonitorAdviceLabel, column: tlpnlMain.ColumnCount - 1, tlpnlMain.GetRow(lblCoreFsMonitor));
        }

        _fsMonitorAdviceLabel.Text = result is null ? "" : GetText(result.Value.Advice, result.Value.IndexBytes / (1024.0 * 1024.0));

        return;

        string GetText(FsMonitorAdvice advice, double indexMegabytes)
            => advice switch
            {
                FsMonitorAdvice.Recommended => string.Format(_fsMonitorRecommended.Text, string.Format(_fsMonitorLarge.Text, indexMegabytes)),
                FsMonitorAdvice.SmallRepository => string.Format(_fsMonitorNotRecommended.Text, string.Format(_fsMonitorSmall.Text, indexMegabytes)),
                FsMonitorAdvice.ManySubmodules => string.Format(_fsMonitorNotRecommended.Text, _fsMonitorSubmodules.Text),
                FsMonitorAdvice.NotFixedDrive => string.Format(_fsMonitorNotRecommended.Text, _fsMonitorNotFixedDrive.Text),
                FsMonitorAdvice.NetworkDrive => string.Format(_fsMonitorNotSupported.Text, _fsMonitorNetworkDrive.Text),
                FsMonitorAdvice.BareRepository => string.Format(_fsMonitorNotSupported.Text, _fsMonitorBare.Text),
                _ => ""
            };
    }
}
