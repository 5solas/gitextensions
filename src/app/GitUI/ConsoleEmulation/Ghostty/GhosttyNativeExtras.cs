using System.Runtime.InteropServices;
using LibGhostty;
using static LibGhostty.GhosttyNativeLibrary;

namespace GitUI.ConsoleEmulation.Ghostty;

/// <summary>
///  Native exports and helper methods not directly exposed by LibGhostty.Net 1.0.2 bindings.
///  Function pointers are resolved dynamically via <see cref="NativeLibrary"/>.
/// </summary>
internal sealed unsafe class GhosttyNativeExtras : IDisposable
{
    private const int ExpectedPaletteColorCount = 256;

    private readonly GhosttyNativeLibrary _library;
    private readonly IntPtr _nativeLibraryHandle;

    private readonly delegate* unmanaged[Cdecl]<IntPtr, int, void*, int> _terminalGet;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, ushort, byte*, int> _terminalModeGet;
    private readonly delegate* unmanaged[Cdecl]<int, byte*, nuint, nuint*, int> _focusEncode;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, int, void*, int> _rowGet;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, int, void*, int> _rowSet;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, void> _terminalReset;

    private bool _disposed;

    /// <summary>
    ///  Initializes a new instance of the <see cref="GhosttyNativeExtras"/> class.
    /// </summary>
    /// <param name="library">The native library wrapper providing the library path.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="library"/> is null.</exception>
    /// <exception cref="EntryPointNotFoundException">Thrown when an expected native export is missing.</exception>
    public GhosttyNativeExtras(GhosttyNativeLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);

        _library = library;
        _nativeLibraryHandle = NativeLibrary.Load(library.LibraryPath);

        _terminalGet = (delegate* unmanaged[Cdecl]<IntPtr, int, void*, int>)GetRequiredExport(_nativeLibraryHandle, "ghostty_terminal_get");
        _terminalModeGet = (delegate* unmanaged[Cdecl]<IntPtr, ushort, byte*, int>)GetRequiredExport(_nativeLibraryHandle, "ghostty_terminal_mode_get");
        _focusEncode = (delegate* unmanaged[Cdecl]<int, byte*, nuint, nuint*, int>)GetRequiredExport(_nativeLibraryHandle, "ghostty_focus_encode");
        _rowGet = (delegate* unmanaged[Cdecl]<IntPtr, int, void*, int>)GetRequiredExport(_nativeLibraryHandle, "ghostty_render_state_row_get");
        _rowSet = (delegate* unmanaged[Cdecl]<IntPtr, int, void*, int>)GetRequiredExport(_nativeLibraryHandle, "ghostty_render_state_row_set");
        _terminalReset = (delegate* unmanaged[Cdecl]<IntPtr, void>)GetRequiredExport(_nativeLibraryHandle, "ghostty_terminal_reset");
    }

    /// <summary>
    ///  Retrieves an unsigned 16-bit terminal property such as column or row count.
    /// </summary>
    public int TerminalGetU16(IntPtr terminal, int data, out ushort value)
    {
        fixed (ushort* valuePointer = &value)
        {
            return _terminalGet(terminal, data, valuePointer);
        }
    }

    /// <summary>
    ///  Retrieves a byte terminal property such as cursor visibility.
    /// </summary>
    public int TerminalGetByte(IntPtr terminal, int data, out byte value)
    {
        fixed (byte* valuePointer = &value)
        {
            return _terminalGet(terminal, data, valuePointer);
        }
    }

    /// <summary>
    ///  Gets the current state of a DEC private mode (e.g. bracketed paste mode 2004).
    /// </summary>
    public bool TerminalModeGet(IntPtr terminal, ushort mode)
    {
        byte value = 0;
        int result = _terminalModeGet(terminal, mode, &value);
        return result == Success && value != 0;
    }

    /// <summary>
    ///  Encodes a focus event into terminal escape sequence bytes (ESC [ I or ESC [ O).
    /// </summary>
    public int FocusEncode(bool gained, Span<byte> buffer, out nuint written)
    {
        int focusEvent = gained ? GhosttyNativeConstants.FocusGained : GhosttyNativeConstants.FocusLost;
        fixed (byte* bufferPointer = buffer)
        {
            fixed (nuint* writtenPointer = &written)
            {
                return _focusEncode(focusEvent, bufferPointer, (nuint)buffer.Length, writtenPointer);
            }
        }
    }

    /// <summary>
    ///  Checks whether the row pointed to by the iterator is marked dirty.
    /// </summary>
    public bool RowIsDirty(IntPtr rowIterator)
    {
        byte dirtyValue = 0;
        int result = _rowGet(rowIterator, GhosttyNativeConstants.RenderStateRowDataDirty, &dirtyValue);
        return result == Success && dirtyValue != 0;
    }

    /// <summary>
    ///  Marks or clears the dirty state of the row pointed to by the iterator.
    /// </summary>
    public void RowSetDirty(IntPtr rowIterator, bool dirty)
    {
        byte dirtyValue = dirty ? (byte)1 : (byte)0;
        _rowSet(rowIterator, GhosttyNativeConstants.RenderStateRowOptionDirty, &dirtyValue);
    }

    /// <summary>
    ///  Sets the complete 256-color palette for the terminal.
    /// </summary>
    public int TerminalSetPalette(IntPtr terminal, ReadOnlySpan<NativeColor> palette256)
    {
        if (palette256.Length != ExpectedPaletteColorCount)
        {
            throw new ArgumentException($"Palette must contain exactly {ExpectedPaletteColorCount} colors.", nameof(palette256));
        }

        NativeColor* nativeBuffer = stackalloc NativeColor[ExpectedPaletteColorCount];
        palette256.CopyTo(new Span<NativeColor>(nativeBuffer, ExpectedPaletteColorCount));
        return _library.TerminalSet(terminal, GhosttyNativeConstants.TerminalOptionColorPalette, (IntPtr)nativeBuffer);
    }

    /// <summary>
    ///  Sets the terminal cursor color.
    /// </summary>
    public int TerminalSetCursorColor(IntPtr terminal, NativeColor color)
    {
        NativeColor* colorPointer = stackalloc NativeColor[1];
        *colorPointer = color;
        return _library.TerminalSet(terminal, GhosttyNativeConstants.TerminalOptionColorCursor, (IntPtr)colorPointer);
    }

    /// <summary>
    ///  Sets the terminal default foreground color.
    /// </summary>
    public int TerminalSetForeground(IntPtr terminal, NativeColor color)
    {
        NativeColor* colorPointer = stackalloc NativeColor[1];
        *colorPointer = color;
        return _library.TerminalSet(terminal, TerminalOptionColorForeground, (IntPtr)colorPointer);
    }

    /// <summary>
    ///  Sets the terminal default background color.
    /// </summary>
    public int TerminalSetBackground(IntPtr terminal, NativeColor color)
    {
        NativeColor* colorPointer = stackalloc NativeColor[1];
        *colorPointer = color;
        return _library.TerminalSet(terminal, TerminalOptionColorBackground, (IntPtr)colorPointer);
    }

    /// <summary>
    ///  Resets the terminal state.
    /// </summary>
    public void TerminalReset(IntPtr terminal)
    {
        _terminalReset(terminal);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        NativeLibrary.Free(_nativeLibraryHandle);
        _disposed = true;
    }

    private static IntPtr GetRequiredExport(IntPtr libraryHandle, string exportName)
    {
        if (!NativeLibrary.TryGetExport(libraryHandle, exportName, out IntPtr address))
        {
            throw new EntryPointNotFoundException($"Native export '{exportName}' was not found.");
        }

        return address;
    }
}
