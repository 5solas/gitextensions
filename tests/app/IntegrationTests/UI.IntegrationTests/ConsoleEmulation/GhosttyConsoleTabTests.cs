using System.ComponentModel.Design;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.RegularExpressions;
using CommonTestUtils;
using GitCommands;
using GitExtUtils;
using GitUI;
using GitUI.CommandsDialogs;
using GitUI.ConsoleEmulation;
using GitUI.ConsoleEmulation.Ghostty;
using GitUI.Shells;

namespace GitExtensions.UITests.ConsoleEmulation;

[TestFixture]
[Apartment(ApartmentState.STA)]
public sealed class GhosttyConsoleTabTests
{
    private const string SpikeConsoleTabScreenshot = "spike/w9-console-tab.png";
    private const string SpikeDirectoryChangeScreenshot = "spike/w9-cd.png";
    private const string SpikeTypingScreenshot = "spike/w9-typing.png";

    private bool _originalShowConEmuTab;
    private string _originalConsoleEmulatorName = null!;
    private string _originalConEmuTerminal = null!;

    private ReferenceRepository _referenceRepository = null!;
    private GitUICommands _commands = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _originalShowConEmuTab = AppSettings.ShowConEmuTab.Value;
        _originalConsoleEmulatorName = AppSettings.ConsoleEmulatorName.Value;
        _originalConEmuTerminal = AppSettings.ConEmuTerminal.Value;

        AppSettings.ShowConEmuTab.Value = true;
        AppSettings.ConsoleEmulatorName.Value = "ghostty";
        AppSettings.ConEmuTerminal.Value = "cmd";
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        AppSettings.ShowConEmuTab.Value = _originalShowConEmuTab;
        AppSettings.ConsoleEmulatorName.Value = _originalConsoleEmulatorName;
        AppSettings.ConEmuTerminal.Value = _originalConEmuTerminal;
    }

    [SetUp]
    public void SetUp()
    {
        if (!GhosttySession.IsSupported)
        {
            Assert.Ignore("LibGhostty is not supported in the current environment.");
        }

        _referenceRepository = new ReferenceRepository();
        _referenceRepository.CreateCommit("Initial commit", "Initial commit");
        _referenceRepository.CreateCommit("Second commit", "Second commit");

        IServiceContainer serviceContainer = GlobalServiceContainer.CreateDefaultMockServiceContainer();
        serviceContainer.RemoveService(typeof(IConsoleEmulatorsRegistry));
        IShellProvider shellProvider = serviceContainer.GetRequiredService<IShellProvider>();
        serviceContainer.AddService<IConsoleEmulatorsRegistry>(
            new ConsoleEmulatorsRegistry(
                consoleEmulators: [new GhosttyConsoleEmulator(shellProvider)],
                useConsoleEmulation: AppSettings.UseConsoleEmulatorForCommands,
                consoleEmulatorName: AppSettings.ConsoleEmulatorName,
                consoleEmulatorTheme: AppSettings.ConEmuStyle,
                consoleFont: () => AppSettings.ConEmuConsoleFont));

        _commands = new GitUICommands(serviceContainer, _referenceRepository.Module);
    }

    [TearDown]
    public void TearDown()
    {
        _referenceRepository?.Dispose();
    }

    [Test]
    public void ConsoleTab_should_host_GhosttyTerminalControl_and_show_prompt()
    {
        RunFormTest(form =>
        {
            (GhosttyShellRunner runner, GhosttyTerminalControl control) = OpenConsoleTab(form);

            UITest.ProcessEventsFor(2000);

            SaveFormScreenshot(form, SpikeConsoleTabScreenshot);

            FormBrowse.TestAccessor testAccessor = form.GetTestAccessor();
            testAccessor.Terminal.Should().BeOfType<GhosttyShellRunner>();
            runner.IsShellRunning.Should().BeTrue();
            control.Columns.Should().BeGreaterThan(0);
            control.Rows.Should().BeGreaterThan(0);
        });
    }

    [Test]
    public void ConsoleTab_should_change_directory_when_module_changes()
    {
        using ReferenceRepository otherRepository = new();
        otherRepository.CreateCommit("Other repo commit", "Other repo commit");

        RunFormTest(form =>
        {
            (GhosttyShellRunner runner, GhosttyTerminalControl control) = OpenConsoleTab(form);

            UITest.ProcessEventsFor(1000);

            form.SetWorkingDir(otherRepository.Module.WorkingDir);

            UITest.ProcessEventsFor(2000);

            SaveFormScreenshot(form, SpikeDirectoryChangeScreenshot);

            control.Selection.Begin(0, 0);
            control.Selection.Extend(control.Columns - 1, control.Rows - 1);
            string extractedText = control.ExtractSelectedTextForTest();

            string directoryName = Path.GetFileName(otherRepository.Module.WorkingDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            extractedText.Should().Contain(directoryName);
        });
    }

    [Test]
    public void ConsoleTab_should_restart_shell_after_exit()
    {
        RunFormTest(form =>
        {
            (GhosttyShellRunner runner, GhosttyTerminalControl control) = OpenConsoleTab(form);

            control.SendText("exit\r");

            UITest.ProcessUntil("shell exited", () => !runner.IsShellRunning, maxMilliseconds: 10_000);
            runner.IsShellRunning.Should().BeFalse();

            FormBrowse.TestAccessor testAccessor = form.GetTestAccessor();
            if (testAccessor.DiffTabPage is not null)
            {
                testAccessor.CommitInfoTabControl.SelectedTab = testAccessor.DiffTabPage;
            }

            testAccessor.CommitInfoTabControl.SelectedTab = testAccessor.ConsoleTabPage;

            UITest.ProcessUntil("shell restarted", () => runner.IsShellRunning, maxMilliseconds: 10_000);
            runner.IsShellRunning.Should().BeTrue();

            control.Selection.Begin(0, 0);
            control.Selection.Extend(control.Columns - 1, control.Rows - 1);
            string extractedText = control.ExtractSelectedTextForTest();
            int exitMessageCount = Regex.Matches(extractedText, @"\[process exited").Count;
            exitMessageCount.Should().BeInRange(0, 1);
        });
    }

    [Test]
    [Explicit("Manual typing demonstration that types git log and captures a screenshot")]
    public void ConsoleTab_manual_typing_screenshot()
    {
        RunFormTest(form =>
        {
            (GhosttyShellRunner runner, GhosttyTerminalControl control) = OpenConsoleTab(form);

            UITest.ProcessEventsFor(1000);

            SimulateTyping(control, "git log --oneline -5\r");

            UITest.ProcessEventsFor(3000);

            SaveFormScreenshot(form, SpikeTypingScreenshot);

            string screenshotPath = Path.Combine(FindRepositoryRoot(), SpikeTypingScreenshot);
            File.Exists(screenshotPath).Should().BeTrue();
        });
    }

    private void RunFormTest(Action<FormBrowse> testDriver)
    {
        UITest.RunForm(
            showForm: () => _commands.StartBrowseDialog(owner: null).Should().BeTrue(),
            (FormBrowse form) =>
            {
                testDriver(form);
                return Task.CompletedTask;
            });
    }

    private static (GhosttyShellRunner Runner, GhosttyTerminalControl Control) OpenConsoleTab(FormBrowse form)
    {
        FormBrowse.TestAccessor testAccessor = form.GetTestAccessor();
        if (testAccessor.ConsoleTabPage is { } consoleTab)
        {
            testAccessor.CommitInfoTabControl.SelectedTab = consoleTab;
        }
        else
        {
            form.ExecuteCommand(FormBrowse.Command.FocusGitConsole);
        }

        UITest.ProcessUntil(
            "Ghostty terminal initialized and running",
            () => form.GetTestAccessor().Terminal is GhosttyShellRunner runner
                  && runner.Control is GhosttyTerminalControl
                  && runner.IsShellRunning,
            maxMilliseconds: 10_000);

        GhosttyShellRunner shellRunner = (GhosttyShellRunner)form.GetTestAccessor().Terminal!;
        GhosttyTerminalControl terminalControl = (GhosttyTerminalControl)shellRunner.Control;
        return (shellRunner, terminalControl);
    }

    private static void SimulateTyping(GhosttyTerminalControl control, string text)
    {
        foreach (char character in text)
        {
            if (character == '\r')
            {
                control.SimulateKeyDown(new KeyEventArgs(Keys.Enter));
            }
            else
            {
                Keys keyCode = character switch
                {
                    >= 'a' and <= 'z' => Keys.A + (character - 'a'),
                    >= 'A' and <= 'Z' => Keys.A + (character - 'A'),
                    >= '0' and <= '9' => Keys.D0 + (character - '0'),
                    ' ' => Keys.Space,
                    '-' => Keys.OemMinus,
                    _ => Keys.None
                };

                control.SimulateKeyDown(new KeyEventArgs(keyCode));
                control.SimulateKeyPress(new KeyPressEventArgs(character));
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        string? current = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(Path.Combine(current, ".git")))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        return AppContext.BaseDirectory;
    }

    private static void SaveFormScreenshot(Form form, string relativePath)
    {
        string repositoryRoot = FindRepositoryRoot();
        string destinationPath = Path.Combine(repositoryRoot, relativePath);
        string? directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using Bitmap bitmap = new(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(destinationPath, ImageFormat.Png);
    }
}
