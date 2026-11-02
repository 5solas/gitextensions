using System.Drawing;
using System.Globalization;

namespace GitUI.ConsoleEmulation.Ghostty;

/// <summary>
///  Built-in terminal color schemes (values from the ghostty theme files of iTerm2-Color-Schemes).
/// </summary>
internal static class GhosttyColorSchemes
{
    public const string DarkDefault = "Tomorrow Night";
    public const string LightDefault = "Tomorrow";

    private const int HexDigitCount = 6;

    private static readonly IReadOnlyList<GhosttyColorScheme> _all =
        new List<GhosttyColorScheme>(
        [
            CreateTomorrow(),
            CreateTomorrowNight(),
            CreateTomorrowNightBright(),
            CreateMonokaiPro(),
            CreateSolarizedDark(),
            CreateSolarizedLight(),
            CreateDracula(),
            CreateOneHalfDark(),
            CreateOneHalfLight(),
            CreateGruvboxDark(),
            CreateGruvboxLight(),
            CreateNord(),
            CreateCatppuccinMocha(),
            CreateGhosttyDefault()
        ])
        .OrderBy(scheme => scheme.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>
    ///  Gets all built-in schemes ordered by name.
    /// </summary>
    public static IReadOnlyList<GhosttyColorScheme> All => _all;

    public static IReadOnlyCollection<string> Names { get; } = _all.Select(scheme => scheme.Name).ToList();

    /// <summary>
    ///  Finds a scheme by name, ignoring case; returns <see langword="null"/> when unknown.
    /// </summary>
    public static GhosttyColorScheme? TryGet(string? name)
    {
        return string.IsNullOrWhiteSpace(name)
            ? null
            : _all.FirstOrDefault(scheme => string.Equals(scheme.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///  Returns the configured scheme, or the dark/light default when the name is unknown.
    /// </summary>
    public static GhosttyColorScheme Resolve(string? configuredName, bool darkMode)
    {
        return TryGet(configuredName) ?? TryGet(darkMode ? DarkDefault : LightDefault)!;
    }

    private static GhosttyColorScheme Create(
        string name,
        Color foreground,
        Color background,
        Color? cursor,
        Color? selectionBackground,
        Color? selectionForeground,
        Color[] palette)
    {
        return new GhosttyColorScheme(
            name,
            foreground,
            background,
            cursor ?? foreground,
            selectionBackground ?? foreground,
            selectionForeground ?? background,
            palette);
    }

    private static Color Hex(string value)
    {
        if (value.Length != HexDigitCount)
        {
            throw new ArgumentException($"Invalid color '{value}'.", nameof(value));
        }

        int rgb = int.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return Color.FromArgb(0xFF, (rgb >> (2 * 8)) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
    }

    // https://github.com/mbadolato/iTerm2-Color-Schemes/blob/master/ghostty/Tomorrow
    private static GhosttyColorScheme CreateTomorrow()
    {
        return Create(
            "Tomorrow",
            Hex("4d4d4c"),
            Hex("ffffff"),
            Hex("4d4d4c"),
            Hex("d6d6d6"),
            Hex("4d4d4c"),
            [
            Hex("000000"),
            Hex("c82829"),
            Hex("718c00"),
            Hex("eab700"),
            Hex("4271ae"),
            Hex("8959a8"),
            Hex("3e999f"),
            Hex("bfbfbf"),
            Hex("000000"),
            Hex("c82829"),
            Hex("718c00"),
            Hex("eab700"),
            Hex("4271ae"),
            Hex("8959a8"),
            Hex("3e999f"),
            Hex("ffffff")
            ]);
    }

    // https://github.com/mbadolato/iTerm2-Color-Schemes/blob/master/ghostty/Tomorrow%20Night
    private static GhosttyColorScheme CreateTomorrowNight()
    {
        return Create(
            "Tomorrow Night",
            Hex("c5c8c6"),
            Hex("1d1f21"),
            Hex("c5c8c6"),
            Hex("373b41"),
            Hex("c5c8c6"),
            [
            Hex("000000"),
            Hex("cc6666"),
            Hex("b5bd68"),
            Hex("f0c674"),
            Hex("81a2be"),
            Hex("b294bb"),
            Hex("8abeb7"),
            Hex("ffffff"),
            Hex("4c4c4c"),
            Hex("cc6666"),
            Hex("b5bd68"),
            Hex("f0c674"),
            Hex("81a2be"),
            Hex("b294bb"),
            Hex("8abeb7"),
            Hex("ffffff")
            ]);
    }

    // https://github.com/mbadolato/iTerm2-Color-Schemes/blob/master/ghostty/Tomorrow%20Night%20Bright
    private static GhosttyColorScheme CreateTomorrowNightBright()
    {
        return Create(
            "Tomorrow Night Bright",
            Hex("eaeaea"),
            Hex("000000"),
            Hex("eaeaea"),
            Hex("424242"),
            Hex("eaeaea"),
            [
            Hex("000000"),
            Hex("d54e53"),
            Hex("b9ca4a"),
            Hex("e7c547"),
            Hex("7aa6da"),
            Hex("c397d8"),
            Hex("70c0b1"),
            Hex("ffffff"),
            Hex("404040"),
            Hex("d54e53"),
            Hex("b9ca4a"),
            Hex("e7c547"),
            Hex("7aa6da"),
            Hex("c397d8"),
            Hex("70c0b1"),
            Hex("ffffff")
            ]);
    }

    // https://github.com/mbadolato/iTerm2-Color-Schemes/blob/master/ghostty/Monokai%20Pro
    private static GhosttyColorScheme CreateMonokaiPro()
    {
        return Create(
            "Monokai Pro",
            Hex("fcfcfa"),
            Hex("2d2a2e"),
            Hex("c1c0c0"),
            Hex("5b595c"),
            Hex("fcfcfa"),
            [
            Hex("2d2a2e"),
            Hex("ff6188"),
            Hex("a9dc76"),
            Hex("ffd866"),
            Hex("fc9867"),
            Hex("ab9df2"),
            Hex("78dce8"),
            Hex("fcfcfa"),
            Hex("727072"),
            Hex("ff6188"),
            Hex("a9dc76"),
            Hex("ffd866"),
            Hex("fc9867"),
            Hex("ab9df2"),
            Hex("78dce8"),
            Hex("fcfcfa")
            ]);
    }

    // https://github.com/mbadolato/iTerm2-Color-Schemes/blob/master/ghostty/iTerm2%20Solarized%20Dark
    private static GhosttyColorScheme CreateSolarizedDark()
    {
        return Create(
            "Solarized Dark",
            Hex("839496"),
            Hex("002b36"),
            Hex("839496"),
            Hex("073642"),
            Hex("93a1a1"),
            [
            Hex("073642"),
            Hex("dc322f"),
            Hex("859900"),
            Hex("b58900"),
            Hex("268bd2"),
            Hex("d33682"),
            Hex("2aa198"),
            Hex("eee8d5"),
            Hex("335e69"),
            Hex("cb4b16"),
            Hex("586e75"),
            Hex("657b83"),
            Hex("839496"),
            Hex("6c71c4"),
            Hex("93a1a1"),
            Hex("fdf6e3")
            ]);
    }

    // https://github.com/mbadolato/iTerm2-Color-Schemes/blob/master/ghostty/iTerm2%20Solarized%20Light
    private static GhosttyColorScheme CreateSolarizedLight()
    {
        return Create(
            "Solarized Light",
            Hex("657b83"),
            Hex("fdf6e3"),
            Hex("657b83"),
            Hex("eee8d5"),
            Hex("586e75"),
            [
            Hex("073642"),
            Hex("dc322f"),
            Hex("859900"),
            Hex("b58900"),
            Hex("268bd2"),
            Hex("d33682"),
            Hex("2aa198"),
            Hex("bbb5a2"),
            Hex("002b36"),
            Hex("cb4b16"),
            Hex("586e75"),
            Hex("657b83"),
            Hex("839496"),
            Hex("6c71c4"),
            Hex("93a1a1"),
            Hex("fdf6e3")
            ]);
    }

    // https://github.com/mbadolato/iTerm2-Color-Schemes/blob/master/ghostty/Dracula
    private static GhosttyColorScheme CreateDracula()
    {
        return Create(
            "Dracula",
            Hex("f8f8f2"),
            Hex("282a36"),
            Hex("f8f8f2"),
            Hex("44475a"),
            Hex("ffffff"),
            [
            Hex("21222c"),
            Hex("ff5555"),
            Hex("50fa7b"),
            Hex("f1fa8c"),
            Hex("bd93f9"),
            Hex("ff79c6"),
            Hex("8be9fd"),
            Hex("f8f8f2"),
            Hex("6272a4"),
            Hex("ff6e6e"),
            Hex("69ff94"),
            Hex("ffffa5"),
            Hex("d6acff"),
            Hex("ff92df"),
            Hex("a4ffff"),
            Hex("ffffff")
            ]);
    }

    // https://github.com/mbadolato/iTerm2-Color-Schemes/blob/master/ghostty/One%20Half%20Dark
    private static GhosttyColorScheme CreateOneHalfDark()
    {
        return Create(
            "One Half Dark",
            Hex("dcdfe4"),
            Hex("282c34"),
            Hex("a3b3cc"),
            Hex("474e5d"),
            Hex("dcdfe4"),
            [
            Hex("282c34"),
            Hex("e06c75"),
            Hex("98c379"),
            Hex("e5c07b"),
            Hex("61afef"),
            Hex("c678dd"),
            Hex("56b6c2"),
            Hex("dcdfe4"),
            Hex("5d677a"),
            Hex("e06c75"),
            Hex("98c379"),
            Hex("e5c07b"),
            Hex("61afef"),
            Hex("c678dd"),
            Hex("56b6c2"),
            Hex("dcdfe4")
            ]);
    }

    // https://github.com/mbadolato/iTerm2-Color-Schemes/blob/master/ghostty/One%20Half%20Light
    private static GhosttyColorScheme CreateOneHalfLight()
    {
        return Create(
            "One Half Light",
            Hex("383a42"),
            Hex("fafafa"),
            Hex("a5b4e5"),
            Hex("bfceff"),
            Hex("383a42"),
            [
            Hex("383a42"),
            Hex("e45649"),
            Hex("50a14f"),
            Hex("c18401"),
            Hex("0184bc"),
            Hex("a626a4"),
            Hex("0997b3"),
            Hex("bababa"),
            Hex("4f525e"),
            Hex("e06c75"),
            Hex("98c379"),
            Hex("d8b36e"),
            Hex("61afef"),
            Hex("c678dd"),
            Hex("56b6c2"),
            Hex("ffffff")
            ]);
    }

    // https://github.com/mbadolato/iTerm2-Color-Schemes/blob/master/ghostty/Gruvbox%20Dark
    private static GhosttyColorScheme CreateGruvboxDark()
    {
        return Create(
            "Gruvbox Dark",
            Hex("ebdbb2"),
            Hex("282828"),
            Hex("ebdbb2"),
            Hex("665c54"),
            Hex("ebdbb2"),
            [
            Hex("282828"),
            Hex("cc241d"),
            Hex("98971a"),
            Hex("d79921"),
            Hex("458588"),
            Hex("b16286"),
            Hex("689d6a"),
            Hex("a89984"),
            Hex("928374"),
            Hex("fb4934"),
            Hex("b8bb26"),
            Hex("fabd2f"),
            Hex("83a598"),
            Hex("d3869b"),
            Hex("8ec07c"),
            Hex("ebdbb2")
            ]);
    }

    // https://github.com/mbadolato/iTerm2-Color-Schemes/blob/master/ghostty/Gruvbox%20Light
    private static GhosttyColorScheme CreateGruvboxLight()
    {
        return Create(
            "Gruvbox Light",
            Hex("3c3836"),
            Hex("fbf1c7"),
            Hex("3c3836"),
            Hex("3c3836"),
            Hex("fbf1c7"),
            [
            Hex("fbf1c7"),
            Hex("cc241d"),
            Hex("98971a"),
            Hex("d79921"),
            Hex("458588"),
            Hex("b16286"),
            Hex("689d6a"),
            Hex("7c6f64"),
            Hex("928374"),
            Hex("9d0006"),
            Hex("79740e"),
            Hex("b57614"),
            Hex("076678"),
            Hex("8f3f71"),
            Hex("427b58"),
            Hex("3c3836")
            ]);
    }

    // https://github.com/mbadolato/iTerm2-Color-Schemes/blob/master/ghostty/Nord
    private static GhosttyColorScheme CreateNord()
    {
        return Create(
            "Nord",
            Hex("d8dee9"),
            Hex("2e3440"),
            Hex("eceff4"),
            Hex("eceff4"),
            Hex("4c566a"),
            [
            Hex("3b4252"),
            Hex("bf616a"),
            Hex("a3be8c"),
            Hex("ebcb8b"),
            Hex("81a1c1"),
            Hex("b48ead"),
            Hex("88c0d0"),
            Hex("e5e9f0"),
            Hex("596377"),
            Hex("bf616a"),
            Hex("a3be8c"),
            Hex("ebcb8b"),
            Hex("81a1c1"),
            Hex("b48ead"),
            Hex("8fbcbb"),
            Hex("eceff4")
            ]);
    }

    // https://github.com/mbadolato/iTerm2-Color-Schemes/blob/master/ghostty/Catppuccin%20Mocha
    private static GhosttyColorScheme CreateCatppuccinMocha()
    {
        return Create(
            "Catppuccin Mocha",
            Hex("cdd6f4"),
            Hex("1e1e2e"),
            Hex("f5e0dc"),
            Hex("f5e0dc"),
            Hex("1e1e2e"),
            [
            Hex("45475a"),
            Hex("f38ba8"),
            Hex("a6e3a1"),
            Hex("f9e2af"),
            Hex("89b4fa"),
            Hex("f5c2e7"),
            Hex("94e2d5"),
            Hex("bac2de"),
            Hex("585b70"),
            Hex("f7aec2"),
            Hex("c2ecbf"),
            Hex("fcd682"),
            Hex("aeccfc"),
            Hex("f398da"),
            Hex("b1eae1"),
            Hex("a6adc8")
            ]);
    }

    // Palette: https://github.com/ghostty-org/ghostty/blob/main/src/terminal/color.zig (default palette); background/foreground/cursor/selection: https://github.com/mbadolato/iTerm2-Color-Schemes/blob/master/ghostty/Ghostty%20Default%20Style%20Dark
    private static GhosttyColorScheme CreateGhosttyDefault()
    {
        return Create(
            "Ghostty Default",
            Hex("ffffff"),
            Hex("282c34"),
            Hex("ffffff"),
            Hex("ffffff"),
            Hex("282c34"),
            [
            Hex("1d1f21"),
            Hex("cc6666"),
            Hex("b5bd68"),
            Hex("f0c674"),
            Hex("81a2be"),
            Hex("b294bb"),
            Hex("8abeb7"),
            Hex("c5c8c6"),
            Hex("666666"),
            Hex("d54e53"),
            Hex("b9ca4a"),
            Hex("e7c547"),
            Hex("7aa6da"),
            Hex("c397d8"),
            Hex("70c0b1"),
            Hex("eaeaea")
            ]);
    }
}
