using System.IO;
using System.Text;

namespace EchoEnvironment.Hardware;

/// <summary>
/// Writes the raw text a transport received, before any framing or parsing. This exists to close the
/// obligation MIGRATION_PLAN 3c and 4 have carried since Phase 3: "ReplaySensorStream needs a real
/// captured log, and the repo has none."
///
/// WHY THIS IS NOT A DECORATOR ON ISensorStream. The seam's contract is frames, not bytes: an
/// ISensorStream hands out SensorFrame values, and by then SensorFrameParser has already run. A
/// capture taken from the seam would record the parser's output, and replaying it would compare the
/// parser against itself - a parity check that cannot fail, and therefore proves nothing. The raw
/// text exists in exactly one place, inside the transport, which is why the tee has to live there
/// too. LineRejected is not a substitute: it carries only the lines that failed to parse.
///
/// WHAT "RAW" MEANS PRECISELY. This records the string SerialPort.ReadExisting() returned - the bytes
/// after the port's decoder, not the bytes on the wire - and writes them with Encoding.ASCII, which
/// is SerialPort's default decoder. So the capture is byte-faithful to what the app saw, and it
/// inherits the decoder: a device emitting a byte outside ASCII would appear here as the decoder's
/// substitution rather than as the original byte. Stating that matters, because an artifact that
/// claims to be the wire and is actually the decode is the kind of artifact that misleads a later
/// investigation.
///
/// TERMINATORS ARE PRESERVED VERBATIM. LineFramer treats '\r' and '\n' as two separate terminators,
/// so a "\r\n" capture replays as a line followed by an empty line. That is what the serial path has
/// always produced. A capture that normalised terminators on the way to disk would replay a stream the
/// port never produced, which is the opposite of what a capture is for. Nothing here calls WriteLine
/// and NewLine is never set.
///
/// FAILURE IS NOT AN EXCEPTION. A diagnostic that can take the sensor path down is worse than no
/// diagnostic. A write failure is recorded in <see cref="Failed"/> and <see cref="Failure"/> and
/// reported to the caller, which abandons the capture and keeps the transport running.
/// </summary>
public sealed class RawTextCapture : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private long _characters;
    private bool _failed;
    private bool _disposed;
    private Exception? _failure;

    /// <summary>The file being written.</summary>
    public string FilePath { get; }

    /// <summary>Characters handed to the file so far. A count of text, not of frames.</summary>
    public long CharactersWritten => Interlocked.Read(ref _characters);

    /// <summary>True once a write has failed. The capture stops permanently at that point.</summary>
    public bool Failed => _failed;

    /// <summary>The exception that ended the capture, or null while it is healthy.</summary>
    public Exception? Failure => _failure;

    public RawTextCapture(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = filePath;

        // Resolved here rather than left to the caller so a capture is never half-configured. A path
        // whose directory does not exist yet is the normal case for this class, not a failure.
        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // AutoFlush, deliberately: a capture that is sitting in a buffer is lost when the app crashes,
        // and "the app crashed while capturing" is the single most valuable capture there is.
        _writer = new StreamWriter(filePath, append: true, Encoding.ASCII) { AutoFlush = true };
    }

    /// <summary>
    /// Appends a received chunk exactly as given. Returns false when the capture has failed or has
    /// already been abandoned. Never throws for a storage problem.
    /// </summary>
    public bool Write(string chunk)
    {
        if (_failed)
        {
            return false;
        }

        if (string.IsNullOrEmpty(chunk))
        {
            return true;
        }

        try
        {
            // SerialPort.DataReceived is not documented to be serialised, so two handlers can reach
            // this method at the same time. The lock protects the file. It does NOT make LineFramer
            // thread-safe, which is an older and separate exposure - see MIGRATION_PLAN 12.11.
            lock (_gate)
            {
                if (_disposed)
                {
                    return false;
                }

                _writer.Write(chunk);
                Interlocked.Add(ref _characters, chunk.Length);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            _failure = ex;
            _failed = true;
            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                _writer.Dispose();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that cannot be closed is reported, not thrown: Dispose runs on the way out of
                // a connection, and throwing there would displace the reason the connection ended.
                _failure ??= ex;
                _failed = true;
            }
        }
    }
}
