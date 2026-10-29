using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using LibGhostty;
using static LibGhostty.GhosttyNativeLibrary;

namespace GitUI.ConsoleEmulation.Ghostty;

internal sealed partial class GhosttyTerminalControl
{
    private const int WheelNotchMultiplier = -3;
    private const int StackBufferSize = 64;

    private readonly GhosttyTerminalSelection _selection = new();

    private IntPtr _keyEncoder = IntPtr.Zero;
    private IntPtr _keyEvent = IntPtr.Zero;
    private IntPtr _mouseEncoder = IntPtr.Zero;
    private IntPtr _mouseEvent = IntPtr.Zero;

    private char? _highSurrogate;
    private bool _isDraggingSelection;
    private bool _outputReceivedSubscribed;

    /// <summary>
    ///  Pastes text from the system clipboard into the terminal, encoding with bracketed paste mode if enabled.
    /// </summary>
    public void Paste()
    {
        if (!Clipboard.ContainsText())
        {
            return;
        }

        string text;
        try
        {
            text = Clipboard.GetText();
        }
        catch (ExternalException)
        {
            return;
        }

        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        byte[] bytes = EncodePasteBytes(text, forceBracketed: null);
        if (bytes.Length > 0)
        {
            SendBytes(bytes);
            ClearSelection();
        }
    }

    /// <summary>
    ///  Copies the currently selected grid text to the system clipboard and clears the selection.
    /// </summary>
    public void CopySelection()
    {
        if (!_selection.IsActive || _selection.IsEmpty)
        {
            return;
        }

        string selectedText = ExtractSelectedText();
        if (!string.IsNullOrEmpty(selectedText))
        {
            try
            {
                Clipboard.SetText(selectedText);
            }
            catch (ExternalException)
            {
            }
        }
    }

    /// <summary>
    ///  Clears the active selection and requests a repaint.
    /// </summary>
    public void ClearSelection()
    {
        if (_selection.IsActive)
        {
            _selection.Clear();
            Invalidate();
        }
    }

    internal byte[] EncodeKeyForTest(Keys keyData)
    {
        EnsureNativeHandlesCreated();
        EnsureInputHandlesCreated();

        if (!GhosttyKeyMap.TryMap(keyData, out int key, out ushort mods))
        {
            return Array.Empty<byte>();
        }

        return EncodeKeyEvent(key, mods, GhosttyKeyMap.UnshiftedCodepoint(keyData & Keys.KeyCode));
    }

    internal byte[] EncodePasteForTest(string text)
    {
        return EncodePasteBytes(text, forceBracketed: false);
    }

    internal bool IsInputKeyForTest(Keys keyData) => IsInputKey(keyData);

    internal void SimulateKeyDown(KeyEventArgs e) => OnKeyDown(e);

    internal void SimulateKeyPress(KeyPressEventArgs e) => OnKeyPress(e);

    internal GhosttyTerminalSelection Selection => _selection;

    internal string ExtractSelectedTextForTest() => ExtractSelectedText();

    protected override bool IsInputKey(Keys keyData)
    {
        if ((keyData & Keys.Alt) != Keys.None && (keyData & Keys.KeyCode) == Keys.F4)
        {
            return false;
        }

        return true;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if ((keyData & Keys.Alt) != Keys.None && (keyData & Keys.KeyCode) == Keys.F4)
        {
            return base.ProcessCmdKey(ref msg, keyData);
        }

        Keys keyCode = keyData & Keys.KeyCode;
        if (keyCode is Keys.Tab or Keys.Escape or Keys.Enter or Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown)
        {
            return false;
        }

        if (GhosttyKeyMap.TryMap(keyData, out _, out _) || GhosttyKeyMap.IsTextKey(keyData))
        {
            return false;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        Keys keyCode = keyData & Keys.KeyCode;
        if (keyCode is Keys.Tab or Keys.Escape or Keys.Enter or Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown)
        {
            return false;
        }

        return base.ProcessDialogKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        Keys modifiers = e.Modifiers;
        Keys keyCode = e.KeyCode;

        if (modifiers == (Keys.Control | Keys.Shift) && keyCode == Keys.C)
        {
            CopySelection();
            ClearSelection();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if ((modifiers == (Keys.Control | Keys.Shift) && keyCode == Keys.V) ||
            (modifiers == Keys.Shift && keyCode == Keys.Insert))
        {
            Paste();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (modifiers == Keys.Control && keyCode == Keys.C)
        {
            if (_selection.IsActive && !_selection.IsEmpty)
            {
                CopySelection();
                ClearSelection();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
        }
        else if (modifiers == Keys.Control && keyCode == Keys.V)
        {
            Paste();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (GhosttyKeyMap.IsTextKey(e.KeyData))
        {
            return;
        }

        if (GhosttyKeyMap.TryMap(e.KeyData, out int key, out ushort mods))
        {
            EnsureInputHandlesCreated();

            byte[] bytes = EncodeKeyEvent(key, mods, GhosttyKeyMap.UnshiftedCodepoint(keyCode));
            if (bytes.Length > 0)
            {
                SendBytes(bytes);
            }

            e.Handled = true;
            e.SuppressKeyPress = true;

            if (keyCode is not (Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin))
            {
                ClearSelection();
            }
        }
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);

        char character = e.KeyChar;
        if (character < 0x20 || character == 0x7F)
        {
            e.Handled = true;
            return;
        }

        if (char.IsHighSurrogate(character))
        {
            _highSurrogate = character;
            e.Handled = true;
            return;
        }

        if (char.IsLowSurrogate(character))
        {
            if (_highSurrogate.HasValue)
            {
                string surrogatePair = new string([_highSurrogate.Value, character]);
                _highSurrogate = null;
                SendText(surrogatePair);
                ClearSelection();
            }

            e.Handled = true;
            return;
        }

        _highSurrogate = null;
        SendText(character.ToString());
        ClearSelection();
        e.Handled = true;
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
        SendFocusEvent(gained: true);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
        SendFocusEvent(gained: false);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        EnsureInputHandlesCreated();

        if (IsMouseTrackingActive())
        {
            SendMouseEvent(MouseEventActionPress, e.Button, e.X, e.Y);
            return;
        }

        if (e.Button == MouseButtons.Left)
        {
            int column = Math.Clamp(e.X / _cellWidth, 0, Math.Max(0, _columns - 1));
            int row = Math.Clamp(e.Y / _cellHeight, 0, Math.Max(0, _rows - 1));

            if (e.Clicks == 2)
            {
                SelectWordAt(column, row);
            }
            else if (e.Clicks >= 3)
            {
                SelectLineAt(row);
            }
            else
            {
                _selection.Begin(column, row);
                _isDraggingSelection = true;
                Invalidate();
            }
        }
        else if (e.Button == MouseButtons.Right)
        {
            if (_selection.IsActive && !_selection.IsEmpty)
            {
                CopySelection();
                ClearSelection();
            }
            else
            {
                Paste();
            }
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        EnsureInputHandlesCreated();

        if (IsMouseTrackingActive())
        {
            SendMouseEvent(MouseEventActionRelease, e.Button, e.X, e.Y);
            return;
        }

        if (e.Button == MouseButtons.Left && _isDraggingSelection)
        {
            _isDraggingSelection = false;
            if (_selection.Anchor == _selection.Head)
            {
                ClearSelection();
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        EnsureInputHandlesCreated();

        if (IsMouseTrackingActive())
        {
            if (e.Button != MouseButtons.None)
            {
                SendMouseEvent(MouseEventActionMotion, e.Button, e.X, e.Y);
            }

            return;
        }

        if (_isDraggingSelection && (e.Button & MouseButtons.Left) != 0)
        {
            int column = Math.Clamp(e.X / _cellWidth, 0, Math.Max(0, _columns - 1));
            int row = Math.Clamp(e.Y / _cellHeight, 0, Math.Max(0, _rows - 1));

            _selection.Extend(column, row);
            Invalidate();
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);

        EnsureInputHandlesCreated();

        if (IsMouseTrackingActive())
        {
            int wheelButton = e.Delta > 0 ? MouseButtonFour : MouseButtonFive;
            SendWheelMouseEvent(wheelButton, e.X, e.Y);
            return;
        }

        int notches = e.Delta / SystemInformation.MouseWheelScrollDelta;
        if (notches == 0 && e.Delta != 0)
        {
            notches = e.Delta > 0 ? 1 : -1;
        }

        int scrollDelta = WheelNotchMultiplier * notches;
        if (scrollDelta != 0 && _terminal != IntPtr.Zero)
        {
            TerminalScrollViewport viewport = new()
            {
                Tag = ScrollViewportDelta,
                Value = new TerminalScrollViewportValue { Delta = (IntPtr)scrollDelta }
            };

            GhosttySession.Library.ScrollViewport(_terminal, viewport);
            UpdateScrollbar();
            Invalidate();
        }
    }

    private void EnsureInputHandlesCreated()
    {
        if (!GhosttySession.IsSupported)
        {
            return;
        }

        GhosttyNativeLibrary library = GhosttySession.Library;

        if (_keyEncoder == IntPtr.Zero)
        {
            Check(library.KeyEncoderNew(out _keyEncoder), "KeyEncoderNew");
        }

        if (_keyEvent == IntPtr.Zero)
        {
            Check(library.KeyEventNew(out _keyEvent), "KeyEventNew");
        }

        if (_mouseEncoder == IntPtr.Zero)
        {
            Check(library.MouseEncoderNew(out _mouseEncoder), "MouseEncoderNew");
        }

        if (_mouseEvent == IntPtr.Zero)
        {
            Check(library.MouseEventNew(out _mouseEvent), "MouseEventNew");
        }

        if (!_outputReceivedSubscribed)
        {
            OutputReceived += OnTerminalOutputReceived;
            _outputReceivedSubscribed = true;
        }
    }

    private void DisposeInputResources()
    {
        if (!GhosttySession.IsSupported)
        {
            return;
        }

        if (_outputReceivedSubscribed)
        {
            OutputReceived -= OnTerminalOutputReceived;
            _outputReceivedSubscribed = false;
        }

        GhosttyNativeLibrary library = GhosttySession.Library;

        if (_keyEvent != IntPtr.Zero)
        {
            library.KeyEventFree(_keyEvent);
            _keyEvent = IntPtr.Zero;
        }

        if (_keyEncoder != IntPtr.Zero)
        {
            library.KeyEncoderFree(_keyEncoder);
            _keyEncoder = IntPtr.Zero;
        }

        if (_mouseEvent != IntPtr.Zero)
        {
            library.MouseEventFree(_mouseEvent);
            _mouseEvent = IntPtr.Zero;
        }

        if (_mouseEncoder != IntPtr.Zero)
        {
            library.MouseEncoderFree(_mouseEncoder);
            _mouseEncoder = IntPtr.Zero;
        }
    }

    private void OnTerminalOutputReceived(object? sender, EventArgs e)
    {
        ClearSelection();
    }

    private bool IsMouseTrackingActive()
    {
        if (_terminal == IntPtr.Zero || !GhosttySession.IsSupported)
        {
            return false;
        }

        return GhosttySession.Library.TerminalGetMouseTracking(_terminal, out byte enabled) == Success && enabled != 0;
    }

    private byte[] EncodeKeyEvent(int key, ushort mods, uint unshiftedCodepoint)
    {
        if (_keyEncoder == IntPtr.Zero || _keyEvent == IntPtr.Zero)
        {
            return Array.Empty<byte>();
        }

        GhosttyNativeLibrary library = GhosttySession.Library;
        if (_terminal != IntPtr.Zero)
        {
            library.KeyEncoderSetFromTerminal(_keyEncoder, _terminal);
        }

        library.KeyEventSetAction(_keyEvent, GhosttyKeyAction.Press);
        library.KeyEventSetKey(_keyEvent, key);
        library.KeyEventSetMods(_keyEvent, mods);
        library.KeyEventSetConsumedMods(_keyEvent, 0);
        library.KeyEventSetUtf8(_keyEvent, IntPtr.Zero, UIntPtr.Zero);
        library.KeyEventSetUnshiftedCodepoint(_keyEvent, unshiftedCodepoint);

        Span<byte> stackBuffer = stackalloc byte[StackBufferSize];
        unsafe
        {
            fixed (byte* bufferPointer = stackBuffer)
            {
                int result = library.KeyEncoderEncode(
                    _keyEncoder,
                    _keyEvent,
                    (IntPtr)bufferPointer,
                    (UIntPtr)stackBuffer.Length,
                    out UIntPtr written);

                if (result == OutOfSpace && (int)written > 0)
                {
                    byte[] largerBuffer = new byte[(int)written];
                    fixed (byte* largerPointer = largerBuffer)
                    {
                        result = library.KeyEncoderEncode(
                            _keyEncoder,
                            _keyEvent,
                            (IntPtr)largerPointer,
                            (UIntPtr)largerBuffer.Length,
                            out written);

                        if (result == Success && (int)written > 0)
                        {
                            return largerBuffer.AsSpan(0, (int)written).ToArray();
                        }
                    }
                }
                else if (result == Success && (int)written > 0)
                {
                    return stackBuffer[..(int)written].ToArray();
                }
            }
        }

        return Array.Empty<byte>();
    }

    private byte[] EncodePasteBytes(string text, bool? forceBracketed)
    {
        string normalized = text.Replace("\r\n", "\r");
        byte[] utf8Bytes = Encoding.UTF8.GetBytes(normalized);
        if (utf8Bytes.Length == 0)
        {
            return Array.Empty<byte>();
        }

        bool bracketed = forceBracketed ?? (_terminal != IntPtr.Zero && GhosttySession.Extras.TerminalModeGet(_terminal, GhosttyNativeConstants.ModeBracketedPaste));

        int bufferSize = Math.Max(256, utf8Bytes.Length + 32);
        byte[] buffer = new byte[bufferSize];

        GhosttyNativeLibrary library = GhosttySession.Library;
        unsafe
        {
            fixed (byte* dataPointer = utf8Bytes)
            {
                fixed (byte* bufferPointer = buffer)
                {
                    int result = library.PasteEncode(
                        (IntPtr)dataPointer,
                        (UIntPtr)utf8Bytes.Length,
                        bracketed,
                        (IntPtr)bufferPointer,
                        (UIntPtr)buffer.Length,
                        out UIntPtr written);

                    if (result == OutOfSpace && (int)written > 0)
                    {
                        byte[] largerBuffer = new byte[(int)written];
                        fixed (byte* largerPointer = largerBuffer)
                        {
                            result = library.PasteEncode(
                                (IntPtr)dataPointer,
                                (UIntPtr)utf8Bytes.Length,
                                bracketed,
                                (IntPtr)largerPointer,
                                (UIntPtr)largerBuffer.Length,
                                out written);

                            if (result == Success && (int)written > 0)
                            {
                                return largerBuffer.AsSpan(0, (int)written).ToArray();
                            }
                        }
                    }
                    else if (result == Success && (int)written > 0)
                    {
                        return buffer.AsSpan(0, (int)written).ToArray();
                    }
                }
            }
        }

        return Array.Empty<byte>();
    }

    private void SendFocusEvent(bool gained)
    {
        if (_terminal == IntPtr.Zero || !GhosttySession.IsSupported)
        {
            return;
        }

        if (!GhosttySession.Extras.TerminalModeGet(_terminal, GhosttyNativeConstants.ModeFocusEvents))
        {
            return;
        }

        Span<byte> buffer = stackalloc byte[16];
        int result = GhosttySession.Extras.FocusEncode(gained, buffer, out nuint written);
        if (result == Success && written > 0)
        {
            SendBytes(buffer[..(int)written]);
        }
    }

    private void SendMouseEvent(int action, MouseButtons button, int pixelX, int pixelY)
    {
        if (_mouseEncoder == IntPtr.Zero || _mouseEvent == IntPtr.Zero || _terminal == IntPtr.Zero)
        {
            return;
        }

        PrepareMouseEncoder();

        GhosttyNativeLibrary library = GhosttySession.Library;
        library.MouseEventSetAction(_mouseEvent, action);

        int nativeButton = MapMouseButton(button);
        if (nativeButton != MouseButtonNone)
        {
            library.MouseEventSetButton(_mouseEvent, nativeButton);
        }
        else
        {
            library.MouseEventClearButton(_mouseEvent);
        }

        library.MouseEventSetMods(_mouseEvent, GetCurrentMouseModifiers());
        library.MouseEventSetPosition(_mouseEvent, new NativeMousePosition { X = pixelX, Y = pixelY });

        EncodeAndSendMouseEvent();
    }

    private void SendWheelMouseEvent(int wheelButton, int pixelX, int pixelY)
    {
        if (_mouseEncoder == IntPtr.Zero || _mouseEvent == IntPtr.Zero || _terminal == IntPtr.Zero)
        {
            return;
        }

        PrepareMouseEncoder();

        GhosttyNativeLibrary library = GhosttySession.Library;
        library.MouseEventSetAction(_mouseEvent, MouseEventActionPress);
        library.MouseEventSetButton(_mouseEvent, wheelButton);
        library.MouseEventSetMods(_mouseEvent, GetCurrentMouseModifiers());
        library.MouseEventSetPosition(_mouseEvent, new NativeMousePosition { X = pixelX, Y = pixelY });

        EncodeAndSendMouseEvent();
    }

    private void PrepareMouseEncoder()
    {
        GhosttyNativeLibrary library = GhosttySession.Library;
        library.MouseEncoderSetFromTerminal(_mouseEncoder, _terminal);

        NativeMouseEncoderSize encoderSize = new()
        {
            Size = (UIntPtr)(nuint)Marshal.SizeOf<NativeMouseEncoderSize>(),
            ScreenWidth = (uint)Math.Max(1, ClientRectangle.Width),
            ScreenHeight = (uint)Math.Max(1, ClientRectangle.Height),
            CellWidth = (uint)Math.Max(1, _cellWidth),
            CellHeight = (uint)Math.Max(1, _cellHeight),
            PaddingTop = 0,
            PaddingBottom = 0,
            PaddingRight = 0,
            PaddingLeft = 0
        };

        library.MouseEncoderSetOption(_mouseEncoder, MouseEncoderOptionSize, ref encoderSize);
    }

    private void EncodeAndSendMouseEvent()
    {
        GhosttyNativeLibrary library = GhosttySession.Library;
        Span<byte> buffer = stackalloc byte[StackBufferSize];

        unsafe
        {
            fixed (byte* bufferPointer = buffer)
            {
                int result = library.MouseEncoderEncode(
                    _mouseEncoder,
                    _mouseEvent,
                    (IntPtr)bufferPointer,
                    (UIntPtr)buffer.Length,
                    out UIntPtr written);

                if (result == OutOfSpace && (int)written > 0)
                {
                    byte[] largerBuffer = new byte[(int)written];
                    fixed (byte* largerPointer = largerBuffer)
                    {
                        result = library.MouseEncoderEncode(
                            _mouseEncoder,
                            _mouseEvent,
                            (IntPtr)largerPointer,
                            (UIntPtr)largerBuffer.Length,
                            out written);

                        if (result == Success && (int)written > 0)
                        {
                            SendBytes(largerBuffer.AsSpan(0, (int)written));
                        }
                    }
                }
                else if (result == Success && (int)written > 0)
                {
                    SendBytes(buffer[..(int)written]);
                }
            }
        }
    }

    private static int MapMouseButton(MouseButtons button)
    {
        if ((button & MouseButtons.Left) != 0)
        {
            return MouseButtonLeft;
        }

        if ((button & MouseButtons.Right) != 0)
        {
            return MouseButtonRight;
        }

        if ((button & MouseButtons.Middle) != 0)
        {
            return MouseButtonMiddle;
        }

        return MouseButtonNone;
    }

    private static ushort GetCurrentMouseModifiers()
    {
        ushort mods = 0;
        Keys modifierKeys = ModifierKeys;

        if ((modifierKeys & Keys.Shift) != Keys.None)
        {
            mods |= GhosttyMods.Shift;
        }

        if ((modifierKeys & Keys.Control) != Keys.None)
        {
            mods |= GhosttyMods.Ctrl;
        }

        if ((modifierKeys & Keys.Alt) != Keys.None)
        {
            mods |= GhosttyMods.Alt;
        }

        return mods;
    }

    private void SelectWordAt(int column, int row)
    {
        string line = GetRowText(row);
        if (string.IsNullOrEmpty(line) || column >= line.Length)
        {
            return;
        }

        char target = line[column];
        bool isSpace = char.IsWhiteSpace(target);
        bool isWordChar = !isSpace && (char.IsLetterOrDigit(target) || target == '_');

        int start = column;
        while (start > 0)
        {
            char c = line[start - 1];
            if (isSpace && !char.IsWhiteSpace(c))
            {
                break;
            }

            if (isWordChar && !(char.IsLetterOrDigit(c) || c == '_'))
            {
                break;
            }

            if (!isSpace && !isWordChar && (char.IsWhiteSpace(c) || char.IsLetterOrDigit(c) || c == '_'))
            {
                break;
            }

            start--;
        }

        int end = column;
        while (end < line.Length - 1)
        {
            char c = line[end + 1];
            if (isSpace && !char.IsWhiteSpace(c))
            {
                break;
            }

            if (isWordChar && !(char.IsLetterOrDigit(c) || c == '_'))
            {
                break;
            }

            if (!isSpace && !isWordChar && (char.IsWhiteSpace(c) || char.IsLetterOrDigit(c) || c == '_'))
            {
                break;
            }

            end++;
        }

        _selection.Begin(start, row);
        _selection.Extend(end, row);
        Invalidate();
    }

    private void SelectLineAt(int row)
    {
        _selection.Begin(0, row);
        _selection.Extend(Math.Max(0, _columns - 1), row);
        Invalidate();
    }

    private string GetRowText(int targetRow)
    {
        if (_terminal == IntPtr.Zero || _renderState == IntPtr.Zero)
        {
            return string.Empty;
        }

        GhosttyNativeLibrary library = GhosttySession.Library;
        library.RenderStateUpdate(_renderState, _terminal);

        IntPtr iterator = _rowIterator;
        if (library.RenderStateGetHandle(_renderState, RenderStateDataRowIterator, ref iterator) != Success)
        {
            return string.Empty;
        }

        int currentRow = 0;
        while (library.RowIteratorNext(iterator))
        {
            if (currentRow == targetRow)
            {
                IntPtr rowCellsHandle = _rowCells;
                if (library.RowGetCells(iterator, ref rowCellsHandle) != Success)
                {
                    return string.Empty;
                }

                StringBuilder stringBuilder = new(_columns);
                for (int col = 0; col < _columns; col++)
                {
                    if (library.RowCellsSelect(rowCellsHandle, (ushort)col) != Success)
                    {
                        break;
                    }

                    if (library.RowCellsGetRaw(rowCellsHandle, out ulong rawCell) == Success)
                    {
                        library.CellGetWide(rawCell, out int wide);
                        if (wide == GhosttyNativeConstants.CellWideSpacerTail)
                        {
                            continue;
                        }
                    }

                    library.RowCellsGetU32(rowCellsHandle, RenderStateRowCellsDataGraphemesLength, out uint length);
                    if (length > 0 && length <= MaxGraphemeLength)
                    {
                        unsafe
                        {
                            fixed (uint* codepointPointer = _codepointBuffer)
                            {
                                if (library.RowCellsGetBuffer(rowCellsHandle, RenderStateRowCellsDataGraphemesBuffer, (IntPtr)codepointPointer) == Success)
                                {
                                    for (int i = 0; i < (int)length; i++)
                                    {
                                        uint codepoint = _codepointBuffer[i];
                                        if (codepoint is > 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF))
                                        {
                                            stringBuilder.Append(char.ConvertFromUtf32((int)codepoint));
                                        }
                                    }

                                    continue;
                                }
                            }
                        }
                    }

                    stringBuilder.Append(' ');
                }

                return stringBuilder.ToString();
            }

            currentRow++;
        }

        return string.Empty;
    }

    private string ExtractSelectedText()
    {
        if (!_selection.IsActive || _terminal == IntPtr.Zero || _renderState == IntPtr.Zero)
        {
            return string.Empty;
        }

        var (start, end) = _selection.Normalized;
        GhosttyNativeLibrary library = GhosttySession.Library;
        library.RenderStateUpdate(_renderState, _terminal);

        IntPtr iterator = _rowIterator;
        if (library.RenderStateGetHandle(_renderState, RenderStateDataRowIterator, ref iterator) != Success)
        {
            return string.Empty;
        }

        List<string> rowTexts = new();
        int currentRow = 0;

        while (library.RowIteratorNext(iterator))
        {
            if (currentRow >= start.Row && currentRow <= end.Row)
            {
                IntPtr rowCellsHandle = _rowCells;
                if (library.RowGetCells(iterator, ref rowCellsHandle) != Success)
                {
                    currentRow++;
                    continue;
                }

                int colStart = (currentRow == start.Row) ? start.Col : 0;
                int colEnd = (currentRow == end.Row) ? end.Col : (_columns - 1);

                colStart = Math.Clamp(colStart, 0, Math.Max(0, _columns - 1));
                colEnd = Math.Clamp(colEnd, 0, Math.Max(0, _columns - 1));

                StringBuilder rowBuilder = new();
                for (int col = colStart; col <= colEnd; col++)
                {
                    if (library.RowCellsSelect(rowCellsHandle, (ushort)col) != Success)
                    {
                        break;
                    }

                    if (library.RowCellsGetRaw(rowCellsHandle, out ulong rawCell) == Success)
                    {
                        library.CellGetWide(rawCell, out int wide);
                        if (wide == GhosttyNativeConstants.CellWideSpacerTail)
                        {
                            continue;
                        }
                    }

                    library.RowCellsGetU32(rowCellsHandle, RenderStateRowCellsDataGraphemesLength, out uint length);
                    if (length > 0 && length <= MaxGraphemeLength)
                    {
                        unsafe
                        {
                            fixed (uint* codepointPointer = _codepointBuffer)
                            {
                                if (library.RowCellsGetBuffer(rowCellsHandle, RenderStateRowCellsDataGraphemesBuffer, (IntPtr)codepointPointer) == Success)
                                {
                                    for (int i = 0; i < (int)length; i++)
                                    {
                                        uint codepoint = _codepointBuffer[i];
                                        if (codepoint is > 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF))
                                        {
                                            rowBuilder.Append(char.ConvertFromUtf32((int)codepoint));
                                        }
                                    }

                                    continue;
                                }
                            }
                        }
                    }

                    rowBuilder.Append(' ');
                }

                string trimmedRow = rowBuilder.ToString().TrimEnd();
                rowTexts.Add(trimmedRow);
            }

            currentRow++;
        }

        return string.Join("\r\n", rowTexts);
    }
}
