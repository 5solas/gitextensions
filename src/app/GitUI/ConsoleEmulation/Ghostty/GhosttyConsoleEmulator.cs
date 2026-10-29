using System.Drawing;
using GitUI.ConsoleEmulation.PlainText;
using GitUI.Shells;

namespace GitUI.ConsoleEmulation.Ghostty;

/// <summary>
///  Represents a console emulator integration based on libghostty-vt.
/// </summary>
internal sealed class GhosttyConsoleEmulator(IShellProvider shellProvider) : IConsoleEmulator
{
    private static readonly Font DefaultFont = new("Consolas", 12);

    /// <inheritdoc/>
    public string Name => "ghostty";

    /// <inheritdoc/>
    public string DisplayName => "Ghostty";

    /// <inheritdoc/>
    public bool IsSupportedInCurrentEnvironment => GhosttySession.IsSupported;

    /// <inheritdoc/>
    public IReadOnlyCollection<string> AvailableThemes => GhosttyColorSchemes.Names;

    /// <inheritdoc/>
    public string? DefaultTheme => null;

    /// <inheritdoc/>
    public IConsoleCommandRunner CreateCommandRunner(ConsoleEmulatorSettings settings)
    {
        return new PlainTextConsoleCommandRunner();
    }

    /// <inheritdoc/>
    public IConsoleShellRunner CreateShellRunner(ConsoleEmulatorSettings settings)
    {
        GhosttyColorScheme scheme = ResolveScheme(settings.Theme);
        Font font = settings.Font ?? DefaultFont;
        return new GhosttyShellRunner(shellProvider, scheme, font);
    }

    internal static GhosttyColorScheme ResolveScheme(string? configuredTheme)
    {
        return GhosttyColorSchemes.Resolve(configuredTheme, Application.IsDarkModeEnabled);
    }
}
