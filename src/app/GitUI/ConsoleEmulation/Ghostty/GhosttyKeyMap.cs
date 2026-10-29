using System.Windows.Forms;

namespace GitUI.ConsoleEmulation.Ghostty;

/// <summary>
///  Maps WinForms <see cref="Keys"/> to libghostty-vt key codes and modifier flags.
/// </summary>
internal static class GhosttyKeyMap
{
    private const int KeyNotMapped = 0;

    /// <summary>
    ///  Translates <see cref="KeyEventArgs.KeyData"/> into a ghostty key code and modifier mask.
    /// </summary>
    /// <returns><see langword="false"/> if the key has no ghostty equivalent (IME keys, <see cref="Keys.None"/>, ...).</returns>
    public static bool TryMap(Keys keyData, out int key, out ushort mods)
    {
        mods = MapModifiers(keyData);
        key = MapKeyCode(keyData & Keys.KeyCode);
        return key != KeyNotMapped;
    }

    /// <summary>
    ///  Whether the key produces text through <c>KeyPress</c> (WM_CHAR), so the caller
    ///  should send that character instead of encoding the key press.
    /// </summary>
    public static bool IsTextKey(Keys keyData)
    {
        if ((keyData & (Keys.Control | Keys.Alt)) != Keys.None)
        {
            return false;
        }

        return IsTextKeyCode(keyData & Keys.KeyCode);
    }

    /// <summary>
    ///  Unshifted codepoint of the key on a US layout (Kitty keyboard protocol), or 0 if none.
    /// </summary>
    public static uint UnshiftedCodepoint(Keys keyCode)
    {
        keyCode &= Keys.KeyCode;

        if (keyCode is >= Keys.A and <= Keys.Z)
        {
            return (uint)('a' + (keyCode - Keys.A));
        }

        if (keyCode is >= Keys.D0 and <= Keys.D9)
        {
            return (uint)('0' + (keyCode - Keys.D0));
        }

        if (keyCode is >= Keys.NumPad0 and <= Keys.NumPad9)
        {
            return (uint)('0' + (keyCode - Keys.NumPad0));
        }

        return keyCode switch
        {
            Keys.Space => ' ',
            Keys.OemMinus => '-',
            Keys.Oemplus => '=',
            Keys.OemOpenBrackets => '[',
            Keys.Oem6 => ']',
            Keys.Oem5 => '\\',
            Keys.Oem1 => ';',
            Keys.Oem7 => '\'',
            Keys.Oemcomma => ',',
            Keys.OemPeriod => '.',
            Keys.OemQuestion => '/',
            Keys.Oemtilde => '`',
            Keys.Add => '+',
            Keys.Subtract => '-',
            Keys.Multiply => '*',
            Keys.Divide => '/',
            Keys.Decimal => '.',
            _ => 0,
        };
    }

    private static ushort MapModifiers(Keys keyData)
    {
        ushort mods = 0;
        if ((keyData & Keys.Shift) != Keys.None)
        {
            mods |= GhosttyMods.Shift;
        }

        if ((keyData & Keys.Control) != Keys.None)
        {
            mods |= GhosttyMods.Ctrl;
        }

        if ((keyData & Keys.Alt) != Keys.None)
        {
            mods |= GhosttyMods.Alt;
        }

        return mods;
    }

    private static bool IsTextKeyCode(Keys keyCode)
    {
        if (keyCode is >= Keys.A and <= Keys.Z or >= Keys.D0 and <= Keys.D9)
        {
            return true;
        }

        return keyCode is Keys.Space
            or Keys.OemMinus or Keys.Oemplus or Keys.OemOpenBrackets or Keys.Oem6 or Keys.Oem5
            or Keys.Oem1 or Keys.Oem7 or Keys.Oemcomma or Keys.OemPeriod or Keys.OemQuestion
            or Keys.Oemtilde or Keys.Oem102
            or Keys.Add or Keys.Subtract or Keys.Multiply or Keys.Divide or Keys.Decimal
            or (>= Keys.NumPad0 and <= Keys.NumPad9);
    }

    private static int MapKeyCode(Keys keyCode)
    {
        if (keyCode is >= Keys.A and <= Keys.Z)
        {
            return GhosttyKey.A + (keyCode - Keys.A);
        }

        if (keyCode is >= Keys.D0 and <= Keys.D9)
        {
            return GhosttyKey.Digit0 + (keyCode - Keys.D0);
        }

        if (keyCode is >= Keys.NumPad0 and <= Keys.NumPad9)
        {
            return GhosttyKey.Numpad0 + (keyCode - Keys.NumPad0);
        }

        if (keyCode is >= Keys.F1 and <= Keys.F24)
        {
            return GhosttyKey.F1 + (keyCode - Keys.F1);
        }

        return keyCode switch
        {
            Keys.Enter => GhosttyKey.Enter,
            Keys.Back => GhosttyKey.Backspace,
            Keys.Tab => GhosttyKey.Tab,
            Keys.Escape => GhosttyKey.Escape,
            Keys.Space => GhosttyKey.Space,
            Keys.Up => GhosttyKey.ArrowUp,
            Keys.Down => GhosttyKey.ArrowDown,
            Keys.Left => GhosttyKey.ArrowLeft,
            Keys.Right => GhosttyKey.ArrowRight,
            Keys.Home => GhosttyKey.Home,
            Keys.End => GhosttyKey.End,
            Keys.PageUp => GhosttyKey.PageUp,
            Keys.PageDown => GhosttyKey.PageDown,
            Keys.Insert => GhosttyKey.Insert,
            Keys.Delete => GhosttyKey.Delete,
            Keys.OemMinus => GhosttyKey.Minus,
            Keys.Oemplus => GhosttyKey.Equal,
            Keys.OemOpenBrackets => GhosttyKey.BracketLeft,
            Keys.Oem6 => GhosttyKey.BracketRight,
            Keys.Oem5 => GhosttyKey.Backslash,
            Keys.Oem1 => GhosttyKey.Semicolon,
            Keys.Oem7 => GhosttyKey.Quote,
            Keys.Oemcomma => GhosttyKey.Comma,
            Keys.OemPeriod => GhosttyKey.Period,
            Keys.OemQuestion => GhosttyKey.Slash,
            Keys.Oemtilde => GhosttyKey.Backquote,
            Keys.Oem102 => GhosttyKey.IntlBackslash,
            Keys.Add => GhosttyKey.NumpadAdd,
            Keys.Subtract => GhosttyKey.NumpadSubtract,
            Keys.Multiply => GhosttyKey.NumpadMultiply,
            Keys.Divide => GhosttyKey.NumpadDivide,
            Keys.Decimal => GhosttyKey.NumpadDecimal,
            Keys.NumLock => GhosttyKey.NumLock,
            Keys.CapsLock => GhosttyKey.CapsLock,
            Keys.Scroll => GhosttyKey.ScrollLock,
            Keys.Pause => GhosttyKey.Pause,
            Keys.PrintScreen => GhosttyKey.PrintScreen,
            Keys.Apps => GhosttyKey.ContextMenu,
            Keys.ShiftKey => GhosttyKey.ShiftLeft,
            Keys.ControlKey => GhosttyKey.ControlLeft,
            Keys.Menu => GhosttyKey.AltLeft,
            Keys.LWin => GhosttyKey.MetaLeft,
            Keys.RWin => GhosttyKey.MetaRight,
            Keys.BrowserBack => GhosttyKey.BrowserBack,
            Keys.BrowserForward => GhosttyKey.BrowserForward,
            Keys.BrowserRefresh => GhosttyKey.BrowserRefresh,
            Keys.BrowserStop => GhosttyKey.BrowserStop,
            Keys.BrowserSearch => GhosttyKey.BrowserSearch,
            Keys.BrowserFavorites => GhosttyKey.BrowserFavorites,
            Keys.BrowserHome => GhosttyKey.BrowserHome,
            Keys.VolumeMute => GhosttyKey.AudioVolumeMute,
            Keys.VolumeDown => GhosttyKey.AudioVolumeDown,
            Keys.VolumeUp => GhosttyKey.AudioVolumeUp,
            Keys.MediaNextTrack => GhosttyKey.MediaTrackNext,
            Keys.MediaPreviousTrack => GhosttyKey.MediaTrackPrevious,
            Keys.MediaStop => GhosttyKey.MediaStop,
            Keys.MediaPlayPause => GhosttyKey.MediaPlayPause,
            Keys.LaunchMail => GhosttyKey.LaunchMail,
            Keys.SelectMedia => GhosttyKey.MediaSelect,
            Keys.LaunchApplication1 => GhosttyKey.LaunchApp1,
            Keys.LaunchApplication2 => GhosttyKey.LaunchApp2,
            Keys.Sleep => GhosttyKey.Sleep,
            _ => KeyNotMapped,
        };
    }
}
