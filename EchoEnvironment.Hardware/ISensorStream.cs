using EchoEnvironment.Core;

namespace EchoEnvironment.Hardware;

/// <summary>
/// The sensor transport seam (Phase 3b). Everything above this line — the App, and from Phase 4 the
/// coalescing stage — consumes frames without knowing whether they came from a serial port, a
/// replayed log file, or the simulator.
///
/// DEPENDENCY CONTRACT: this assembly references Core only. It must never reference the App or
/// EchoEnvironment.Rendering (MIGRATION_PLAN 1, dependency graph). HardwareAssemblyReferencesNothingButCore
/// enforces it.
///
/// THREADING CONTRACT — read this before subscribing:
/// FrameReceived, LineRejected and Faulted are raised on the transport's own thread, NOT the UI
/// thread. SerialSensorStream raises them from SerialPort.DataReceived; the simulator raises them
/// from a timer thread. Consumers that touch UI objects must marshal — MainWindow does, via
/// Dispatcher.BeginInvoke, exactly as it did before this seam existed. Phase 4 replaces that with a
/// bounded channel, but the obligation to marshal stays with the consumer, not the stream.
/// </summary>
public interface ISensorStream : IDisposable
{
    /// <summary>A complete, successfully parsed frame.</summary>
    event EventHandler<SensorFrame> FrameReceived;

    /// <summary>
    /// Transport state for the status line: "Connected to COM14", "Disconnected", and so on.
    /// </summary>
    event EventHandler<string> StatusChanged;

    /// <summary>
    /// A recoverable transport failure — a port that vanished mid-read, an access denial, a bad
    /// port name. Not a crash: the stream stays usable or is cleanly closed.
    /// </summary>
    event EventHandler<Exception> Faulted;

    /// <summary>
    /// A line that arrived framed correctly but did not parse. Carries the raw line.
    ///
    /// DEVIATION FROM THE PLAN: MIGRATION_PLAN 3b's ISensorStream has no member for this, but the
    /// current app reports "Rejected line: …" in the status bar, and dropping the event would be a
    /// behaviour change smuggled into a structural move. Kept as its own event rather than folded
    /// into StatusChanged so the two diagnostics cannot be confused with each other.
    /// </summary>
    event EventHandler<string> LineRejected;

    IReadOnlyList<PortDescriptor> ListPorts();

    bool IsOpen { get; }

    void Open(string portName, int baudRate);

    void Close();
}
