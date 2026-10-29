using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GitUI.ConsoleEmulation.Ghostty;
using LibGhostty;
using static LibGhostty.GhosttyNativeLibrary;

namespace GitUITests.ConsoleEmulation.Ghostty;

[TestFixture]
public sealed class GhosttySessionTests
{
    [Test]
    public void IsSupported_should_not_throw_and_return_boolean()
    {
        Assert.DoesNotThrow(() =>
        {
            bool supported = GhosttySession.IsSupported;
            TestContext.Out.WriteLine($"GhosttySession.IsSupported: {supported}, NativeDirectory: {GhosttySession.NativeDirectory}");
        });
    }

    [Test]
    public void NativeLibrary_and_Extras_should_work_with_terminal()
    {
        if (!GhosttySession.IsSupported)
        {
            Assert.Ignore("LibGhostty is not supported in the current environment.");
        }

        GhosttyNativeLibrary library = GhosttySession.Library;
        GhosttyNativeExtras extras = GhosttySession.Extras;

        library.Should().NotBeNull();
        extras.Should().NotBeNull();

        TerminalOptions options = new()
        {
            Columns = 80,
            Rows = 24,
            MaxScrollback = (UIntPtr)1000
        };

        int terminalResult = library.TerminalNew(options, out IntPtr terminal);
        terminalResult.Should().Be(Success);
        terminal.Should().NotBe(IntPtr.Zero);

        try
        {
            int getColsResult = extras.TerminalGetU16(terminal, GhosttyNativeConstants.TerminalDataCols, out ushort cols);
            getColsResult.Should().Be(Success);
            cols.Should().Be(80);

            bool bracketedPasteMode = extras.TerminalModeGet(terminal, GhosttyNativeConstants.ModeBracketedPaste);
            bracketedPasteMode.Should().BeFalse();

            int renderStateResult = library.RenderStateNew(out IntPtr renderState);
            renderStateResult.Should().Be(Success);

            try
            {
                library.RenderStateUpdate(renderState, terminal).Should().Be(Success);

                int rowIteratorResult = library.RowIteratorNew(out IntPtr rowIterator);
                rowIteratorResult.Should().Be(Success);

                try
                {
                    library.RenderStateGetHandle(renderState, RenderStateDataRowIterator, ref rowIterator).Should().Be(Success);
                    library.RowIteratorNext(rowIterator).Should().BeTrue();

                    extras.RowSetDirty(rowIterator, true);
                    extras.RowIsDirty(rowIterator).Should().BeTrue();

                    extras.RowSetDirty(rowIterator, false);
                    extras.RowIsDirty(rowIterator).Should().BeFalse();
                }
                finally
                {
                    library.RowIteratorFree(rowIterator);
                }
            }
            finally
            {
                library.RenderStateFree(renderState);
            }

            NativeColor[] palette = new NativeColor[256];
            int setPaletteResult = extras.TerminalSetPalette(terminal, palette);
            setPaletteResult.Should().Be(Success);

            byte[] focusBuffer = new byte[16];
            int focusResult = extras.FocusEncode(true, focusBuffer, out nuint written);
            focusResult.Should().Be(Success);
            written.Should().Be((nuint)3);
            focusBuffer[0].Should().Be(0x1B);
            focusBuffer[1].Should().Be((byte)'[');
            focusBuffer[2].Should().Be((byte)'I');
        }
        finally
        {
            library.TerminalFree(terminal);
        }
    }

    [Test]
    public async Task PtyConnection_smoke_test_should_launch_and_exit()
    {
        if (!GhosttySession.IsSupported)
        {
            Assert.Ignore("LibGhostty is not supported in the current environment.");
        }

        string comSpec = Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
        GhosttyPtyOptions options = new()
        {
            App = comSpec,
            CommandLine = [comSpec, "/c", "echo", "hi"],
            Cwd = AppContext.BaseDirectory,
            Cols = 80,
            Rows = 24
        };

        using CancellationTokenSource cancellationTokenSource = new(TimeSpan.FromSeconds(10));
        IGhosttyPtyConnection connection = await GhosttyPtyConnectionFactory.StartAsync(options, cancellationTokenSource.Token);
        connection.Should().NotBeNull();

        try
        {
            bool exited = connection.WaitForExit(5000);
            exited.Should().BeTrue();
            connection.ExitCode.Should().Be(0);
        }
        finally
        {
            if (connection is IDisposable disposableConnection)
            {
                disposableConnection.Dispose();
            }
        }
    }
}
