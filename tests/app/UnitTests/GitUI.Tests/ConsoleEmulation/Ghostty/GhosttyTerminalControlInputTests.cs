using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using GitUI.ConsoleEmulation.Ghostty;

namespace GitUITests.ConsoleEmulation.Ghostty;

[TestFixture]
[Apartment(ApartmentState.STA)]
public sealed class GhosttyTerminalControlInputTests
{
    [Test]
    public void IsInputKey_should_return_true_for_tab()
    {
        using GhosttyTerminalControl control = new();
        control.IsInputKeyForTest(Keys.Tab).Should().BeTrue();
    }

    [Test]
    public void IsInputKey_should_return_false_for_alt_f4()
    {
        using GhosttyTerminalControl control = new();
        control.IsInputKeyForTest(Keys.F4 | Keys.Alt).Should().BeFalse();
    }

    [TestCase(Keys.Up)]
    [TestCase(Keys.Down)]
    [TestCase(Keys.Left)]
    [TestCase(Keys.Right)]
    [TestCase(Keys.Enter)]
    [TestCase(Keys.Escape)]
    [TestCase(Keys.Tab | Keys.Shift)]
    [TestCase(Keys.Tab | Keys.Control)]
    [TestCase(Keys.Home)]
    [TestCase(Keys.End)]
    [TestCase(Keys.PageUp)]
    [TestCase(Keys.PageDown)]
    public void IsInputKey_should_return_true_for_command_and_navigation_keys(Keys keyData)
    {
        using GhosttyTerminalControl control = new();
        control.IsInputKeyForTest(keyData).Should().BeTrue();
    }

    [Test]
    public void EncodeKeyForTest_should_encode_enter_as_carriage_return()
    {
        EnsureSupportedOrIgnore();

        using GhosttyTerminalControl control = new();
        byte[] bytes = control.EncodeKeyForTest(Keys.Enter);

        bytes.Should().Equal(new byte[] { 0x0D });
    }

    [Test]
    public void EncodeKeyForTest_should_encode_up_arrow_as_csi_a()
    {
        EnsureSupportedOrIgnore();

        using GhosttyTerminalControl control = new();
        byte[] bytes = control.EncodeKeyForTest(Keys.Up);

        bytes.Should().Equal(new byte[] { 0x1B, (byte)'[', (byte)'A' });
    }

    [Test]
    public void EncodeKeyForTest_should_encode_ctrl_c_as_etx()
    {
        EnsureSupportedOrIgnore();

        using GhosttyTerminalControl control = new();
        byte[] bytes = control.EncodeKeyForTest(Keys.C | Keys.Control);

        bytes.Should().Equal(new byte[] { 0x03 });
    }

    [Test]
    public void EncodeKeyForTest_should_encode_backspace_as_delete_char()
    {
        EnsureSupportedOrIgnore();

        using GhosttyTerminalControl control = new();
        byte[] bytes = control.EncodeKeyForTest(Keys.Back);

        bytes.Should().Equal(new byte[] { 0x7F });
    }

    [Test]
    public void EncodeKeyForTest_should_encode_tab_as_tab_char()
    {
        EnsureSupportedOrIgnore();

        using GhosttyTerminalControl control = new();
        byte[] bytes = control.EncodeKeyForTest(Keys.Tab);

        bytes.Should().Equal(new byte[] { 0x09 });
    }

    [Test]
    public void EncodePasteForTest_should_normalize_crlf_to_cr_when_bracketed_off()
    {
        EnsureSupportedOrIgnore();

        using GhosttyTerminalControl control = new();
        string input = "echo hello\r\necho world\r\n";
        byte[] bytes = control.EncodePasteForTest(input);

        string expected = "echo hello\recho world\r";
        bytes.Should().Equal(Encoding.UTF8.GetBytes(expected));
    }

    [Test]
    public void FocusEncode_should_encode_focus_gained_and_lost()
    {
        EnsureSupportedOrIgnore();

        Span<byte> gainedBuffer = stackalloc byte[16];
        int gainedResult = GhosttySession.Extras.FocusEncode(gained: true, gainedBuffer, out nuint gainedWritten);
        gainedResult.Should().Be(0);
        gainedBuffer[..(int)gainedWritten].ToArray().Should().Equal(new byte[] { 0x1B, (byte)'[', (byte)'I' });

        Span<byte> lostBuffer = stackalloc byte[16];
        int lostResult = GhosttySession.Extras.FocusEncode(gained: false, lostBuffer, out nuint lostWritten);
        lostResult.Should().Be(0);
        lostBuffer[..(int)lostWritten].ToArray().Should().Equal(new byte[] { 0x1B, (byte)'[', (byte)'O' });
    }

    [Test]
    [Explicit]
    public async Task ManualInput_should_type_dir_and_capture_screenshot()
    {
        EnsureSupportedOrIgnore();

        using Form form = new()
        {
            ClientSize = new Size(1000, 600),
            Text = "Ghostty Terminal Manual Input Host",
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
        terminalControl.Start("cmd.exe", ["/k"], repositoryRoot, null);

        await PumpEventsAsync(2000);

        // Type 'dir' + Enter through OnKeyDown/OnKeyPress
        SimulateTyping(terminalControl, "dir\r");
        await PumpEventsAsync(2500);

        string spikeDirectory = Path.Combine(repositoryRoot, "spike");
        Directory.CreateDirectory(spikeDirectory);
        string screenshotPath = Path.Combine(spikeDirectory, "w7-screenshot.png");

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
                    _ => Keys.None
                };

                control.SimulateKeyDown(new KeyEventArgs(keyCode));
                control.SimulateKeyPress(new KeyPressEventArgs(character));
            }
        }
    }

    private static void EnsureSupportedOrIgnore()
    {
        if (!GhosttySession.IsSupported)
        {
            Assert.Ignore("LibGhostty is not supported in the current environment.");
        }
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
