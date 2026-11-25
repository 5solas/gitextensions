using System.Drawing;

namespace GitUI.ConsoleEmulation.Ghostty;

/// <summary>
///  A terminal color scheme: default colors, selection colors and the 16 ANSI palette entries.
/// </summary>
internal sealed record GhosttyColorScheme(
    string Name,
    Color Foreground,
    Color Background,
    Color Cursor,
    Color SelectionBackground,
    Color SelectionForeground,
    IReadOnlyList<Color> Palette)
{
    public const int PaletteSize = 16;

    public IReadOnlyList<Color> Palette { get; } = Palette is { Count: PaletteSize }
        ? Palette
        : throw new ArgumentException($"Palette must contain exactly {PaletteSize} colors.", nameof(Palette));
}
