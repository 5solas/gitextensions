using System.Collections;
using System.Drawing;
using System.IO;
using System.Text;
using GitCommands;
using GitCommands.Logging;
using GitExtUtils;

namespace GitUI.ConsoleEmulation.Ghostty;

/// <summary>
///  Executes console commands and displays their output inside an embedded Ghostty terminal control.
/// </summary>
internal sealed class GhosttyCommandRunner : IConsoleCommandRunner, IDisposable
{
    private static readonly byte[] DoneMessageBytes = "\r\nDone\r\n"u8.ToArray();

    private readonly GhosttyTerminalControl _control;
    private readonly ProcessOutputLineEmitter _emitter = new();

    private ProcessOperation? _currentOperation;
    private bool _hasCommandExecuted;
    private bool _disposed;

    /// <summary>
    ///  Initializes a new instance of the <see cref="GhosttyCommandRunner"/> class.
    /// </summary>
    public GhosttyCommandRunner(GhosttyColorScheme scheme, Font font)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        ArgumentNullException.ThrowIfNull(font);

        _control = new GhosttyTerminalControl
        {
            Dock = DockStyle.Fill
        };
        _control.ShowProcessExitMessage = false;
        _control.ApplyColorScheme(scheme);
        _control.ApplyFont(font);

        _control.OutputReceived += OnOutputReceived;
        _control.ProcessExited += OnProcessExited;
        _control.KeyPressedAfterExit += OnKeyPressedAfterExit;
    }

    /// <inheritdoc/>
    public Control Control => _control;

    /// <inheritdoc/>
    public event EventHandler<ConsoleOutputEventArgs>? CommandOutputReceived;

    /// <inheritdoc/>
    public event EventHandler<ConsoleProcessExitEventArgs>? CommandProcessExited;

    /// <inheritdoc/>
    public event EventHandler? ConsoleHostTerminated;

    /// <inheritdoc/>
    public void StartCommand(string command, string arguments, string workDir, Dictionary<string, string> envVariables)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        ProcessOperation operation = CommandLog.LogProcessStart(command, arguments, workDir);

        if (_control.IsProcessRunning)
        {
            KillCommandProcess();
        }

        _hasCommandExecuted = true;
        _currentOperation = operation;

        string echoCommandLine = string.IsNullOrWhiteSpace(arguments)
            ? $"{command.Quote()}\r\n"
            : $"{command.Quote()} {arguments}\r\n";
        byte[] echoBytes = Encoding.UTF8.GetBytes(echoCommandLine);

        string fullCommandLine = string.IsNullOrWhiteSpace(arguments)
            ? command.Quote()
            : $"{command.Quote()} {arguments}";
        (_, IReadOnlyList<string> args) = GhosttyShellRunner.SplitCommandLine(fullCommandLine);

        Dictionary<string, string> environment = new(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                environment[key] = value;
            }
        }

        if (!string.IsNullOrEmpty(AppSettings.GitCommandValue))
        {
            string? gitDirectory = Path.GetDirectoryName(AppSettings.GitCommandValue);
            if (!string.IsNullOrEmpty(gitDirectory))
            {
                if (environment.TryGetValue("PATH", out string? existingPath) && !string.IsNullOrEmpty(existingPath))
                {
                    environment["PATH"] = $"{gitDirectory};{existingPath}";
                }
                else
                {
                    environment["PATH"] = gitDirectory;
                }
            }
        }

        if (envVariables is not null)
        {
            foreach ((string key, string value) in envVariables)
            {
                environment[key] = value;
            }
        }

        try
        {
            _control.Start(command, args, workDir, environment);
            _control.WriteToTerminalForDisplay(echoBytes);
        }
        catch (Exception ex)
        {
            _currentOperation = null;
            operation.LogProcessEnd(ex);
            throw;
        }
    }

    /// <inheritdoc/>
    public void WriteCommandProcessInput(string text)
    {
        _control.SendText(text);
    }

    /// <inheritdoc/>
    public void KillCommandProcess()
    {
        if (!_control.IsProcessRunning)
        {
            return;
        }

        _control.Stop();

        _emitter.Flush(text => CommandOutputReceived?.Invoke(this, new ConsoleOutputEventArgs(text)));

        ProcessOperation? operation = Interlocked.Exchange(ref _currentOperation, null);
        operation?.LogProcessEnd(-1);

        CommandProcessExited?.Invoke(this, new ConsoleProcessExitEventArgs(-1));
    }

    /// <inheritdoc/>
    public void ResetConsole()
    {
        KillCommandProcess();
        _control.ResetScreen();
        _emitter.Reset();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _control.OutputReceived -= OnOutputReceived;
        _control.ProcessExited -= OnProcessExited;
        _control.KeyPressedAfterExit -= OnKeyPressedAfterExit;

        _control.Dispose();
    }

    private void OnOutputReceived(object? sender, EventArgs e)
    {
        ReadOnlyMemory<byte> chunk = _control.LastOutputChunk;
        if (!chunk.IsEmpty)
        {
            _emitter.Append(chunk.Span, text => CommandOutputReceived?.Invoke(this, new ConsoleOutputEventArgs(text)));
        }
    }

    private void OnProcessExited(object? sender, int exitCode)
    {
        _emitter.Flush(text => CommandOutputReceived?.Invoke(this, new ConsoleOutputEventArgs(text)));

        ProcessOperation? operation = Interlocked.Exchange(ref _currentOperation, null);
        operation?.LogProcessEnd(exitCode);

        _control.WriteToTerminalForDisplay(DoneMessageBytes);

        CommandProcessExited?.Invoke(this, new ConsoleProcessExitEventArgs(exitCode));
    }

    private void OnKeyPressedAfterExit(object? sender, EventArgs e)
    {
        if (_hasCommandExecuted)
        {
            ConsoleHostTerminated?.Invoke(this, EventArgs.Empty);
        }
    }
}
