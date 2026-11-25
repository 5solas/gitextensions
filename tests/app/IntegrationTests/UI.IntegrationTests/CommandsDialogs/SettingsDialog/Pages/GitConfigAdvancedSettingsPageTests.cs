using CommonTestUtils;
using GitCommands;
using GitCommands.Logging;
using GitExtensions.Extensibility.Settings;
using GitExtensions.UITests;
using GitUI.CommandsDialogs.SettingsDialog;
using GitUI.CommandsDialogs.SettingsDialog.Pages;

namespace UITests.CommandsDialogs.SettingsDialog.Pages;

[Apartment(ApartmentState.STA)]
[NonParallelizable]
public sealed class GitConfigAdvancedSettingsPageTests
{
    [TestCase(100, "Not recommended: small repository (index 0")]
    [TestCase(2 * 1024 * 1024, "Recommended: large repository on a local drive (index 2")]
    public void Advice_should_be_shown_for_fsmonitor(int indexBytes, string expectedText)
    {
        using ReferenceRepository repository = new();
        repository.Module.GitExecutable.GetOutput("--version");
        using UITestThreadExceptions threadExceptions = new();
        MockHost? host = null;

        UITest.RunForm(
            () =>
            {
                host = new MockHost(repository.Module) { Size = new Size(1200, 600) };
                GitConfigAdvancedSettingsPage page = SettingsPageBase.Create<GitConfigAdvancedSettingsPage>(host, GitExtensions.UITests.GlobalServiceContainer.CreateDefaultMockServiceContainer());
                page.Dock = DockStyle.Fill;
                host.Page = page;
                host.Controls.Add(page);
                host.ShowDialog(owner: null);
            },
            async (MockHost form) =>
            {
                using (FileStream index = new(Path.Combine(repository.Module.WorkingDirGitDir, "index"), FileMode.Open))
                {
                    index.SetLength(indexBytes);
                }

                // Is it a bare repository? This is known by the application.
                repository.Module.IsBareRepository().Should().BeFalse();
                CommandLog.Clear();

                form.Page!.OnPageShown();
                form.Page.OnPageShown();
                await AsyncTestHelper.JoinPendingOperationsAsync(AsyncTestHelper.UnexpectedTimeout);

                Label label = form.Controls.Find("_NO_TRANSLATE_lblFsMonitorAdvice", searchAllChildren: true).Cast<Label>().Single();
                label.Text.Should().StartWith(expectedText);
                TableLayoutPanel panel = (TableLayoutPanel)label.Parent!;
                panel.GetRow(label).Should().Be(panel.GetRow(panel.Controls["lblCoreFsMonitor"]!));
                label.Visible.Should().BeTrue();
                CommandLog.Commands.Should().BeEmpty();
            });

        threadExceptions.ThrowIfAny();
    }

    private sealed class MockHost(GitModule module) : Form, ISettingsPageHost
    {
        public GitConfigAdvancedSettingsPage? Page;

        public CheckSettingsLogic CheckSettingsLogic { get; } = new(new CommonLogic(module));

        public void GotoPage(SettingsPageReference settingsPageReference) => throw new NotSupportedException();

        public void LoadAll() => throw new NotSupportedException();

        public void SaveAll() => throw new NotSupportedException();
    }
}
