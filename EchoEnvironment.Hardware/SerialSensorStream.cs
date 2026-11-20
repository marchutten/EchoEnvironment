using System.IO;
using System.IO.Ports;
using EchoEnvironment.Core;

namespace EchoEnvironment.Hardware;

/// <summary>
/// The production transport (Phase 3c): <see cref="SerialPort"/> + <see cref="LineFramer"/> +
/// <see cref="SensorFrameParser"/>, which used to be inlined in <c>MainWindow.Connect_Click</c> and
/// <c>MainWindow.Port_DataReceived</c>.
///
/// This is a move, not a rewrite. The construction order, the <c>NewLine</c> and <c>ReadTimeout</c>
/// settings, the exception filters, the silent swallow of a port that dies mid-read, and the fact
/// that the receive buffer is NOT cleared between connections are all preserved exactly as
/// MainWindow had them, so the app's behaviour is unchanged by the extraction.
///
/// Two defects that travelled with the code are deliberately left in place, because 3d's governing
/// rule is that a behaviour change gets its own commit (§3 rule 2):
///   - D5: the SerialPort is constructed and subscribed before <c>Open()</c>, so a failed Open
///     leaks it. Marked in <see cref="Open"/>.
///   - D7: <c>ReadTimeout = 1000</c> is set and never honored, because <c>ReadExisting()</c> ignores
///     it.
/// Fixing either here would make a hardware regression unattributable to one of the three changes.
///
/// PHASE 5 ADDITION (the raw capture): <see cref="StartCapture"/> tees the text this transport
/// receives to a file. Until this commit the solution contained no writer of any kind, so the
/// "capture a real serial log" obligation MIGRATION_PLAN 3c and 4 have carried since Phase 3 was not
/// merely undone - it was impossible even during a live hardware session. The capture is deliberately
/// NOT on <see cref="ISensorStream"/>: it is a property of the serial transport, not of the seam, and
/// a replay source has nothing to capture.
/// </summary>
public sealed class SerialSensorStream : ISensorStream
{
    private readonly LineFramer _framer = new();
    private RawTextCapture? _capture;
    private SerialPort? _port;
    private bool _disposed;

    public event EventHandler<SensorFrame>? FrameReceived;

    public event EventHandler<string>? StatusChanged;

    public event EventHandler<Exception>? Faulted;

    public event EventHandler<string>? LineRejected;

    public bool IsOpen => _port is not null;

    /// <summary>The framer's overflow counter, exposed for diagnostics and for tests.</summary>
    public long DroppedBytes => _framer.DroppedBytes;

    // ---------------------------------------------------------------------------------------------
    // The raw capture. Opt-in: with no capture started, nothing above or below behaves differently,
    // and the transport still has no opinion about where a log should be kept.
    // ---------------------------------------------------------------------------------------------

    /// <summary>The file being captured, or null when no capture is active.</summary>
    public string? CaptureFilePath => _capture?.FilePath;

    /// <summary>Raw characters written so far. A count of text, not of frames.</summary>
    public long CaptureCharacters => _capture?.CharactersWritten ?? 0;

    /// <summary>The exception that ended the capture, or null while it is healthy.</summary>
    public Exception? CaptureFailure => _capture?.Failure;

    /// <summary>
    /// Builds a default capture path: a timestamped file named for the port and the baud rate, under a
    /// captures folder next to the executable. The name carries the conditions of the capture because a
    /// log whose baud rate is unknown cannot be compared against a run at a different one.
    /// </summary>
    public static string DefaultCapturePath(string portName, int baudRate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        string safe = string.Concat(portName.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
        return Path.Combine(AppContext.BaseDirectory, "captures", $"{safe}-{baudRate}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
    }

    /// <summary>
    /// Starts capturing raw received text. An unusable path throws here, at the caller's request,
    /// rather than failing later inside the receive handler: a capture that cannot be started must not
    /// be able to prevent a connection. Starting a second capture closes the first, so switching
    /// targets cannot leave a file open by accident.
    /// </summary>
    public void StartCapture(string filePath)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SerialSensorStream));
        }

        // Constructed before the previous capture is released: if this path is unusable the exception
        // propagates and the capture that was working keeps working.
        var capture = new RawTextCapture(filePath);
        RawTextCapture? previous = _capture;
        _capture = capture;
        previous?.Dispose();
    }

    /// <summary>
    /// Ends the capture and closes the file. Safe when no capture is active and safe to call twice:
    /// <see cref="Close"/> calls it, and a consumer may call it first to report a character count
    /// before the connection ends.
    /// </summary>
    public void StopCapture()
    {
        RawTextCapture? capture = _capture;
        _capture = null;
        capture?.Dispose();
    }

    public IReadOnlyList<PortDescriptor> ListPorts() =>
        SerialPort.GetPortNames().Select(name => new PortDescriptor(name)).ToList();

    public void Open(string portName, int baudRate)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SerialSensorStream));
        }

        if (_port is not null)
        {
            throw new InvalidOperationException($"Already connected to {_port.PortName}.");
        }

        // D5, preserved on purpose: construct, subscribe, then Open. If Open() throws, this
        // SerialPort is never disposed and nothing holds a reference to it. MainWindow did exactly
        // this. The fix belongs in 3d's own commit, not in the extraction.
        var port = new SerialPort(portName, baudRate) { NewLine = "\n", ReadTimeout = 1000 };
        port.DataReceived += Port_DataReceived;
        port.Open();

        _port = port;
        StatusChanged?.Invoke(this, $"Connected to {port.PortName}");
    }

    /// <summary>
    /// Closes the port. Note what this does NOT do: it does not raise <see cref="StatusChanged"/>.
    /// MainWindow's Disconnect writes "Disconnected" unconditionally — including when no port was
    /// ever open — and having the transport write it as well would change the never-connected case.
    /// The disconnect message therefore stays the consumer's, and this asymmetry is a deviation
    /// from 3b's sketch, recorded in ISensorStream.
    /// </summary>
    public void Close()
    {
        SerialPort? port = _port;
        _port = null;

        if (port is not null)
        {
            port.DataReceived -= Port_DataReceived;
            port.Dispose();
        }

        // The capture closes with the connection it belongs to, so no file handle can outlive the
        // session that opened it. StopCapture never throws, so a file that will not close cannot
        // disturb a port that is already closing.
        StopCapture();
    }

    public void Dispose()
    {
        Close();
        _disposed = true;
    }

    private void Port_DataReceived(object? sender, SerialDataReceivedEventArgs e)
    {
        if (sender is not SerialPort port)
        {
            return;
        }

        try
        {
            // The tee sits BEFORE the framer and the parser, which is the whole point: a capture taken
            // after parsing would record SensorFrameParser's own output, and replaying it would compare
            // the parser against itself. See RawTextCapture.
            string received = port.ReadExisting();
            _capture?.Write(received);
            _framer.Append(received);

            while (_framer.TryReadLine(out string line))
            {
                if (SensorFrameParser.TryParse(line, out SensorFrame frame))
                {
                    FrameReceived?.Invoke(this, frame);
                }
                else if (!string.IsNullOrWhiteSpace(line))
                {
                    // Blank lines are framing noise (see the "\r\n" note on LineFramer), not
                    // rejected frames. MainWindow filtered them the same way.
                    LineRejected?.Invoke(this, line);
                }
            }

            _framer.TrimOverflow();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            // Port closed while reading. Today MainWindow swallows this silently; Faulted is
            // raised so a consumer CAN report it, but MainWindow does not subscribe to it yet —
            // surfacing it would add a status message the app has never shown.
            Faulted?.Invoke(this, ex);
        }
    }
}
