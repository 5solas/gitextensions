// This file contains fork-specific (hj) console tab recreation logic split from FormBrowse
// to avoid upstream merge conflicts.

using System.Globalization;
using GitCommands;

namespace GitUI.CommandsDialogs;

public sealed partial class FormBrowse
{
    private bool _consoleTabSelectingSubscribed;

    internal readonly record struct ConsoleSettingsSnapshot(string EmulatorName, string Theme, string? FontKey, string Shell, bool ShowTab);

    internal static ConsoleSettingsSnapshot CaptureConsoleSettings()
    {
        Font? font = AppSettings.ConEmuConsoleFont;
        string? fontKey = font is null ? null : $"{font.Name}|{font.Size.ToString(CultureInfo.InvariantCulture)}|{font.Style}";
        return new(
            AppSettings.ConsoleEmulatorName.Value ?? string.Empty,
            AppSettings.ConEmuStyle.Value ?? string.Empty,
            fontKey,
            AppSettings.ConEmuTerminal.Value ?? string.Empty,
            AppSettings.ShowConEmuTab.Value);
    }

    private void DisposeConsoleTab()
    {
        if (_consoleTabSelectingSubscribed)
        {
            CommitInfoTabControl.Selecting -= CommitInfoTabControl_Selecting;
            _consoleTabSelectingSubscribed = false;
        }

        if (_terminal is IDisposable disposableRunner)
        {
            disposableRunner.Dispose();
            _terminal = null;
        }
    }

    private void ApplyConsoleSettingsIfChanged(ConsoleSettingsSnapshot oldConsoleSettings)
    {
        if (oldConsoleSettings != CaptureConsoleSettings())
        {
            ResetTerminalTab(oldConsoleSettings.ShowTab);
        }
    }

    private void CommitInfoTabControl_Selecting(object? sender, TabControlCancelEventArgs args)
    {
        if (args.TabPage != _consoleTabPage)
        {
            return;
        }

        EnsureTerminalCreatedAndStarted();
    }

    private void EnsureTerminalCreatedAndStarted()
    {
        if (_consoleTabPage is null)
        {
            return;
        }

        if (_terminal is null)
        {
            _terminal = _consoleEmulatorsRegistry.CreateShellRunner();
            if (_terminal is null)
            {
                return;
            }

            _terminal.Control.Dock = DockStyle.Fill;
            _consoleTabPage.Controls.Add(_terminal.Control);
        }

        if (_terminal.IsShellRunning)
        {
            _terminal.FocusTerminal();
            return;
        }

        _terminal.StartShell(Module.WorkingDir);
    }

    private void ResetTerminalTab(bool oldShowTab)
    {
        if (_terminal is not null)
        {
            _consoleTabPage?.Controls.Remove(_terminal.Control);
            if (_terminal is IDisposable disposableRunner)
            {
                disposableRunner.Dispose();
            }
            else
            {
                _terminal.Control.Dispose();
            }

            _terminal = null;
        }

        if (!AppSettings.ShowConEmuTab)
        {
            if (_consoleTabPage is not null)
            {
                CommitInfoTabControl.RemoveIfExists(_consoleTabPage);
                _consoleTabPage.Dispose();
                _consoleTabPage = null;
            }

            return;
        }

        if (_consoleTabPage is null)
        {
            FillTerminalTab();
        }

        if (_consoleTabPage is not null && CommitInfoTabControl.SelectedTab == _consoleTabPage)
        {
            EnsureTerminalCreatedAndStarted();
        }
    }
}
