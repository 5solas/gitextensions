using System.Buffers;
using System.Text;

namespace GitUI.ConsoleEmulation.Ghostty;

/// <summary>
///  Converts a PTY byte stream into lines of text with ANSI/VT escape sequences removed.
///  Lines are terminated by CRLF, LF or a lone CR (so that git progress output such as
///  <c>Receiving objects: 45%\r</c> yields one line per update).
///  State (UTF-8 decoder, escape sequence parser, pending CR) is kept between calls.
///  This type is not thread-safe.
/// </summary>
internal sealed class AnsiLineSplitter
{
    private const char Escape = '\u001b';
    private const char Bell = '\u0007';
    private const char CarriageReturn = '\r';
    private const char LineFeed = '\n';
    private const char Tab = '\t';

    private enum ParserState
    {
        Ground,
        Escape,
        EscapeIntermediate,
        Csi,
        Osc,
        OscEscape,
        StringSequence,
        StringSequenceEscape,
    }

    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _line = new();
    private ParserState _state = ParserState.Ground;
    private bool _skipLineFeed;

    /// <summary>
    ///  Decodes <paramref name="data"/> and invokes <paramref name="onLine"/> for each completed line.
    /// </summary>
    public void Append(ReadOnlySpan<byte> data, Action<string> onLine)
    {
        ArgumentNullException.ThrowIfNull(onLine);

        if (data.IsEmpty)
        {
            return;
        }

        char[] buffer = ArrayPool<char>.Shared.Rent(Encoding.UTF8.GetMaxCharCount(data.Length));
        try
        {
            int charCount = _decoder.GetChars(data, buffer, flush: false);
            for (int index = 0; index < charCount; index++)
            {
                Process(buffer[index], onLine);
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    /// <summary>
    ///  Emits the remaining partial line (if not empty) and resets all state. Call when the process exits.
    /// </summary>
    public void Flush(Action<string> onLine)
    {
        ArgumentNullException.ThrowIfNull(onLine);

        if (_line.Length > 0)
        {
            string text = _line.ToString();
            _line.Clear();
            onLine(text);
        }

        _decoder.Reset();
        _state = ParserState.Ground;
        _skipLineFeed = false;
    }

    private void Process(char value, Action<string> onLine)
    {
        switch (_state)
        {
            case ParserState.Ground:
                ProcessGround(value, onLine);
                break;

            case ParserState.Escape:
                ProcessEscape(value);
                break;

            case ParserState.EscapeIntermediate:
                if (value is >= '\u0030' and <= '\u007e')
                {
                    _state = ParserState.Ground;
                }
                else if (value == Escape)
                {
                    _state = ParserState.Escape;
                }

                break;

            case ParserState.Csi:
                if (value is >= '\u0040' and <= '\u007e')
                {
                    _state = ParserState.Ground;
                }
                else if (value == Escape)
                {
                    _state = ParserState.Escape;
                }

                break;

            case ParserState.Osc:
                if (value == Bell)
                {
                    _state = ParserState.Ground;
                }
                else if (value == Escape)
                {
                    _state = ParserState.OscEscape;
                }

                break;

            case ParserState.StringSequence:
                if (value == Escape)
                {
                    _state = ParserState.StringSequenceEscape;
                }

                break;

            case ParserState.OscEscape:
            case ParserState.StringSequenceEscape:
                if (value == '\\')
                {
                    _state = ParserState.Ground;
                }
                else
                {
                    _state = ParserState.Escape;
                    ProcessEscape(value);
                }

                break;
        }
    }

    private void ProcessGround(char value, Action<string> onLine)
    {
        switch (value)
        {
            case CarriageReturn:
                EmitLine(onLine);
                _skipLineFeed = true;
                return;

            case LineFeed:
                if (_skipLineFeed)
                {
                    _skipLineFeed = false;
                }
                else
                {
                    EmitLine(onLine);
                }

                return;

            case Escape:
                _state = ParserState.Escape;
                return;
        }

        _skipLineFeed = false;
        if (value == Tab || (value >= ' ' && value != '\u007f'))
        {
            _line.Append(value);
        }
    }

    private void ProcessEscape(char value)
    {
        switch (value)
        {
            case '[':
                _state = ParserState.Csi;
                break;

            case ']':
                _state = ParserState.Osc;
                break;

            case 'P':
            case 'X':
            case '^':
            case '_':
                _state = ParserState.StringSequence;
                break;

            case Escape:
                break;

            case >= ' ' and <= '/':
                _state = ParserState.EscapeIntermediate;
                break;

            default:
                _state = ParserState.Ground;
                break;
        }
    }

    private void EmitLine(Action<string> onLine)
    {
        string text = _line.ToString();
        _line.Clear();
        onLine(text);
    }
}
