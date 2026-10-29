using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GitUI.ConsoleEmulation.Ghostty;

namespace GitUITests.ConsoleEmulation.Ghostty;

[TestFixture]
[Explicit]
[Apartment(ApartmentState.STA)]
public sealed class GhosttyTerminalControlManualTests
{
    [Test]
    public async Task ManualHost_should_run_commands_and_capture_screenshot()
    {
        if (!GhosttySession.IsSupported)
        {
            Assert.Ignore("LibGhostty is not supported in the current environment.");
        }

        using Form form = new()
        {
            ClientSize = new Size(1000, 600),
            Text = "Ghostty Terminal Manual Host",
            StartPosition = FormStartPosition.CenterScreen
        };

        using GhosttyTerminalControl terminalControl = new()
        {
            Dock = DockStyle.Fill
        };

        terminalControl.ApplyColorScheme(GhosttyColorSchemes.Resolve("Dracula", darkMode: true));
        form.Controls.Add(terminalControl);

        form.Show();
        Application.DoEvents();

        string repositoryRoot = FindRepositoryRoot();
        terminalControl.Start("pwsh.exe", Array.Empty<string>(), repositoryRoot, null);

        await PumpEventsAsync(2500);

        terminalControl.SendText("git log --oneline --graph --color=always -20\r");
        await PumpEventsAsync(2500);

        terminalControl.SendText("echo '한글 😀'\r");
        await PumpEventsAsync(2500);

        form.ClientSize = new Size(1100, 700);
        await PumpEventsAsync(2000);

        string spikeDirectory = Path.Combine(repositoryRoot, "spike");
        Directory.CreateDirectory(spikeDirectory);
        string screenshotPath = Path.Combine(spikeDirectory, "w6-screenshot.png");

        using (Bitmap bitmap = new(terminalControl.Width, terminalControl.Height))
        {
            terminalControl.DrawToBitmap(bitmap, new Rectangle(Point.Empty, terminalControl.Size));
            bitmap.Save(screenshotPath, System.Drawing.Imaging.ImageFormat.Png);
        }

        await TestContext.Out.WriteLineAsync($"Saved screenshot to: {screenshotPath}");
        File.Exists(screenshotPath).Should().BeTrue();

        terminalControl.Stop();
        form.Close();
    }

    private static async Task PumpEventsAsync(int milliseconds)
    {
        int elapsed = 0;
        const int step = 50;
        while (elapsed < milliseconds)
        {
            Application.DoEvents();
            await Task.Delay(step);
            elapsed += step;
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

            current = Directory.GetParent(current)?.FullName;
        }

        throw new DirectoryNotFoundException("Repository root (.git) was not found.");
    }
}
