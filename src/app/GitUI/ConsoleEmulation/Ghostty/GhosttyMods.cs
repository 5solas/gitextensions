namespace GitUI.ConsoleEmulation.Ghostty;

/// <summary>
///  Bit flags of the libghostty-vt <c>GhosttyMods</c> bitmask and <c>GhosttyKeyAction</c> values.
/// </summary>
internal static class GhosttyMods
{
    public const ushort Shift = 1 << 0;
    public const ushort Ctrl = 1 << 1;
    public const ushort Alt = 1 << 2;
    public const ushort Super = 1 << 3;
    public const ushort CapsLock = 1 << 4;
    public const ushort NumLock = 1 << 5;
    public const ushort ShiftSide = 1 << 6;
    public const ushort CtrlSide = 1 << 7;
    public const ushort AltSide = 1 << 8;
    public const ushort SuperSide = 1 << 9;
}

/// <summary>
///  Values of the libghostty-vt <c>GhosttyKeyAction</c> enum.
/// </summary>
internal static class GhosttyKeyAction
{
    public const int Release = 0;
    public const int Press = 1;
    public const int Repeat = 2;
}
