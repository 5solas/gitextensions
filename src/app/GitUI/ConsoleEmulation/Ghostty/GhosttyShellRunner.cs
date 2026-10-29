using System.Collections;
using System.Drawing;
using System.IO;
using GitCommands;
using GitUI.Shells;

namespace GitUI.ConsoleEmulation.Ghostty;

/// <summary>
///  Wraps <see cref="GhosttyTerminalControl"/> for the repository browser's terminal tab.
/// </summary>
internal sealed class GhosttyShellRunner : IConsoleShellRunner, IDisposable
{
    private const string BashClearLinePrefix = "\u0001\u000b";
    private const string NonBashClearLinePrefix = "\u001b";
    private const string CarriageReturnSuffix = "\r";

    private readonly IShellProvider _shellProvider;
    private readonly GhosttyTerminalControl _control;

    public GhosttyShellRunner(IShellProvider shellProvider, GhosttyColorScheme colorScheme, Font font)
    {
        _shellProvider = shellProvider;
        _control = new GhosttyTerminalControl
        {
            Dock = DockStyle.Fill
        };
        _control.ApplyColorScheme(colorScheme);
        _control.ApplyFont(font);
    }

    /// <inheritdoc/>
    public Control Control => _control;

    /// <inheritdoc/>
    public bool IsShellRunning => _control.IsProcessRunning;

    /// <inheritdoc/>
    public void FocusTerminal()
    {
        _control.Focus();
    }

    /// <inheritdoc/>
    public void StartShell(string workDir)
    {
        if (IsShellRunning)
        {
            FocusTerminal();
            return;
        }

        string? shellType = AppSettings.ConEmuTerminal;
        string commandLine = _shellProvider.GetShellCommandLine(shellType);
        (string application, IReadOnlyList<string> arguments) = SplitCommandLine(commandLine);

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

        _control.Start(application, arguments, workDir, environment);
    }

    /// <inheritdoc/>
    public void ChangeWorkingDirectory(string path)
    {
        if (!IsShellRunning || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string? shellType = AppSettings.ConEmuTerminal;
        IShellDescriptor shell = _shellProvider.GetShell(shellType);
        string command = shell.GetChangeDirCommand(path);
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        string prefix = string.Equals(shell.Name, BashShell.ShellName, StringComparison.OrdinalIgnoreCase)
            ? BashClearLinePrefix
            : NonBashClearLinePrefix;

        _control.SendText($"{prefix}{command}{CarriageReturnSuffix}");
    }

    /// <summary>
    ///  Splits a command line into the executable application path and arguments list.
    /// </summary>
    /// <param name="commandLine">The command line to split.</param>
    /// <returns>A tuple containing the application path and the argument list.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="commandLine"/> is null, empty, or whitespace.</exception>
    internal static (string App, IReadOnlyList<string> Arguments) SplitCommandLine(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            throw new ArgumentException("Command line cannot be empty or whitespace.", nameof(commandLine));
        }

        string trimmedCommandLine = commandLine.Trim();
        string application;
        string remainder;

        if (trimmedCommandLine.StartsWith('"'))
        {
            int quoteEndIndex = trimmedCommandLine.IndexOf('"', 1);
            if (quoteEndIndex < 0)
            {
                application = trimmedCommandLine[1..];
                remainder = string.Empty;
            }
            else
            {
                application = trimmedCommandLine[1..quoteEndIndex];
                remainder = quoteEndIndex + 1 < trimmedCommandLine.Length
                    ? trimmedCommandLine[(quoteEndIndex + 1)..]
                    : string.Empty;
            }
        }
        else
        {
            int firstSpaceIndex = -1;
            for (int index = 0; index < trimmedCommandLine.Length; index++)
            {
                if (char.IsWhiteSpace(trimmedCommandLine[index]))
                {
                    firstSpaceIndex = index;
                    break;
                }
            }

            if (firstSpaceIndex < 0)
            {
                application = trimmedCommandLine;
                remainder = string.Empty;
            }
            else
            {
                application = trimmedCommandLine[..firstSpaceIndex];
                remainder = trimmedCommandLine[(firstSpaceIndex + 1)..];
            }
        }

        List<string> arguments = [];
        int argumentIndex = 0;

        while (argumentIndex < remainder.Length)
        {
            while (argumentIndex < remainder.Length && char.IsWhiteSpace(remainder[argumentIndex]))
            {
                argumentIndex++;
            }

            if (argumentIndex >= remainder.Length)
            {
                break;
            }

            if (remainder[argumentIndex] == '"')
            {
                int nextQuoteIndex = remainder.IndexOf('"', argumentIndex + 1);
                if (nextQuoteIndex < 0)
                {
                    arguments.Add(remainder[(argumentIndex + 1)..]);
                    break;
                }

                arguments.Add(remainder[(argumentIndex + 1)..nextQuoteIndex]);
                argumentIndex = nextQuoteIndex + 1;
            }
            else
            {
                int nextSpaceIndex = argumentIndex;
                while (nextSpaceIndex < remainder.Length && !char.IsWhiteSpace(remainder[nextSpaceIndex]))
                {
                    nextSpaceIndex++;
                }

                arguments.Add(remainder[argumentIndex..nextSpaceIndex]);
                argumentIndex = nextSpaceIndex;
            }
        }

        return (application, arguments);
    }

    public void Dispose()
    {
        _control.Dispose();
    }
}
