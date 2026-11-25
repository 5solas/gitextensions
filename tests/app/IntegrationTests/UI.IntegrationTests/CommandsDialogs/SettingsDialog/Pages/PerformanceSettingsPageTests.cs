using CommonTestUtils;
using GitCommands;
using GitCommands.Git.Broker;
using GitCommands.Settings;
using GitExtensions.Extensibility.Git;
using GitExtensions.Extensibility.Settings;
using GitExtensions.Extensibility.Translations.Xliff;
using GitExtensions.UITests;
using GitUI;
using GitUI.CommandsDialogs;
using GitUI.CommandsDialogs.SettingsDialog;
using GitUI.CommandsDialogs.SettingsDialog.Pages;

namespace UITests.CommandsDialogs.SettingsDialog.Pages;

[Apartment(ApartmentState.STA)]
[NonParallelizable]
public sealed class PerformanceSettingsPageTests
{
    private static readonly BooleanSetting[] _booleans =
    [
        AppSettings.UseDirectGitBinary,
        AppSettings.UseGitBroker,
        AppSettings.UseGitBrokerForAttributes,
        AppSettings.RefreshIndexAfterSlowStatus,
        AppSettings.StopIdleFsMonitorDaemons
    ];

    private static readonly IntegerSetting[] _integers =
    [
        AppSettings.RepoSnapshotMemoryLimit,
        AppSettings.GitBrokerMemoryLimit,
        AppSettings.MaxFsMonitorDaemons
    ];

    private readonly Dictionary<string, string?> _originalValues = [];
    private ReferenceRepository _repository = null!;

    private static IEnumerable<string> Keys => _booleans.Select(s => s.FullPath).Concat(_integers.Select(s => s.FullPath));

    [SetUp]
    public void SetUp()
    {
        _repository = new ReferenceRepository();
        _repository.Module.GitExecutable.GetOutput("--version");
        foreach (string key in Keys)
        {
            _originalValues[key] = AppSettings.GetString(key, defaultValue: null);
            AppSettings.SetString(key, null!);
        }
    }

    [TearDown]
    public void TearDown()
    {
        foreach ((string key, string? value) in _originalValues)
        {
            AppSettings.SetString(key, value!);
        }

        GitBroker.ReleaseAll();
        UITest.ProcessUntil("Persistent processes ended", () => GitBrokerStats.RunningProcesses == 0, maxMilliseconds: 30_000);
        _repository.Dispose();
    }

    [Test]
    public void Defaults_should_be_shown_and_not_stored()
    {
        RunPageTest(page =>
        {
            page.LoadSettings();
            foreach (BooleanSetting setting in _booleans)
            {
                GetCheckBox(page, setting).Checked.Should().BeTrue();
            }

            GetNumber(page, AppSettings.RepoSnapshotMemoryLimit).Value.Should().Be(1024);
            GetNumber(page, AppSettings.GitBrokerMemoryLimit).Value.Should().Be(512);
            GetNumber(page, AppSettings.MaxFsMonitorDaemons).Value.Should().Be(5);

            page.SaveSettings();

            Keys.Select(key => AppSettings.GetString(key, defaultValue: null)).Should().OnlyContain(value => value == null);
        });
    }

    [Test]
    public void Changes_should_be_saved_and_loaded()
    {
        RunPageTest(page =>
        {
            page.LoadSettings();
            foreach (BooleanSetting setting in _booleans)
            {
                GetCheckBox(page, setting).Checked = false;
            }

            foreach ((IntegerSetting setting, int index) in _integers.Select((s, i) => (s, i)))
            {
                GetNumber(page, setting).Value = index + 7;
                setting.Value.Should().NotBe(index + 7, "it has not been saved yet");
            }

            page.SaveSettings();

            _booleans.Should().OnlyContain(s => !s.Value);
            _integers.Select(s => s.Value).Should().Equal(7, 8, 9);

            AppSettings.UseDirectGitBinary.Value = true;
            AppSettings.MaxFsMonitorDaemons.Value = 3;
            page.LoadSettings();
            GetCheckBox(page, AppSettings.UseDirectGitBinary).Checked.Should().BeTrue();
            GetCheckBox(page, AppSettings.UseGitBroker).Checked.Should().BeFalse();
            GetNumber(page, AppSettings.MaxFsMonitorDaemons).Value.Should().Be(3);
        });
    }

    [TestCase(-5, 0)]
    [TestCase(int.MaxValue, null)]
    public void Values_out_of_range_should_be_shown_and_kept(int value, int? shownValue)
    {
        RunPageTest(page =>
        {
            foreach (IntegerSetting setting in _integers)
            {
                setting.Value = value;
            }

            page.LoadSettings();
            foreach (IntegerSetting setting in _integers)
            {
                NumericUpDown number = GetNumber(page, setting);
                number.Value.Should().Be(shownValue ?? number.Maximum);
            }

            page.SaveSettings();
            _integers.Should().OnlyContain(s => s.Value == value);

            GetNumber(page, AppSettings.GitBrokerMemoryLimit).Value = 10;
            page.SaveSettings();
            AppSettings.GitBrokerMemoryLimit.Value.Should().Be(10);
        });
    }

    [Test]
    public void Dependent_controls_should_be_disabled()
    {
        RunPageTest(page =>
        {
            page.LoadSettings();
            CheckBox attributes = GetCheckBox(page, AppSettings.UseGitBrokerForAttributes);
            NumericUpDown maxDaemons = GetNumber(page, AppSettings.MaxFsMonitorDaemons);
            attributes.Enabled.Should().BeTrue();
            maxDaemons.Enabled.Should().BeTrue();

            GetCheckBox(page, AppSettings.UseGitBroker).Checked = false;
            GetCheckBox(page, AppSettings.StopIdleFsMonitorDaemons).Checked = false;
            attributes.Enabled.Should().BeFalse();
            maxDaemons.Enabled.Should().BeFalse();

            page.SaveSettings();
            AppSettings.UseGitBrokerForAttributes.Value.Should().BeTrue("a disabled control does not change its setting");
            page.LoadSettings();
            attributes.Enabled.Should().BeFalse();
            maxDaemons.Enabled.Should().BeFalse();
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Persistent_processes_should_be_ended_when_disabled(bool isDisabled)
    {
        ObjectId commitId = ObjectId.Parse(_repository.CreateCommit("Add file", "content", "file.txt"));

        // The persistent process is not used before the config has been read by the application
        _repository.Module.GetEffectiveSetting("core.autocrlf");
        ThreadHelper.JoinableTaskFactory.Run(() => Task.Run(() => GitBroker.TryGetFileTreeId(_repository.Module, commitId, "file.txt", out _, out _, CancellationToken.None))).Should().BeTrue();
        GitBroker.Pool.SessionCount.Should().Be(1);

        RunPageTest(page =>
        {
            page.LoadSettings();
            GetCheckBox(page, AppSettings.UseGitBroker).Checked = !isDisabled;
            page.SaveSettings();
        });

        GitBroker.Pool.SessionCount.Should().Be(isDisabled ? 0 : 1);
    }

    [Test]
    public void Layout_should_show_all_controls()
    {
        RunPageTest(page =>
        {
            page.LoadSettings();
            Control[] inputs = [.. _booleans.Select(s => GetCheckBox(page, s)), .. _integers.Select(s => GetNumber(page, s))];
            foreach (Control control in inputs.Concat(page.FindDescendants().OfType<Label>()))
            {
                control.Visible.Should().BeTrue(control.Name);
                control.Width.Should().BePositive();
                control.Parent!.ClientRectangle.Contains(control.Bounds).Should().BeTrue($"{control.Name} {control.Bounds} must be within {control.Parent.ClientRectangle}");
            }

            foreach (IGrouping<Control, Control> siblings in page.FindDescendants().Where(c => c is Label or CheckBox or NumericUpDown).GroupBy(c => c.Parent!))
            {
                Control[] controls = [.. siblings];
                for (int i = 0; i < controls.Length; ++i)
                {
                    for (int j = i + 1; j < controls.Length; ++j)
                    {
                        controls[i].Bounds.IntersectsWith(controls[j].Bounds).Should().BeFalse($"{controls[i].Name} and {controls[j].Name}");
                    }
                }
            }

            foreach (GroupBox group in page.FindDescendants().OfType<GroupBox>())
            {
                Console.WriteLine($"[{group.Text}] {group.Bounds}");
                foreach (Control control in group.Controls[0].Controls)
                {
                    Console.WriteLine($"    {control.Name} {control.Bounds} {control.Text}");
                }
            }
        });
    }

    [Test]
    public void Translations_should_be_complete()
    {
        TranslationFile translation = new();
        using (PerformanceSettingsPage page = new(GitExtensions.UITests.GlobalServiceContainer.CreateDefaultMockServiceContainer()))
        {
            page.AddTranslationItems(translation);
        }

        string file = Path.Combine(FindSourceDirectory(), "src", "app", "GitUI", "Translation", "English.xlf");
        TranslationFile english = TranslationSerializer.Deserialize(file)!;

        Items(english).Should().Equal(Items(translation));

        return;

        static (string Id, string? Source)[] Items(TranslationFile translationFile)
            => [.. translationFile.TranslationCategories.Single(c => c.Name == nameof(PerformanceSettingsPage)).Body.TranslationItems
                .Select(item => (Id: $"{item.Name}.{item.Property}", item.Source)).OrderBy(item => item.Id, StringComparer.Ordinal)];

        static string FindSourceDirectory()
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GitExtensions.slnx")))
            {
                directory = directory.Parent;
            }

            return directory!.FullName;
        }
    }

    [Test]
    public void Page_should_be_found_in_the_settings_dialog()
    {
        using UITestThreadExceptions threadExceptions = new();
        GitUICommands commands = new(GitExtensions.UITests.GlobalServiceContainer.CreateDefaultMockServiceContainer(), _repository.Module);

        UITest.RunForm(
            () =>
            {
                using FormSettings form = new(commands, PerformanceSettingsPage.GetPageReference());
                form.ShowDialog(owner: null);
            },
            async (FormSettings form) =>
            {
                await AsyncTestHelper.JoinPendingOperationsAsync(AsyncTestHelper.UnexpectedTimeout);

                SettingsTreeViewUserControl tree = form.FindDescendants().OfType<SettingsTreeViewUserControl>().Single();
                PerformanceSettingsPage page = tree.SettingsPages.OfType<PerformanceSettingsPage>().Single();
                page.Visible.Should().BeTrue("it has been opened by its reference");
                form.Text.Should().EndWith("Performance");
                page.GetSearchKeywords().Should().Contain(keyword => keyword.Contains("index.lock"));
                GetCheckBox(page, AppSettings.UseGitBroker).Checked.Should().BeTrue();
            });

        threadExceptions.ThrowIfAny();
    }

    private static CheckBox GetCheckBox(PerformanceSettingsPage page, BooleanSetting setting)
        => page.Controls.Find($"_chk{setting.Name}", searchAllChildren: true).OfType<CheckBox>().Single();

    private static NumericUpDown GetNumber(PerformanceSettingsPage page, IntegerSetting setting)
        => page.Controls.Find($"_num{setting.Name}", searchAllChildren: true).OfType<NumericUpDown>().Single();

    private void RunPageTest(Action<PerformanceSettingsPage> test)
    {
        using UITestThreadExceptions threadExceptions = new();

        UITest.RunForm(
            () =>
            {
                MockHost host = new(_repository.Module) { ClientSize = new Size(900, 800) };
                PerformanceSettingsPage page = SettingsPageBase.Create<PerformanceSettingsPage>(host, GitExtensions.UITests.GlobalServiceContainer.CreateDefaultMockServiceContainer());
                host.Page = page;
                host.Controls.Add(page.GuiControl);
                page.GuiControl.Dock = DockStyle.Fill;
                host.ShowDialog(owner: null);
            },
            (MockHost form) =>
            {
                test(form.Page!);
                return Task.CompletedTask;
            });

        threadExceptions.ThrowIfAny();
    }

    private sealed class MockHost(GitModule module) : Form, ISettingsPageHost
    {
        public PerformanceSettingsPage? Page;

        public CheckSettingsLogic CheckSettingsLogic { get; } = new(new CommonLogic(module));

        public void GotoPage(SettingsPageReference settingsPageReference) => throw new NotSupportedException();

        public void LoadAll() => throw new NotSupportedException();

        public void SaveAll() => throw new NotSupportedException();
    }
}
