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
    [Apartment(ApartmentState.STA)]
    public void ApplyFont_should_update_cell_dimensions()
    {
        using GhosttyTerminalControl control = new();
        using Font font9 = new("Consolas", 9f);
        using Font font16 = new("Consolas", 16f);

        control.ApplyFont(font9);
        int cellWidth9 = control.CellWidth;
        int cellHeight9 = control.CellHeight;

        control.ApplyFont(font16);
        int cellWidth16 = control.CellWidth;
        int cellHeight16 = control.CellHeight;

        cellHeight16.Should().BeGreaterThan(cellHeight9);
        cellWidth16.Should().BeGreaterThan(cellWidth9);
    }

    [Test]
    [Apartment(ApartmentState.STA)]
    public void ApplyFont_on_new_control_should_preserve_default_grid_size()
    {
        using GhosttyTerminalControl control = new();
        using Font font16 = new("Consolas", 16f);

        control.ApplyFont(font16);

        control.Columns.Should().Be(80);
        control.Rows.Should().Be(24);
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
