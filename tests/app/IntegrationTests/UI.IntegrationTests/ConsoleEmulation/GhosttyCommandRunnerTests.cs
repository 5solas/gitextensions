using System.ComponentModel.Design;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using CommonTestUtils;
using GitCommands;
using GitExtUtils;
using GitUI;
using GitUI.ConsoleEmulation;
using GitUI.ConsoleEmulation.Ghostty;
using GitUI.HelperDialogs;
using GitUI.Shells;

namespace GitExtensions.UITests.ConsoleEmulation;

[TestFixture]
[Apartment(ApartmentState.STA)]
public sealed class GhosttyCommandRunnerTests
{
    private const string SpikeFormProcessScreenshot = "spike/w10-formprocess.png";

    private string _originalConsoleEmulatorName = null!;
    private bool _originalUseConsoleEmulatorForCommands;
    private bool _originalCloseProcessDialog;

    private ReferenceRepository _referenceRepository = null!;
    private GitUICommands _commands = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _originalConsoleEmulatorName = AppSettings.ConsoleEmulatorName.Value;
        _originalUseConsoleEmulatorForCommands = AppSettings.UseConsoleEmulatorForCommands.Value;
        _originalCloseProcessDialog = AppSettings.CloseProcessDialog;

        AppSettings.ConsoleEmulatorName.Value = "ghostty";
        AppSettings.UseConsoleEmulatorForCommands.Value = true;
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        AppSettings.ConsoleEmulatorName.Value = _originalConsoleEmulatorName;
        AppSettings.UseConsoleEmulatorForCommands.Value = _originalUseConsoleEmulatorForCommands;
        AppSettings.CloseProcessDialog = _originalCloseProcessDialog;
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
    public void CommandRunner_should_emit_lines_and_exit_code()
    {
        using Form form = new() { Width = 800, Height = 600 };
        using GhosttyCommandRunner runner = new(GhosttyColorSchemes.Resolve(null, false), SystemFonts.DefaultFont);
        form.Controls.Add(runner.Control);
        runner.Control.Dock = DockStyle.Fill;
        form.Show();

        List<string> collected = [];
        int? exitCode = null;

        runner.CommandOutputReceived += (_, e) => collected.Add(e.Text);
        runner.CommandProcessExited += (_, e) => exitCode = e.ExitCode;

        runner.StartCommand("cmd.exe", "/c echo hello & echo world", Environment.CurrentDirectory, []);

        UITest.ProcessUntil("cmd.exe command finished", () => exitCode.HasValue, maxMilliseconds: 10_000);

        exitCode.Should().Be(0);

        foreach (string s in collected)
        {
            TestContext.Out.WriteLine("COLLECTED: " + s.Replace("\r", "<CR>").Replace("\r\n", "<LF>"));
        }

        string combined = string.Concat(collected);
        combined.Should().Contain("world\n");
        collected.Should().Contain(s => s.StartsWith("hello") && s.EndsWith('\n'));
    }

    [Test]
    public void CommandRunner_should_report_git_output_through_FormProcess()
    {
        using Form owner = new() { Width = 640, Height = 480 };
        owner.Show();

        string? outputString = null;

        UITest.RunForm<FormProcess>(
            showForm: () => _commands.StartCommandLineProcessDialog(owner, "git", "log --oneline").Should().BeTrue(),
            runTestAsync: form =>
            {
                UITest.ProcessUntil(
                    "log output received",
                    () => form.GetOutputString().Contains("Initial commit") && form.GetOutputString().Contains("Second commit"),
                    maxMilliseconds: 10_000);

                UITest.ProcessEventsFor(200);

                SaveFormScreenshot(form, SpikeFormProcessScreenshot);
                outputString = form.GetOutputString();

                return Task.CompletedTask;
            });

        outputString.Should().NotBeNull();
        outputString.Should().Contain("Initial commit");
        outputString.Should().Contain("Second commit");

        string screenshotPath = Path.Combine(FindRepositoryRoot(), SpikeFormProcessScreenshot);
        File.Exists(screenshotPath).Should().BeTrue();
    }

    [Test]
    public void CommandRunner_should_report_minus_one_when_killed()
    {
        using Form form = new() { Width = 800, Height = 600 };
        using GhosttyCommandRunner runner = new(GhosttyColorSchemes.Resolve(null, false), SystemFonts.DefaultFont);
        form.Controls.Add(runner.Control);
        runner.Control.Dock = DockStyle.Fill;
        form.Show();

        int exitCount = 0;
        int? lastExitCode = null;

        runner.CommandProcessExited += (_, e) =>
        {
            exitCount++;
            lastExitCode = e.ExitCode;
        };

        runner.StartCommand("cmd.exe", "/c timeout /t 20 >nul", Environment.CurrentDirectory, []);

        UITest.ProcessEventsFor(1000);

        runner.KillCommandProcess();

        UITest.ProcessUntil("kill exit code received", () => exitCount > 0, maxMilliseconds: 5_000);

        exitCount.Should().Be(1);
        lastExitCode.Should().Be(-1);
    }

    [Test]
    public void CommandRunner_should_receive_progress_with_carriage_return()
    {
        using Form form = new() { Width = 800, Height = 600 };
        using GhosttyCommandRunner runner = new(GhosttyColorSchemes.Resolve(null, false), SystemFonts.DefaultFont);
        form.Controls.Add(runner.Control);
        runner.Control.Dock = DockStyle.Fill;
        form.Show();

        List<string> chunks = [];
        int? exitCode = null;

        runner.CommandOutputReceived += (_, e) => chunks.Add(e.Text);
        runner.CommandProcessExited += (_, e) => exitCode = e.ExitCode;

        string tempClonePath = Path.Combine(Path.GetTempPath(), "ghostty-clone-" + Guid.NewGuid().ToString("N"));
        try
        {
            string gitPath = string.IsNullOrWhiteSpace(AppSettings.GitCommandValue) ? "git" : AppSettings.GitCommandValue;
            string arguments = $"clone --progress --no-local \"{_referenceRepository.Module.WorkingDir}\" \"{tempClonePath}\"";

            runner.StartCommand(gitPath, arguments, Environment.CurrentDirectory, []);

            UITest.ProcessUntil("git clone finished", () => exitCode.HasValue, maxMilliseconds: 15_000);

            exitCode.Should().Be(0);

            foreach (string c in chunks)
            {
                TestContext.Out.WriteLine("CLONE CHUNK: " + c.Replace("\r", "<CR>").Replace("\r\n", "<LF>"));
            }

            bool hasCarriageReturnChunk = chunks.Any(c => c.EndsWith('\r'));
            hasCarriageReturnChunk.Should().BeTrue();
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempClonePath))
                {
                    Directory.Delete(tempClonePath, recursive: true);
                }
            }
            catch
            {
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
