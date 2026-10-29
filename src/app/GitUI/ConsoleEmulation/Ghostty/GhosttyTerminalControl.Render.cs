using System.Diagnostics;
using System.Drawing;
using System.Text;
using LibGhostty;
using static LibGhostty.GhosttyNativeLibrary;

namespace GitUI.ConsoleEmulation.Ghostty;

internal sealed partial class GhosttyTerminalControl
{
    private const int MaxGraphemeLength = 64;
    private const int CursorStyleBar = 0;
    private const int CursorStyleBlock = 1;
    private const int CursorStyleUnderline = 2;
    private const int CursorStyleBlockHollow = 3;

    private readonly List<GhosttyCell> _rowCellsBuffer = new(256);
    private readonly StringBuilder _graphemeBuilder = new(16);
    private readonly uint[] _codepointBuffer = new uint[MaxGraphemeLength];
    private readonly Dictionary<FontStyle, Font> _fontCache = new();

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics graphics = e.Graphics;

        if (_terminal == IntPtr.Zero)
        {
            graphics.Clear(_currentScheme?.Background ?? Color.Black);
            return;
        }

        GhosttyNativeLibrary library = GhosttySession.Library;
        GhosttyNativeExtras extras = GhosttySession.Extras;

        int updateResult = library.RenderStateUpdate(_renderState, _terminal);
        if (updateResult != Success)
        {
            Trace.WriteLine($"RenderStateUpdate failed with result {updateResult}.");
            graphics.Clear(_currentScheme?.Background ?? Color.Black);
            return;
        }

        library.RenderStateGetColor(_renderState, RenderStateDataColorForeground, out NativeColor defaultForegroundNative);
        library.RenderStateGetColor(_renderState, RenderStateDataColorBackground, out NativeColor defaultBackgroundNative);
        Color defaultForeground = ToColor(defaultForegroundNative);
        Color defaultBackground = ToColor(defaultBackgroundNative);

        graphics.Clear(defaultBackground);

        library.RenderStateGetByte(_renderState, RenderStateDataCursorVisible, out byte cursorVisibleByte);
        library.RenderStateGetByte(_renderState, RenderStateDataCursorViewportHasValue, out byte cursorHasValueByte);
        library.RenderStateGetU16(_renderState, RenderStateDataCursorViewportX, out ushort cursorX);
        library.RenderStateGetU16(_renderState, RenderStateDataCursorViewportY, out ushort cursorY);
        library.RenderStateGetInt(_renderState, RenderStateDataCursorVisualStyle, out int visualStyle);

        bool cursorVisible = cursorVisibleByte != 0 && cursorHasValueByte != 0;
        string cursorUnderlyingText = string.Empty;
        FontStyle cursorUnderlyingStyle = FontStyle.Regular;

        IntPtr iterator = _rowIterator;
        Check(library.RenderStateGetHandle(_renderState, RenderStateDataRowIterator, ref iterator), "RenderStateGetHandle(ROW_ITERATOR)");

        int row = 0;
        while (library.RowIteratorNext(iterator))
        {
            IntPtr rowCellsHandle = _rowCells;
            Check(library.RowGetCells(iterator, ref rowCellsHandle), "RowGetCells");

            _rowCellsBuffer.Clear();

            for (int col = 0; col < _columns; col++)
            {
                if (library.RowCellsSelect(rowCellsHandle, (ushort)col) != Success)
                {
                    break;
                }

                string cellText = string.Empty;
                library.RowCellsGetU32(rowCellsHandle, RenderStateRowCellsDataGraphemesLength, out uint length);
                if (length > 0 && length <= MaxGraphemeLength)
                {
                    unsafe
                    {
                        fixed (uint* codepointPointer = _codepointBuffer)
                        {
                            if (library.RowCellsGetBuffer(rowCellsHandle, RenderStateRowCellsDataGraphemesBuffer, (IntPtr)codepointPointer) == Success)
                            {
                                _graphemeBuilder.Clear();
                                for (int i = 0; i < (int)length; i++)
                                {
                                    uint codepoint = _codepointBuffer[i];
                                    if (codepoint is > 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF))
                                    {
                                        _graphemeBuilder.Append(char.ConvertFromUtf32((int)codepoint));
                                    }
                                    else
                                    {
                                        _graphemeBuilder.Append('?');
                                    }
                                }

                                cellText = _graphemeBuilder.ToString();
                            }
                        }
                    }
                }

                library.RowCellsGetStyle(rowCellsHandle, out NativeStyle style);
                bool hasForeground = library.RowCellsGetColor(rowCellsHandle, RenderStateRowCellsDataForegroundColor, out NativeColor nativeForeground) == Success;
                bool hasBackground = library.RowCellsGetColor(rowCellsHandle, RenderStateRowCellsDataBackgroundColor, out NativeColor nativeBackground) == Success;

                Color foregroundColor = hasForeground ? ToColor(nativeForeground) : defaultForeground;
                Color backgroundColor = hasBackground ? ToColor(nativeBackground) : defaultBackground;

                int wide = 0;
                if (library.RowCellsGetRaw(rowCellsHandle, out ulong rawCell) == Success)
                {
                    library.CellGetWide(rawCell, out wide);
                }

                bool bold = style.Bold != 0;
                bool italic = style.Italic != 0;
                bool underline = style.Underline != 0;
                bool strikethrough = style.Strikethrough != 0;
                bool inverse = style.Inverse != 0;
                bool faint = style.Faint != 0;
                bool invisible = style.Invisible != 0;

                if (row == cursorY && col == cursorX)
                {
                    cursorUnderlyingText = cellText;
                    if (bold)
                    {
                        cursorUnderlyingStyle |= FontStyle.Bold;
                    }

                    if (italic)
                    {
                        cursorUnderlyingStyle |= FontStyle.Italic;
                    }
                }

                _rowCellsBuffer.Add(new GhosttyCell(
                    cellText,
                    wide,
                    foregroundColor,
                    backgroundColor,
                    bold,
                    italic,
                    underline,
                    strikethrough,
                    inverse,
                    faint,
                    invisible));
            }

            IReadOnlyList<GhosttyCellRun> runs = GhosttyCellRun.Build(_rowCellsBuffer);
            for (int index = 0; index < runs.Count; index++)
            {
                GhosttyCellRun run = runs[index];
                Rectangle bounds = new(
                    run.StartColumn * _cellWidth,
                    row * _cellHeight,
                    run.ColumnCount * _cellWidth,
                    _cellHeight);

                if (run.Bg != defaultBackground)
                {
                    using SolidBrush backgroundBrush = new(run.Bg);
                    graphics.FillRectangle(backgroundBrush, bounds);
                }

                if (!string.IsNullOrWhiteSpace(run.Text) && !run.Invisible)
                {
                    Color textForeground = run.Fg;
                    if (run.Faint)
                    {
                        textForeground = Color.FromArgb(
                            (textForeground.R + run.Bg.R) / 2,
                            (textForeground.G + run.Bg.G) / 2,
                            (textForeground.B + run.Bg.B) / 2);
                    }

                    FontStyle fontStyle = FontStyle.Regular;
                    if (run.Bold)
                    {
                        fontStyle |= FontStyle.Bold;
                    }

                    if (run.Italic)
                    {
                        fontStyle |= FontStyle.Italic;
                    }

                    if (run.Underline)
                    {
                        fontStyle |= FontStyle.Underline;
                    }

                    if (run.Strikethrough)
                    {
                        fontStyle |= FontStyle.Strikeout;
                    }

                    Font cellFont = GetCachedFont(fontStyle);
                    TextRenderer.DrawText(
                        graphics,
                        run.Text,
                        cellFont,
                        bounds,
                        textForeground,
                        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine);
                }
            }

            extras.RowSetDirty(iterator, false);
            row++;
        }

        if (cursorVisible && cursorX < _columns && cursorY < _rows)
        {
            DrawCursor(graphics, cursorX, cursorY, visualStyle, defaultForeground, defaultBackground, cursorUnderlyingText, cursorUnderlyingStyle);
        }

        UpdateScrollbar();
    }

    private static Color ToColor(NativeColor color) => Color.FromArgb(color.Red, color.Green, color.Blue);

    private static NativeColor ToNativeColor(Color color) => new() { Red = color.R, Green = color.G, Blue = color.B };

    private void DrawCursor(
        Graphics graphics,
        int cursorX,
        int cursorY,
        int visualStyle,
        Color defaultForeground,
        Color defaultBackground,
        string cursorText,
        FontStyle cursorTextStyle)
    {
        Rectangle cursorRect = new(cursorX * _cellWidth, cursorY * _cellHeight, _cellWidth, _cellHeight);
        Color cursorColor = _currentScheme?.Cursor ?? defaultForeground;

        int effectiveStyle = Focused ? visualStyle : CursorStyleBlockHollow;

        switch (effectiveStyle)
        {
            case CursorStyleBar:
                using (Pen linePen = new(cursorColor, 2f))
                {
                    graphics.DrawLine(linePen, cursorRect.Left + 1, cursorRect.Top, cursorRect.Left + 1, cursorRect.Bottom - 1);
                }

                break;

            case CursorStyleUnderline:
                using (Pen underlinePen = new(cursorColor, 2f))
                {
                    graphics.DrawLine(underlinePen, cursorRect.Left, cursorRect.Bottom - 1, cursorRect.Right - 1, cursorRect.Bottom - 1);
                }

                break;

            case CursorStyleBlockHollow:
                using (Pen borderPen = new(cursorColor, 1f))
                {
                    graphics.DrawRectangle(borderPen, cursorRect.X, cursorRect.Y, cursorRect.Width - 1, cursorRect.Height - 1);
                }

                break;

            case CursorStyleBlock:
            default:
                using (SolidBrush blockBrush = new(cursorColor))
                {
                    graphics.FillRectangle(blockBrush, cursorRect);
                }

                if (!string.IsNullOrWhiteSpace(cursorText))
                {
                    Font cursorFont = GetCachedFont(cursorTextStyle);
                    TextRenderer.DrawText(
                        graphics,
                        cursorText,
                        cursorFont,
                        cursorRect,
                        defaultBackground,
                        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine);
                }

                break;
        }
    }

    private void OnScrollBarScroll(object? sender, ScrollEventArgs e)
    {
        if (_updatingScrollbar || _terminal == IntPtr.Zero)
        {
            return;
        }

        int delta = e.NewValue - _currentScrollOffset;
        if (delta != 0)
        {
            TerminalScrollViewport viewport = new()
            {
                Tag = ScrollViewportDelta,
                Value = new TerminalScrollViewportValue { Delta = (IntPtr)delta }
            };

            GhosttySession.Library.ScrollViewport(_terminal, viewport);
            _currentScrollOffset = e.NewValue;
            Invalidate();
        }
    }

    private void UpdateScrollbar()
    {
        if (_terminal == IntPtr.Zero || _updatingScrollbar)
        {
            return;
        }

        _updatingScrollbar = true;
        try
        {
            if (GhosttySession.Library.TerminalGetScrollbar(_terminal, out NativeScrollbar scrollbar) == Success)
            {
                int total = (int)Math.Min(int.MaxValue, scrollbar.Total);
                int length = (int)Math.Min(int.MaxValue, scrollbar.Length);
                int offset = (int)Math.Min(int.MaxValue, scrollbar.Offset);

                _scrollBar.Minimum = 0;
                _scrollBar.Maximum = Math.Max(0, total);
                _scrollBar.LargeChange = Math.Max(1, length);
                _scrollBar.SmallChange = 1;

                int maxValue = Math.Max(0, total - length);
                _scrollBar.Value = Math.Clamp(offset, 0, maxValue);
                _currentScrollOffset = _scrollBar.Value;
            }
        }
        finally
        {
            _updatingScrollbar = false;
        }
    }

    private Font GetCachedFont(FontStyle style)
    {
        if (!_fontCache.TryGetValue(style, out Font? font))
        {
            font = new Font(_font, style);
            _fontCache[style] = font;
        }

        return font;
    }

    private void ClearFontCache()
    {
        foreach (Font font in _fontCache.Values)
        {
            font.Dispose();
        }

        _fontCache.Clear();
    }

    private void DisposeFontCache()
    {
        ClearFontCache();
    }
}
