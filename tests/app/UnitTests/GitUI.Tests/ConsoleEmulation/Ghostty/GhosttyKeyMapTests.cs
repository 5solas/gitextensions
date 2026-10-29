using System.Windows.Forms;
using GitUI.ConsoleEmulation.Ghostty;

namespace GitUITests.ConsoleEmulation.Ghostty;

[TestFixture]
public sealed class GhosttyKeyMapTests
{
    [TestCase(Keys.Enter, GhosttyKey.Enter, 0)]
    [TestCase(Keys.C | Keys.Control, 22, GhosttyMods.Ctrl)]
    [TestCase(Keys.Tab | Keys.Shift, GhosttyKey.Tab, GhosttyMods.Shift)]
    [TestCase(Keys.Up, GhosttyKey.ArrowUp, 0)]
    [TestCase(Keys.Down, GhosttyKey.ArrowDown, 0)]
    [TestCase(Keys.Left, GhosttyKey.ArrowLeft, 0)]
    [TestCase(Keys.Right, GhosttyKey.ArrowRight, 0)]
    [TestCase(Keys.F5, GhosttyKey.F1 + 4, 0)]
    [TestCase(Keys.F24, GhosttyKey.F24, 0)]
    [TestCase(Keys.NumPad7, GhosttyKey.Numpad7, 0)]
    [TestCase(Keys.OemMinus, GhosttyKey.Minus, 0)]
    [TestCase(Keys.Oem102, GhosttyKey.IntlBackslash, 0)]
    [TestCase(Keys.Back, GhosttyKey.Backspace, 0)]
    [TestCase(Keys.Escape, GhosttyKey.Escape, 0)]
    [TestCase(Keys.Space, GhosttyKey.Space, 0)]
    [TestCase(Keys.Scroll, GhosttyKey.ScrollLock, 0)]
    [TestCase(Keys.LWin, GhosttyKey.MetaLeft, 0)]
    [TestCase(Keys.Pause, GhosttyKey.Pause, 0)]
    [TestCase(Keys.PrintScreen, GhosttyKey.PrintScreen, 0)]
    [TestCase(Keys.BrowserBack, GhosttyKey.BrowserBack, 0)]
    [TestCase(Keys.VolumeMute, GhosttyKey.AudioVolumeMute, 0)]
    [TestCase(Keys.MediaPlayPause, GhosttyKey.MediaPlayPause, 0)]
    [TestCase(Keys.Sleep, GhosttyKey.Sleep, 0)]
    [TestCase(Keys.A | Keys.Control | Keys.Shift | Keys.Alt, GhosttyKey.A, GhosttyMods.Ctrl | GhosttyMods.Shift | GhosttyMods.Alt)]
    public void TryMap_should_map_key_and_modifiers(Keys keyData, int expectedKey, int expectedMods)
    {
        GhosttyKeyMap.TryMap(keyData, out int key, out ushort mods).Should().BeTrue();
        key.Should().Be(expectedKey);
        mods.Should().Be((ushort)expectedMods);
    }

    [TestCase(Keys.ProcessKey)]
    [TestCase(Keys.HanguelMode)]
    [TestCase(Keys.HanjaMode)]
    [TestCase(Keys.IMEConvert)]
    [TestCase(Keys.Packet)]
    [TestCase(Keys.None)]
    public void TryMap_should_return_false_for_unmapped_keys(Keys keyData)
    {
        GhosttyKeyMap.TryMap(keyData, out _, out _).Should().BeFalse();
    }

    [TestCase(Keys.A, true)]
    [TestCase(Keys.A | Keys.Shift, true)]
    [TestCase(Keys.A | Keys.Control, false)]
    [TestCase(Keys.A | Keys.Alt, false)]
    [TestCase(Keys.Space, true)]
    [TestCase(Keys.D1, true)]
    [TestCase(Keys.OemMinus, true)]
    [TestCase(Keys.Enter, false)]
    [TestCase(Keys.Tab, false)]
    [TestCase(Keys.Back, false)]
    [TestCase(Keys.Escape, false)]
    [TestCase(Keys.Up, false)]
    [TestCase(Keys.F1, false)]
    public void IsTextKey_should_detect_keys_producing_text(Keys keyData, bool expected)
    {
        GhosttyKeyMap.IsTextKey(keyData).Should().Be(expected);
    }

    [TestCase(Keys.A, 'a')]
    [TestCase(Keys.Z, 'z')]
    [TestCase(Keys.D1, '1')]
    [TestCase(Keys.OemMinus, '-')]
    [TestCase(Keys.Oemtilde, '`')]
    [TestCase(Keys.F1, 0)]
    [TestCase(Keys.Enter, 0)]
    public void UnshiftedCodepoint_should_return_us_layout_character(Keys keyCode, int expected)
    {
        GhosttyKeyMap.UnshiftedCodepoint(keyCode).Should().Be((uint)expected);
    }

    [Test]
    public void GhosttyKey_constants_should_match_measured_values()
    {
        GhosttyKey.Backquote.Should().Be(1);
        GhosttyKey.A.Should().Be(20);
        GhosttyKey.Z.Should().Be(45);
        GhosttyKey.Enter.Should().Be(58);
        GhosttyKey.Tab.Should().Be(64);
        GhosttyKey.Delete.Should().Be(68);
        GhosttyKey.ArrowUp.Should().Be(78);
        GhosttyKey.NumpadEnter.Should().Be(97);
        GhosttyKey.Escape.Should().Be(120);
        GhosttyKey.F1.Should().Be(121);
        GhosttyKey.F24.Should().Be(144);
        GhosttyKey.F25.Should().Be(145);
        GhosttyKey.ScrollLock.Should().Be(149);
        GhosttyKey.Pause.Should().Be(150);
        GhosttyKey.Paste.Should().Be(175);
    }
}
