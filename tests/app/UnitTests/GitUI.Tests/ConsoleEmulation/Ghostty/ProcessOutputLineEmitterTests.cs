using System.Text;
using GitUI.ConsoleEmulation.Ghostty;

namespace GitUITests.ConsoleEmulation.Ghostty;

[TestFixture]
public sealed class ProcessOutputLineEmitterTests
{
    private static List<string> Run(params byte[][] chunks)
    {
        ProcessOutputLineEmitter emitter = new();
        List<string> lines = [];
        foreach (byte[] chunk in chunks)
        {
            emitter.Append(chunk, lines.Add);
        }

        return lines;
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static List<string> Run(params string[] chunks) => Run(chunks.Select(Bytes).ToArray());

    [Test]
    public void Append_splits_crlf_and_lf_with_trailing_lf()
    {
        Run("a\r\nb\n").Should().Equal("a\n", "b\n");
    }

    [Test]
    public void Append_preserves_lone_cr_and_crlf_endings()
    {
        Run("45%\r46%\r\n").Should().Equal("45%\r", "46%\n");
    }

    [Test]
    public void Append_handles_crlf_split_across_chunks_and_emits_partial_line_on_flush()
    {
        ProcessOutputLineEmitter emitter = new();
        List<string> lines = [];

        emitter.Append(Bytes("a\r"), lines.Add);
        lines.Should().BeEmpty();

        emitter.Append(Bytes("\nb"), lines.Add);
        lines.Should().Equal("a\n");

        emitter.Flush(lines.Add);
        lines.Should().Equal("a\n", "b");
    }

    [Test]
    public void Append_handles_lone_cr_split_across_chunks()
    {
        ProcessOutputLineEmitter emitter = new();
        List<string> lines = [];

        emitter.Append(Bytes("45%\r"), lines.Add);
        lines.Should().BeEmpty();

        emitter.Append(Bytes("46%\r\n"), lines.Add);
        lines.Should().Equal("45%\r", "46%\n");
    }

    [Test]
    public void Append_strips_ansi_color_sequences()
    {
        Run("\u001b[32mok\u001b[0m\r\n").Should().Equal("ok\n");
    }

    [Test]
    public void Append_decodes_multibyte_split_across_chunks()
    {
        byte[] bytes = Bytes("한글\n");
        Run(bytes[..4], bytes[4..]).Should().Equal("한글\n");
    }

    [Test]
    public void Flush_emits_partial_line_without_line_ending_for_prompts()
    {
        ProcessOutputLineEmitter emitter = new();
        List<string> lines = [];

        emitter.Append(Bytes("Password:"), lines.Add);
        lines.Should().BeEmpty();

        emitter.Flush(lines.Add);
        lines.Should().Equal("Password:");
    }

    [Test]
    public void Flush_emits_pending_lone_cr()
    {
        ProcessOutputLineEmitter emitter = new();
        List<string> lines = [];

        emitter.Append(Bytes("100%\r"), lines.Add);
        lines.Should().BeEmpty();

        emitter.Flush(lines.Add);
        lines.Should().Equal("100%\r");
    }

    [Test]
    public void Flush_does_not_emit_when_buffer_is_empty()
    {
        ProcessOutputLineEmitter emitter = new();
        List<string> lines = [];

        emitter.Append(Bytes("line\n"), lines.Add);
        lines.Should().Equal("line\n");

        emitter.Flush(lines.Add);
        lines.Should().Equal("line\n");
    }

    [Test]
    public void Reset_clears_buffer_without_emitting()
    {
        ProcessOutputLineEmitter emitter = new();
        List<string> lines = [];

        emitter.Append(Bytes("pending data"), lines.Add);
        emitter.Reset();
        emitter.Flush(lines.Add);

        lines.Should().BeEmpty();
    }

    [Test]
    public void Append_strips_control_characters_but_preserves_tab()
    {
        Run("a\u0007b\bc\td\n").Should().Equal("abc\td\n");
    }

    [Test]
    public void Append_strips_csi_erase_in_line()
    {
        Run("clean\u001b[K\n").Should().Equal("clean\n");
    }
}
