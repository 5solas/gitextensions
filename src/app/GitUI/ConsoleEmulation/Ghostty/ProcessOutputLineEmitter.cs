using System.Buffers;
using System.Text;

namespace GitUI.ConsoleEmulation.Ghostty;

/// <summary>
///  Converts a PTY byte stream into lines of text with ANSI/VT escape sequences removed.
///  Line endings (\r\n normalized to \n, lone \n, or lone \r) are preserved for FormProcess consumers.
///  Partial line fragments without line endings are held until flushed.
/// </summary>
internal sealed class ProcessOutputLineEmitter
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
    private bool _pendingCarriageReturn;

    /// <summary>
    ///  Decodes <paramref name="data"/> and invokes <paramref name="onText"/> for each completed line or progress update.
    /// </summary>
    public void Append(ReadOnlySpan<byte> data, Action<string> onText)
    {
        ArgumentNullException.ThrowIfNull(onText);

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
                Process(buffer[index], onText);
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    /// <summary>
    ///  Emits any remaining pending CR or partial line without trailing delimiter, then resets all state.
    /// </summary>
    public void Flush(Action<string> onText)
    {
        ArgumentNullException.ThrowIfNull(onText);

        if (_pendingCarriageReturn)
        {
            _pendingCarriageReturn = false;
            string text = _line.ToString() + CarriageReturn;
            _line.Clear();
            onText(text);
        }
        else if (_line.Length > 0)
        {
            string text = _line.ToString();
            _line.Clear();
            onText(text);
        }

        _decoder.Reset();
        _state = ParserState.Ground;
    }

    /// <summary>
    ///  Clears all buffers and resets parser state without emitting pending text.
    /// </summary>
    public void Reset()
    {
        _line.Clear();
        _pendingCarriageReturn = false;
        _decoder.Reset();
        _state = ParserState.Ground;
    }

    private void Process(char value, Action<string> onText)
    {
        if (_pendingCarriageReturn)
        {
            if (value == LineFeed)
            {
                _pendingCarriageReturn = false;
                EmitLine(LineFeed, onText);
                return;
            }

            _pendingCarriageReturn = false;
            EmitLine(CarriageReturn, onText);
        }

        switch (_state)
        {
            case ParserState.Ground:
                ProcessGround(value, onText);
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

    private void ProcessGround(char value, Action<string> onText)
    {
        switch (value)
        {
            case CarriageReturn:
                _pendingCarriageReturn = true;
                return;

            case LineFeed:
                EmitLine(LineFeed, onText);
                return;

            case Escape:
                _state = ParserState.Escape;
                return;
        }

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

    private void EmitLine(char ending, Action<string> onText)
    {
        string text = ending switch
        {
            LineFeed => _line.ToString() + LineFeed,
            CarriageReturn => _line.ToString() + CarriageReturn,
            _ => _line.ToString()
        };
        _line.Clear();
        onText(text);
    }
}
