using EchoEnvironment.Core;

namespace EchoEnvironment.Hardware;

/// <summary>
/// Replays a captured serial log through the same framing and parsing the production path uses
/// (Phase 3c), so the visualizer can be debugged with no sensor attached.
///
/// ⚠️ THE PLAN'S WARNING IS STILL OPEN (§3c, §4): "ReplaySensorStream needs a real captured log, and
/// the repo has none." That is still true — no capture exists in the repo. This class is therefore
/// tested against a synthetic log written by ReplaySensorStreamTests, which proves it reads, frames
/// and parses a log correctly but does NOT prove parity with the real device. The parity half of
/// Phase 3's gate stays open until a capture is taken during a hardware session.
///
/// The replay runs the SAME <see cref="LineFramer"/> and the SAME <see cref="SensorFrameParser"/>
/// over the file's text as <see cref="SerialSensorStream"/> runs over the port's text, and it feeds
/// the framer in fixed-size chunks rather than one whole read, so a frame split across a chunk
/// boundary behaves exactly as it does on a serial port. That is the point: a frame that survives
/// one survives the other, and any difference between them is a framing difference, not a parsing
/// one.
///
/// THREADING: frames are raised on a worker thread, never the UI thread — see ISensorStream.
/// </summary>
public sealed class ReplaySensorStream : ISensorStream
{
    /// <summary>
    /// Characters handed to the framer at a time. Deliberately smaller than a log file so that
    /// chunk-boundary framing is exercised on replay, the way it is on a real port.
    /// </summary>
    private const int ChunkCharacters = 4096;

    private readonly string _logPath;
    private readonly LineFramer _framer = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _pump;
    private bool _open;
    private bool _disposed;
    private long _frames;
    private long _rejected;

    public event EventHandler<SensorFrame>? FrameReceived;

    public event EventHandler<string>? StatusChanged;

    public event EventHandler<Exception>? Faulted;

    public event EventHandler<string>? LineRejected;

    /// <summary>
    /// Milliseconds to wait between frames, reproducing the original arrival pacing. 0 replays as
    /// fast as the consumer can absorb, which is what makes a deterministic test possible.
    /// </summary>
    public int IntervalMilliseconds { get; set; }

    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return _open;
            }
        }
    }

    /// <summary>Frames delivered so far.</summary>
    public long FramesReplayed => Interlocked.Read(ref _frames);

    /// <summary>Lines that arrived framed but did not parse.</summary>
    public long LinesRejected => Interlocked.Read(ref _rejected);

    /// <summary>The framer's overflow counter, exposed for diagnostics and for tests.</summary>
    public long DroppedBytes => _framer.DroppedBytes;

    public ReplaySensorStream(string logPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);
        _logPath = logPath;
    }

    public IReadOnlyList<PortDescriptor> ListPorts() => [new PortDescriptor(_logPath)];

    /// <summary>portName and baudRate are ignored: the log is the source and its own pacing is used.</summary>
    public void Open(string portName, int baudRate)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ReplaySensorStream));
        }

        lock (_gate)
        {
            if (_open)
            {
                throw new InvalidOperationException($"Already replaying {_logPath}.");
            }

            _cts = new CancellationTokenSource();
            _open = true;
            _pump = Task.Run(() => Pump(_cts.Token));
        }

        StatusChanged?.Invoke(this, $"Replaying {_logPath}");
    }

    public void Close()
    {
        CancellationTokenSource? cts;
        Task? pump;
        lock (_gate)
        {
            if (!_open)
            {
                return;
            }

            _open = false;
            cts = _cts;
            pump = _pump;
            _cts = null;
            _pump = null;
        }

        cts?.Cancel();

        try
        {
            pump?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // A pump that faulted while closing is not a close failure.
        }

        cts?.Dispose();
    }

    public void Dispose()
    {
        Close();
        _disposed = true;
    }

    private void Pump(CancellationToken cancellation)
    {
        try
        {
            using var reader = new StreamReader(_logPath);
            char[] chunk = new char[ChunkCharacters];

            while (!cancellation.IsCancellationRequested)
            {
                int read = reader.Read(chunk, 0, ChunkCharacters);
                if (read <= 0)
                {
                    break;
                }

                _framer.Append(new string(chunk, 0, read));

                while (_framer.TryReadLine(out string line))
                {
                    Deliver(line);
                }

                _framer.TrimOverflow();
            }

            // A log file, unlike a serial port, has an end. A final frame with no trailing newline
            // is still a frame, so it is drained here. SerialSensorStream deliberately does not do
            // this: a port has no end to drain at.
            while (_framer.TryReadPendingLine(out string tail))
            {
                Deliver(tail);
            }

            StatusChanged?.Invoke(this, $"Replay finished: {_logPath}");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            Faulted?.Invoke(this, ex);
        }
    }

    private void Deliver(string line)
    {
        if (SensorFrameParser.TryParse(line, out SensorFrame frame))
        {
            Interlocked.Increment(ref _frames);
            FrameReceived?.Invoke(this, frame);
        }
        else if (!string.IsNullOrWhiteSpace(line))
        {
            Interlocked.Increment(ref _rejected);
            LineRejected?.Invoke(this, line);
        }

        if (IntervalMilliseconds > 0)
        {
            Thread.Sleep(IntervalMilliseconds);
        }
    }
}
