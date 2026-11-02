using System.Text;
using GitUI.ConsoleEmulation.Ghostty;

namespace GitUITests.ConsoleEmulation.Ghostty;

[TestFixture]
public sealed class AnsiLineSplitterTests
{
    private static List<string> Run(params byte[][] chunks)
    {
        AnsiLineSplitter splitter = new();
        List<string> lines = [];
        foreach (byte[] chunk in chunks)
        {
            splitter.Append(chunk, lines.Add);
        }

        return lines;
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static List<string> Run(params string[] chunks) => Run(chunks.Select(Bytes).ToArray());

    [Test]
    public void Append_splits_crlf_and_lf()
    {
        Run("a\r\nb\n").Should().Equal("a", "b");
    }

    [Test]
    public void Append_does_not_duplicate_line_when_crlf_split_across_chunks()
    {
        Run("a\r", "\nb").Should().Equal("a");
    }

    [Test]
    public void Append_emits_remaining_line_after_crlf_split_across_chunks_on_flush()
    {
        AnsiLineSplitter splitter = new();
        List<string> lines = [];
        splitter.Append(Bytes("a\r"), lines.Add);
        splitter.Append(Bytes("\nb"), lines.Add);
        splitter.Flush(lines.Add);
        lines.Should().Equal("a", "b");
    }

    [Test]
    public void Append_treats_lone_cr_as_line_end()
    {
        Run("45%\r46%\r").Should().Equal("45%", "46%");
    }

    [Test]
    public void Append_strips_csi()
    {
        Run("\u001b[31mred\u001b[0m\n").Should().Equal("red");
    }

    [Test]
    public void Append_strips_csi_split_across_chunks()
    {
        Run("\u001b[", "32mok\n").Should().Equal("ok");
    }

    [Test]
    public void Append_strips_osc_terminated_by_bel()
    {
        Run("\u001b]0;title\u0007x\n").Should().Equal("x");
    }

    [Test]
    public void Append_strips_osc_terminated_by_st()
    {
        Run("\u001b]0;t\u001b\\x\n").Should().Equal("x");
    }

    [Test]
    public void Append_strips_dcs_terminated_by_st()
    {
        Run("\u001bPdata\u001b\\x\n").Should().Equal("x");
    }

    [Test]
    public void Append_strips_charset_designation_and_single_char_escapes()
    {
        Run("\u001b(Bx\u001b7y\u001b=z\n").Should().Equal("xyz");
    }

    [Test]
    public void Append_strips_bel_and_backspace_but_keeps_tab()
    {
        Run("a\u0007b\bc\td\n").Should().Equal("abc\td");
    }

    [Test]
    public void Append_decodes_multibyte_split_across_chunks()
    {
        byte[] bytes = Bytes("한글\n");
        Run(bytes[..4], bytes[4..]).Should().Equal("한글");
    }

    [Test]
    public void Flush_emits_partial_line_once()
    {
        AnsiLineSplitter splitter = new();
        List<string> lines = [];
        splitter.Append(Bytes("tail"), lines.Add);
        splitter.Flush(lines.Add);
        splitter.Flush(lines.Add);
        lines.Should().Equal("tail");
    }

    [Test]
    public void Flush_does_not_emit_when_nothing_remains()
    {
        AnsiLineSplitter splitter = new();
        List<string> lines = [];
        splitter.Append(Bytes("a\n"), lines.Add);
        splitter.Flush(lines.Add);
        lines.Should().Equal("a");
    }

    [Test]
    public void Append_reports_empty_lines()
    {
        Run("\n\n").Should().Equal("", "");
    }
}
