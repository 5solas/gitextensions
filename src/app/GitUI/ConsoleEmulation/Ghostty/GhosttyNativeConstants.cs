namespace GitUI.ConsoleEmulation.Ghostty;

/// <summary>
///  Native constants and option codes not exposed by LibGhostty.Net 1.0.2.
///  Verified against spike measurements in spike/FINDINGS.md and ghostty C headers.
/// </summary>
internal static class GhosttyNativeConstants
{
    // Terminal options (ghostty_terminal_set / TerminalSet)
    // Source: spike/FINDINGS.md section 6, line 70
    public const int TerminalOptionColorCursor = 13;
    public const int TerminalOptionColorPalette = 14;

    // Terminal data keys (ghostty_terminal_get)
    // Source: spike/FINDINGS.md section 6, line 64
    public const int TerminalDataCols = 1;
    public const int TerminalDataRows = 2;
    public const int TerminalDataCursorX = 3;
    public const int TerminalDataCursorY = 4;
    public const int TerminalDataCursorVisible = 7;

    // Render state row data & options (ghostty_render_state_row_get / set)
    // Source: spike/FINDINGS.md section 6, lines 63 and 67
    public const int RenderStateRowDataDirty = 1;
    public const int RenderStateRowOptionDirty = 0;

    // Focus event types (ghostty_focus_encode)
    // Source: spike/FINDINGS.md section 6, line 68
    public const int FocusGained = 0;
    public const int FocusLost = 1;

    // Cell width classifications (ghostty_cell_get / CellGetWide)
    // Source: spike/FINDINGS.md section 7, line 85
    public const int CellWideNarrow = 0;
    public const int CellWideWide = 1;
    public const int CellWideSpacerTail = 2;
    public const int CellWideSpacerHead = 3;

    // DEC private modes (ghostty_terminal_mode_get)
    // Source: spike/FINDINGS.md section 6, line 65
    public const ushort ModeCursorKeys = 1;
    public const ushort ModeAutoWrap = 7;
    public const ushort ModeCursorVisible = 25;
    public const ushort ModeFocusEvents = 1004;
    public const ushort ModeBracketedPaste = 2004;
}
