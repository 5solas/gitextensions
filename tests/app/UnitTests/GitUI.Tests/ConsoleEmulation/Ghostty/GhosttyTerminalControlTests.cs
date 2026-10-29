using System.Drawing;
using System.Threading;
using GitUI.ConsoleEmulation.Ghostty;

namespace GitUITests.ConsoleEmulation.Ghostty;

[TestFixture]
public sealed class GhosttyTerminalControlTests
{
    [Test]
    [Apartment(ApartmentState.STA)]
    public void ApplyFont_should_not_take_ownership_of_caller_font()
    {
        using Font font = new("Consolas", 12f);
        GhosttyTerminalControl control = new();
        control.ApplyFont(font);
        control.Dispose();

        Action action = () =>
        {
            _ = font.Name;
            _ = font.Size;
        };

        action.Should().NotThrow();
        font.Name.Should().Be("Consolas");
        font.Size.Should().Be(12f);
    }

    [Test]
    [Apartment(ApartmentState.STA)]
    public void Dispose_should_not_throw_when_never_started()
    {
        Action action = () =>
        {
            GhosttyTerminalControl control = new();
            control.Dispose();
        };

        action.Should().NotThrow();
    }

    [Test]
    [Retry(2)]
    [Apartment(ApartmentState.STA)]
    public void Start_twice_should_not_report_stale_exit()
    {
        if (!GhosttySession.IsSupported)
        {
            Assert.Ignore("LibGhostty is not supported in the current environment.");
        }

        using Form form = new()
        {
            ClientSize = new Size(800, 600)
        };

        using GhosttyTerminalControl control = new()
        {
            Dock = DockStyle.Fill
        };

        form.Controls.Add(control);
        form.Show();
        Application.DoEvents();

        int exitedEventCount = 0;
        control.ProcessExited += (_, _) =>
        {
            exitedEventCount++;
        };

        string workingDirectory = TestContext.CurrentContext.TestDirectory;
        control.Start("cmd.exe", ["/c", "exit 7"], workingDirectory, null);
        control.Start("cmd.exe", ["/c", "timeout /t 5 >nul"], workingDirectory, null);

        DateTime deadline = DateTime.UtcNow.AddSeconds(1);
        while (DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(20);
        }

        control.IsProcessRunning.Should().BeTrue();
        exitedEventCount.Should().Be(0);

        control.Stop();
        form.Close();
    }
}
