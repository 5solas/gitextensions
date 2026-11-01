using System.Drawing;
using System.Text;

namespace GitUI.ConsoleEmulation.Ghostty;

/// <summary>
///  Represents a single terminal cell with its text, width classification, colors, and styling flags.
/// </summary>
internal readonly record struct GhosttyCell(
    string Text,
    int Wide,
    Color Fg,
    Color Bg,
    bool Bold = false,
    bool Italic = false,
    bool Underline = false,
    bool Strikethrough = false,
    bool Inverse = false,
    bool Faint = false,
    bool Invisible = false);

/// <summary>
///  Represents a contiguous sequence of cells on a single row sharing identical effective style and colors.
/// </summary>
internal readonly record struct GhosttyCellRun(
    int StartColumn,
    int ColumnCount,
    string Text,
    Color Fg,
    Color Bg,
    bool Bold = false,
    bool Italic = false,
    bool Underline = false,
    bool Strikethrough = false,
    bool Faint = false,
    bool Invisible = false)
{
    /// <summary>
    ///  Builds a list of contiguous cell runs from a sequence of raw terminal cells.
    ///  Adjacent cells with identical effective colors and styles are merged into a single run.
    ///  Spacer tail cells (Wide == 2) are absorbed into the preceding run's column count.
    /// </summary>
    /// <param name="cells">The sequence of row cells to group.</param>
    /// <returns>A list of grouped cell runs ready for rendering.</returns>
    public static IReadOnlyList<GhosttyCellRun> Build(IReadOnlyList<GhosttyCell> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);

        if (cells.Count == 0)
        {
            return Array.Empty<GhosttyCellRun>();
        }

        List<GhosttyCellRun> runs = new();
        int activeStartColumn = -1;
        int activeColumnCount = 0;
        StringBuilder activeTextBuilder = new();
        Color activeFg = default;
        Color activeBg = default;
        bool activeBold = false;
        bool activeItalic = false;
        bool activeUnderline = false;
        bool activeStrikethrough = false;
        bool activeFaint = false;
        bool activeInvisible = false;

        for (int index = 0; index < cells.Count; index++)
        {
            GhosttyCell cell = cells[index];

            if (cell.Wide == GhosttyNativeConstants.CellWideSpacerTail)
            {
                if (activeStartColumn >= 0)
                {
                    activeColumnCount++;
                }
                else
                {
                    runs.Add(new GhosttyCellRun(index, 1, string.Empty, cell.Fg, cell.Bg));
                }

                continue;
            }

            Color effectiveFg = cell.Inverse ? cell.Bg : cell.Fg;
            Color effectiveBg = cell.Inverse ? cell.Fg : cell.Bg;
            string cellText = string.IsNullOrEmpty(cell.Text) ? " " : cell.Text;

            bool matches = activeStartColumn >= 0
                && activeFg == effectiveFg
                && activeBg == effectiveBg
                && activeBold == cell.Bold
                && activeItalic == cell.Italic
                && activeUnderline == cell.Underline
                && activeStrikethrough == cell.Strikethrough
                && activeFaint == cell.Faint
                && activeInvisible == cell.Invisible;

            if (matches)
            {
                activeColumnCount++;
                activeTextBuilder.Append(cellText);
            }
            else
            {
                if (activeStartColumn >= 0)
                {
                    runs.Add(new GhosttyCellRun(
                        activeStartColumn,
                        activeColumnCount,
                        activeTextBuilder.ToString(),
                        activeFg,
                        activeBg,
                        activeBold,
                        activeItalic,
                        activeUnderline,
                        activeStrikethrough,
                        activeFaint,
                        activeInvisible));
                    activeTextBuilder.Clear();
                }

                activeStartColumn = index;
                activeColumnCount = 1;
                activeFg = effectiveFg;
                activeBg = effectiveBg;
                activeBold = cell.Bold;
                activeItalic = cell.Italic;
                activeUnderline = cell.Underline;
                activeStrikethrough = cell.Strikethrough;
                activeFaint = cell.Faint;
                activeInvisible = cell.Invisible;
                activeTextBuilder.Append(cellText);
            }
        }

        if (activeStartColumn >= 0)
        {
            runs.Add(new GhosttyCellRun(
                activeStartColumn,
                activeColumnCount,
                activeTextBuilder.ToString(),
                activeFg,
                activeBg,
                activeBold,
                activeItalic,
                activeUnderline,
                activeStrikethrough,
                activeFaint,
                activeInvisible));
        }

        return runs;
    }
}
