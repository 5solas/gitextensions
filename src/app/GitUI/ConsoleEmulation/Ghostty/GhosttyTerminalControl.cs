using System.Collections.Concurrent;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Text;
using LibGhostty;
using static LibGhostty.GhosttyNativeLibrary;

namespace GitUI.ConsoleEmulation.Ghostty;

/// <summary>
///  WinForms terminal control that directly hosts and renders a PTY process using libghostty-vt.
/// </summary>
[DesignerCategory("")]
internal sealed partial class GhosttyTerminalControl : Control
{
    private const int DefaultColumns = 80;
    private const int DefaultRows = 24;
    private const int ReadBufferSize = 65536;
    private const uint MaxScrollbackLines = 10000;
    private const string DefaultFontFamily = "Consolas";
    private const float DefaultFontSize = 10f;
    private const string PtyProcessName = "gitextensions-ghostty";

    private readonly object _writeLock = new();
    private readonly ConcurrentQueue<byte[]> _outputQueue = new();
    private readonly WritePtyCallback _writePtyCallback;
    private readonly VScrollBar _scrollBar;

    private IntPtr _terminal = IntPtr.Zero;
    private IntPtr _renderState = IntPtr.Zero;
    private IntPtr _rowIterator = IntPtr.Zero;
    private IntPtr _rowCells = IntPtr.Zero;

    private IGhosttyPtyConnection? _connection;
    private CancellationTokenSource? _readerCts;
    private GhosttyColorScheme? _currentScheme;
    private Font _font;
    private ReadOnlyMemory<byte> _lastOutputChunk = ReadOnlyMemory<byte>.Empty;

    private int _columns = DefaultColumns;
    private int _rows = DefaultRows;
    private int _cellWidth;
    private int _cellHeight;
    private int _drainScheduled;
    private int _currentScrollOffset;
    private bool _updatingScrollbar;
    private bool _isProcessRunning;
    private bool _disposed;

    /// <summary>
    ///  Initializes a new instance of the <see cref="GhosttyTerminalControl"/> class.
    /// </summary>
    public GhosttyTerminalControl()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.Opaque |
            ControlStyles.Selectable |
            ControlStyles.ResizeRedraw,
            true);

        TabStop = true;
        _writePtyCallback = OnTerminalWritePty;

        _font = new Font(DefaultFontFamily, DefaultFontSize, FontStyle.Regular, GraphicsUnit.Point);
        UpdateCellMetrics();

        _scrollBar = new VScrollBar
        {
            Dock = DockStyle.Right,
            Visible = true
        };
        _scrollBar.Scroll += OnScrollBarScroll;
        Controls.Add(_scrollBar);
    }

    /// <summary>
    ///  Occurs on the UI thread when the PTY process has exited.
    /// </summary>
    public event EventHandler<int>? ProcessExited;

    /// <summary>
    ///  Occurs on the UI thread immediately after terminal output data has been received and written to the terminal.
    /// </summary>
    public event EventHandler? OutputReceived;

    /// <summary>
    ///  Gets a value indicating whether the underlying PTY process is currently running.
    /// </summary>
    public bool IsProcessRunning => _isProcessRunning && _connection is not null;

    /// <summary>
    ///  Gets the number of columns in the terminal grid.
    /// </summary>
    public int Columns => _columns;

    /// <summary>
    ///  Gets the number of rows in the terminal grid.
    /// </summary>
    public int Rows => _rows;

    /// <summary>
    ///  Gets the native libghostty terminal handle, or <see cref="IntPtr.Zero"/> if not yet initialized.
    /// </summary>
    public IntPtr TerminalHandle => _terminal;

    /// <summary>
    ///  Gets the most recent output chunk processed by the terminal.
    /// </summary>
    public ReadOnlyMemory<byte> LastOutputChunk => _lastOutputChunk;

    /// <summary>
    ///  Starts a PTY process with the specified application, arguments, working directory, and environment.
    ///  If a process is already running, the previous process is terminated and the terminal is reset.
    /// </summary>
    public void Start(string app, IReadOnlyList<string> commandLine, string workingDirectory, IReadOnlyDictionary<string, string>? environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(app);

        if (!GhosttySession.IsSupported)
        {
            throw new PlatformNotSupportedException("libghostty-vt is not supported in the current environment.");
        }

        Stop();

        EnsureNativeHandlesCreated();

        if (_terminal != IntPtr.Zero)
        {
            GhosttySession.Extras.TerminalReset(_terminal);
        }

        _readerCts = new CancellationTokenSource();
        int cols = _columns > 0 ? _columns : DefaultColumns;
        int rows = _rows > 0 ? _rows : DefaultRows;

        GhosttyPtyOptions options = new()
        {
            App = ResolveApplicationPath(app),
            CommandLine = commandLine ?? Array.Empty<string>(),
            Cwd = workingDirectory ?? string.Empty,
            Environment = environment ?? new Dictionary<string, string>(),
            Cols = cols,
            Rows = rows,
            Name = PtyProcessName
        };

        IGhosttyPtyConnection connection = ThreadHelper.JoinableTaskFactory.Run(() => GhosttyPtyConnectionFactory.StartAsync(options, _readerCts.Token));
        _connection = connection;
        _isProcessRunning = true;
        connection.ProcessExited += OnConnectionProcessExited;

        _ = Task.Run(() => ReadLoop(connection, _readerCts.Token));
    }

    /// <summary>
    ///  Terminates the running PTY process if any. The <see cref="ProcessExited"/> event will be raised.
    /// </summary>
    public void Stop()
    {
        ClearSelection();

        IGhosttyPtyConnection? connection = _connection;
        if (connection is null)
        {
            return;
        }

        _connection = null;
        _isProcessRunning = false;
        connection.ProcessExited -= OnConnectionProcessExited;

        try
        {
            if (_readerCts is not null)
            {
                _readerCts.Cancel();
                _readerCts.Dispose();
                _readerCts = null;
            }

            connection.Kill();
            connection.Dispose();
        }
        catch
        {
        }
        finally
        {
            while (_outputQueue.TryDequeue(out _))
            {
            }

            _lastOutputChunk = ReadOnlyMemory<byte>.Empty;
        }
    }

    /// <summary>
    ///  Encodes the given text as UTF-8 and writes it directly to the PTY input stream.
    /// </summary>
    public void SendText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(text);
        SendBytes(bytes);
    }

    /// <summary>
    ///  Writes raw bytes directly to the PTY input stream in a thread-safe manner.
    /// </summary>
    public void SendBytes(ReadOnlySpan<byte> data)
    {
        if (_connection is null || data.IsEmpty)
        {
            return;
        }

        lock (_writeLock)
        {
            _connection.WriterStream.Write(data);
            _connection.WriterStream.Flush();
        }
    }

    /// <summary>
    ///  Applies the specified color scheme to the terminal palette, foreground, background, and cursor.
    /// </summary>
    public void ApplyColorScheme(GhosttyColorScheme scheme)
    {
        ArgumentNullException.ThrowIfNull(scheme);

        _currentScheme = scheme;

        if (_terminal != IntPtr.Zero)
        {
            ApplyColorSchemeCore(scheme);
            Invalidate();
        }
    }

    /// <summary>
    ///  Applies the specified font, recalculates cell metrics, and resizes the terminal and PTY accordingly.
    /// </summary>
    public void ApplyFont(Font font)
    {
        ArgumentNullException.ThrowIfNull(font);

        Font oldFont = _font;
        _font = new Font(font, font.Style);
        oldFont.Dispose();

        ClearFontCache();
        UpdateCellMetrics();
        UpdateTerminalSize();
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        EnsureNativeHandlesCreated();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateTerminalSize();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;

            Stop();

            DisposeInputResources();

            if (_rowCells != IntPtr.Zero || _rowIterator != IntPtr.Zero || _renderState != IntPtr.Zero || _terminal != IntPtr.Zero)
            {
                GhosttyNativeLibrary library = GhosttySession.Library;
                if (_rowCells != IntPtr.Zero)
                {
                    library.RowCellsFree(_rowCells);
                    _rowCells = IntPtr.Zero;
                }

                if (_rowIterator != IntPtr.Zero)
                {
                    library.RowIteratorFree(_rowIterator);
                    _rowIterator = IntPtr.Zero;
                }

                if (_renderState != IntPtr.Zero)
                {
                    library.RenderStateFree(_renderState);
                    _renderState = IntPtr.Zero;
                }

                if (_terminal != IntPtr.Zero)
                {
                    library.TerminalFree(_terminal);
                    _terminal = IntPtr.Zero;
                }
            }

            DisposeFontCache();
            _font.Dispose();
            _readerCts?.Dispose();
        }

        base.Dispose(disposing);
    }

    private static void Check(int result, string operation)
    {
        if (result != Success)
        {
            throw new InvalidOperationException($"{operation} failed with result {result}.");
        }
    }

    private static string ResolveApplicationPath(string app)
    {
        if (Path.IsPathRooted(app) && File.Exists(app))
        {
            return app;
        }

        string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string directory in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory, app);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (!app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                candidate = Path.Combine(directory, $"{app}.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return app;
    }

    private void EnsureNativeHandlesCreated()
    {
        if (_terminal != IntPtr.Zero || !GhosttySession.IsSupported)
        {
            return;
        }

        GhosttyNativeLibrary library = GhosttySession.Library;
        TerminalOptions options = new()
        {
            Columns = (ushort)_columns,
            Rows = (ushort)_rows,
            MaxScrollback = (UIntPtr)MaxScrollbackLines
        };

        Check(library.TerminalNew(options, out _terminal), "TerminalNew");
        Check(library.RenderStateNew(out _renderState), "RenderStateNew");
        Check(library.RowIteratorNew(out _rowIterator), "RowIteratorNew");
        Check(library.RowCellsNew(out _rowCells), "RowCellsNew");

        IntPtr callbackPointer = GhosttyNativeLibrary.GetCallbackPointer(_writePtyCallback);
        Check(library.TerminalSet(_terminal, TerminalOptionWritePty, callbackPointer), "TerminalSet(TerminalOptionWritePty)");
        Check(library.TerminalResize(_terminal, (ushort)_columns, (ushort)_rows, (uint)_cellWidth, (uint)_cellHeight), "TerminalResize");

        if (_currentScheme is not null)
        {
            ApplyColorSchemeCore(_currentScheme);
        }

        EnsureInputHandlesCreated();
    }

    private void UpdateCellMetrics()
    {
        Size size = TextRenderer.MeasureText("W", _font, Size.Empty, TextFormatFlags.NoPadding);
        _cellWidth = Math.Max(1, size.Width);
        _cellHeight = Math.Max(1, size.Height);
    }

    private void UpdateTerminalSize()
    {
        if (_cellWidth <= 0 || _cellHeight <= 0)
        {
            return;
        }

        int availableWidth = Math.Max(1, ClientRectangle.Width - (_scrollBar.Visible ? _scrollBar.Width : 0));
        int availableHeight = Math.Max(1, ClientRectangle.Height);
        int newColumns = Math.Max(1, availableWidth / _cellWidth);
        int newRows = Math.Max(1, availableHeight / _cellHeight);

        if (newColumns == _columns && newRows == _rows)
        {
            return;
        }

        _columns = newColumns;
        _rows = newRows;

        if (_terminal != IntPtr.Zero)
        {
            GhosttySession.Library.TerminalResize(_terminal, (ushort)_columns, (ushort)_rows, (uint)_cellWidth, (uint)_cellHeight);
            _connection?.Resize(_columns, _rows);
            UpdateScrollbar();
            Invalidate();
        }
    }

    private void ApplyColorSchemeCore(GhosttyColorScheme scheme)
    {
        NativeColor[] palette = new NativeColor[RenderStatePaletteLength];
        for (int i = 0; i < GhosttyColorScheme.PaletteSize; i++)
        {
            Color c = scheme.Palette[i];
            palette[i] = new NativeColor { Red = c.R, Green = c.G, Blue = c.B };
        }

        int paletteIndex = 16;
        for (int r = 0; r < 6; r++)
        {
            byte red = (byte)(r == 0 ? 0 : 55 + (r * 40));
            for (int g = 0; g < 6; g++)
            {
                byte green = (byte)(g == 0 ? 0 : 55 + (g * 40));
                for (int b = 0; b < 6; b++)
                {
                    byte blue = (byte)(b == 0 ? 0 : 55 + (b * 40));
                    palette[paletteIndex++] = new NativeColor { Red = red, Green = green, Blue = blue };
                }
            }
        }

        for (int i = 0; i < 24; i++)
        {
            byte gray = (byte)(8 + (i * 10));
            palette[paletteIndex++] = new NativeColor { Red = gray, Green = gray, Blue = gray };
        }

        GhosttyNativeExtras extras = GhosttySession.Extras;
        extras.TerminalSetForeground(_terminal, ToNativeColor(scheme.Foreground));
        extras.TerminalSetBackground(_terminal, ToNativeColor(scheme.Background));
        extras.TerminalSetCursorColor(_terminal, ToNativeColor(scheme.Cursor));
        extras.TerminalSetPalette(_terminal, palette);
    }

    private void OnTerminalWritePty(IntPtr terminal, IntPtr userdata, IntPtr data, UIntPtr length)
    {
        if (_connection is null || length == UIntPtr.Zero)
        {
            return;
        }

        lock (_writeLock)
        {
            unsafe
            {
                ReadOnlySpan<byte> span = new((void*)data, (int)length);
                _connection.WriterStream.Write(span);
                _connection.WriterStream.Flush();
            }
        }
    }

    private void ReadLoop(IGhosttyPtyConnection activeConnection, CancellationToken ct)
    {
        byte[] buffer = new byte[ReadBufferSize];
        try
        {
            while (!_disposed && !ct.IsCancellationRequested)
            {
                int read = activeConnection.ReaderStream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                if (!ReferenceEquals(activeConnection, _connection))
                {
                    break;
                }

                byte[] chunk = buffer.AsSpan(0, read).ToArray();
                _outputQueue.Enqueue(chunk);

                if (Interlocked.CompareExchange(ref _drainScheduled, 1, 0) == 0)
                {
                    if (!_disposed && IsHandleCreated)
                    {
                        BeginInvoke(DrainOutputQueue);
                    }
                    else
                    {
                        Interlocked.Exchange(ref _drainScheduled, 0);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private void DrainOutputQueue()
    {
        Interlocked.Exchange(ref _drainScheduled, 0);

        if (_disposed || _terminal == IntPtr.Zero)
        {
            return;
        }

        if (_connection is null)
        {
            while (_outputQueue.TryDequeue(out _))
            {
            }

            return;
        }

        bool hasWritten = false;
        while (_outputQueue.TryDequeue(out byte[]? chunk))
        {
            if (chunk.Length == 0)
            {
                continue;
            }

            _lastOutputChunk = chunk;
            WriteToTerminal(chunk);
            hasWritten = true;
            OutputReceived?.Invoke(this, EventArgs.Empty);
        }

        if (hasWritten)
        {
            UpdateScrollbar();
            Invalidate();
        }

        if (!_outputQueue.IsEmpty && Interlocked.CompareExchange(ref _drainScheduled, 1, 0) == 0)
        {
            if (!_disposed && IsHandleCreated)
            {
                BeginInvoke(DrainOutputQueue);
            }
            else
            {
                Interlocked.Exchange(ref _drainScheduled, 0);
            }
        }
    }

    private void WriteToTerminal(byte[] data)
    {
        if (_terminal == IntPtr.Zero || data.Length == 0)
        {
            return;
        }

        unsafe
        {
            fixed (byte* pointer = data)
            {
                GhosttySession.Library.TerminalWrite(_terminal, (IntPtr)pointer, (UIntPtr)data.Length);
            }
        }
    }

    private void OnConnectionProcessExited(object? sender, GhosttyPtyExitedEventArgs e)
    {
        if (_disposed || !IsHandleCreated || !ReferenceEquals(sender, _connection))
        {
            return;
        }

        try
        {
            BeginInvoke(() =>
            {
                if (_disposed || !ReferenceEquals(sender, _connection))
                {
                    return;
                }

                _isProcessRunning = false;
                string message = $"\r\n[process exited with code {e.ExitCode}]\r\n";
                byte[] messageBytes = Encoding.UTF8.GetBytes(message);
                WriteToTerminal(messageBytes);
                UpdateScrollbar();
                Invalidate();
                ProcessExited?.Invoke(this, e.ExitCode);
            });
        }
        catch (InvalidOperationException)
        {
        }
    }
}
