using GitUI.ConsoleEmulation;
using GitUI.ConsoleEmulation.Ghostty;
using GitUI.Shells;
using NSubstitute;

namespace GitUITests.ConsoleEmulation.Ghostty;

[TestFixture]
public sealed class GhosttyConsoleEmulatorTests
{
    [Test]
    public void Name_should_be_ghostty()
    {
        IShellProvider shellProvider = Substitute.For<IShellProvider>();
        GhosttyConsoleEmulator emulator = new(shellProvider);

        emulator.Name.Should().Be("ghostty");
    }

    [Test]
    public void DisplayName_should_be_Ghostty()
    {
        IShellProvider shellProvider = Substitute.For<IShellProvider>();
        GhosttyConsoleEmulator emulator = new(shellProvider);

        emulator.DisplayName.Should().Be("Ghostty");
    }

    [Test]
    public void AvailableThemes_should_match_GhosttyColorSchemes_Names()
    {
        IShellProvider shellProvider = Substitute.For<IShellProvider>();
        GhosttyConsoleEmulator emulator = new(shellProvider);

        emulator.AvailableThemes.Should().BeEquivalentTo(GhosttyColorSchemes.Names);
    }

    [Test]
    public void DefaultTheme_should_be_null()
    {
        IShellProvider shellProvider = Substitute.For<IShellProvider>();
        GhosttyConsoleEmulator emulator = new(shellProvider);

        emulator.DefaultTheme.Should().BeNull();
    }

    [Test]
    public void ResolveScheme_null_should_return_Tomorrow_or_TomorrowNight()
    {
        GhosttyColorScheme scheme = GhosttyConsoleEmulator.ResolveScheme(null);

        scheme.Name.Should().BeOneOf("Tomorrow", "Tomorrow Night");
    }

    [Test]
    public void ResolveScheme_should_return_requested_theme()
    {
        GhosttyColorScheme scheme = GhosttyConsoleEmulator.ResolveScheme("Dracula");

        scheme.Name.Should().Be("Dracula");
    }

    [Test]
    public void IsSupportedInCurrentEnvironment_should_return_bool_without_throwing()
    {
        IShellProvider shellProvider = Substitute.For<IShellProvider>();
        GhosttyConsoleEmulator emulator = new(shellProvider);

        Func<bool> action = () => emulator.IsSupportedInCurrentEnvironment;

        action.Should().NotThrow();
        emulator.IsSupportedInCurrentEnvironment.Should().Be(GhosttySession.IsSupported);
    }

    [Test]
    public void CreateCommandRunner_should_return_GhosttyCommandRunner()
    {
        IShellProvider shellProvider = Substitute.For<IShellProvider>();
        GhosttyConsoleEmulator emulator = new(shellProvider);

        IConsoleCommandRunner runner = emulator.CreateCommandRunner(new ConsoleEmulatorSettings(null, null));

        runner.Should().BeOfType<GhosttyCommandRunner>();
    }
}
