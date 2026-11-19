using System.Text;

namespace EchoEnvironment.Hardware;

/// <summary>
/// Splits received text into lines (Phase 3a). This replaces MainWindow's
/// <c>IndexOfNewline</c> + <c>_rxBuffer.Remove(0, newline + 1)</c> pair, which moved the entire
/// remaining buffer on every line and was therefore O(n²) in lines-per-chunk (defect D6). It is
/// cursor-based: the consumed prefix is left in place until it is large enough to be worth moving.
///
/// This is a faithful port of the old framing, not an improvement of it. The old behaviour had two
/// unpinned quirks and both are preserved and pinned by <c>LineFramerTests</c>, because 3a's rule is
/// "do not change framing semantics while writing this":
///
///   - A terminator is a single '\n' OR a single '\r'. So a "\r\n" line ending yields the line and
///     then an empty line, because the '\n' is left behind and terminates an empty one. Callers
///     ignore blank lines, so this is invisible in the app — but it is what the serial path has
///     always produced, and "fixing" it here would be a framing change smuggled into a move.
///   - Overflow discards everything unread. Today that is silent; here <see cref="DroppedBytes"/>
///     makes it observable without changing what happens to any frame.
///
/// DEVIATION FROM THE PLAN (3a's sketch): the sketch has no overflow method, which implies Append
/// drops on overflow. It must not. The old code checked the buffer AFTER draining every complete
/// line, so a chunk holding three complete frames plus garbage still delivered those three frames.
/// Dropping inside Append would discard them. <see cref="TrimOverflow"/> is therefore called by the
/// consumer at exactly the point where MainWindow used to check.
///
/// DEVIATION FROM THE PLAN: <see cref="DroppedBytes"/> is <c>long</c>, not the sketch's <c>int</c>.
/// At 115200 baud a day of garbage is comfortably past 2^31 bytes, and a counter that wraps is
/// worse than no counter at all.
/// </summary>
public sealed class LineFramer
{
    /// <summary>The limit MainWindow hard-coded, kept as the default so nothing changes.</summary>
    public const int DefaultMaxBuffer = 10_000;

    /// <summary>
    /// Consumed characters are only moved out of the buffer once enough of them have piled up.
    /// Below this, the consumed prefix simply stays where it is, which is what makes the common
    /// case allocation-of-nothing rather than a memmove per line.
    /// </summary>
    private const int CompactThreshold = 4096;

    private readonly StringBuilder _buffer = new();
    private int _cursor;    // first character not yet handed to a consumer
    private int _scanFrom;  // first character not yet tested for a terminator
    private long _dropped;

    /// <summary>
    /// How many unread characters are tolerated before <see cref="TrimOverflow"/> discards them.
    /// Zero or negative disables the guard entirely.
    /// </summary>
    public int MaxBuffer { get; set; } = DefaultMaxBuffer;

    /// <summary>Total characters discarded by overflow so far. Observable, where today it is silent.</summary>
    public long DroppedBytes => _dropped;

    /// <summary>Characters received but not yet consumed by a line read.</summary>
    public int BufferedCharacters => _buffer.Length - _cursor;

    /// <summary>Adds a received chunk. Never trims: see the class comment.</summary>
    public void Append(string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
        {
            return;
        }

        _buffer.Append(chunk);
    }

    /// <summary>
    /// Reads the next complete line, without its terminator. Returns false when no terminator has
    /// arrived yet, leaving the partial line in the buffer for the next chunk.
    /// </summary>
    public bool TryReadLine(out string line)
    {
        for (int i = _scanFrom; i < _buffer.Length; i++)
        {
            if (_buffer[i] is '\n' or '\r')
            {
                line = _buffer.ToString(_cursor, i - _cursor);
                _cursor = i + 1;
                _scanFrom = _cursor;
                Compact();
                return true;
            }
        }

        _scanFrom = _buffer.Length;
        line = string.Empty;
        return false;
    }

    /// <summary>
    /// Takes the unterminated remainder as a line. Only meaningful at the end of a stream: a serial
    /// port never ends, so <see cref="SerialSensorStream"/> never calls this, but
    /// <see cref="ReplaySensorStream"/> does, so that a log file whose last frame is missing its
    /// trailing newline is not silently dropped. That is a deliberate replay-only difference, and
    /// <c>ReplaySensorStreamTests</c> pins it.
    /// </summary>
    public bool TryReadPendingLine(out string line)
    {
        if (_cursor >= _buffer.Length)
        {
            line = string.Empty;
            return false;
        }

        line = _buffer.ToString(_cursor, _buffer.Length - _cursor);
        _cursor = _buffer.Length;
        _scanFrom = _cursor;
        Compact();
        return true;
    }

    /// <summary>
    /// Discards the unread remainder when it has grown past <see cref="MaxBuffer"/>, and counts what
    /// it discarded. Call this after draining complete lines, which is where MainWindow put its
    /// <c>if (_rxBuffer.Length > 10000) _rxBuffer.Clear();</c>.
    /// </summary>
    public void TrimOverflow()
    {
        int unread = BufferedCharacters;
        if (MaxBuffer > 0 && unread > MaxBuffer)
        {
            _dropped += unread;
            Clear();
        }
    }

    /// <summary>Discards everything buffered. Not counted as dropped: it is a deliberate reset.</summary>
    public void Clear()
    {
        _buffer.Clear();
        _cursor = 0;
        _scanFrom = 0;
    }

    private void Compact()
    {
        if (_cursor >= CompactThreshold || _cursor == _buffer.Length)
        {
            _buffer.Remove(0, _cursor);
            _scanFrom -= _cursor;
            _cursor = 0;
        }
    }
}
