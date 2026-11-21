using System.ComponentModel;
using System.IO;
using EchoEnvironment.Core;
using EchoEnvironment.Hardware;

namespace EchoEnvironment;

/// <summary>
/// The application's state and policy, held apart from the window that draws it (MIGRATION_PLAN 6b).
///
/// WHAT MOVED HERE FROM MainWindow AT 6b, because §2 named each of these as 6b's list: the status text,
/// the port list and its selection, connect/disconnect and the whole connection lifecycle, the zone-count
/// and status-line arithmetic, the orientation readout, the keep-map toggle, the capture request, and the
/// render-rate policy (§2's note that Phase 4 added the rate as a third thing 6b must absorb). The scene
/// graph, the orbit, the zone renderer, the orientation *matrix*, the render timer and the frame coalescer
/// stayed with the window: they are how a frame becomes pixels on a UI thread, which is the thing §2 calls
/// "XAML + input only".
///
/// WHY THIS TYPE IS WPF-FREE, AND WHY THAT IS NOT AN ACCIDENT. ArchitectureInvariantTests.
/// WpfConversionIsConfinedToOneType forbids any public type in the App or Rendering assemblies from naming
/// a Core type and a WPF type in the same public surface, exempting only WpfMath, Window and Application.
/// MainViewModel speaks Core (Mat3, SensorFrame) and Hardware (ISensorStream, SerialSensorStream,
/// PortDescriptor), so it may not speak WPF either - and that rules out the two WPF types a view model
/// would most naturally use:
///
///   ICommand lives in PresentationCore. §2 sketches 6b as "INotifyPropertyChanged + ICommand", but an
///   ICommand property on this type would make it a second conversion point, which is exactly what the
///   guard exists to keep from existing. So the commands are ordinary public methods and the XAML keeps
///   the Click= handlers it already has. The binding mechanism 6b actually needs is INotifyPropertyChanged,
///   which lives in System.ComponentModel and is not WPF.
///
///   Dispatcher / DispatcherTimer live in PresentationFramework. The view model therefore cannot marshal
///   its own property changes, which is why the cross-thread half of the status line is not a property
///   write - see TransportAnnounced below.
///
/// WHY THERE ARE TWO STATUS CHANNELS INSTEAD OF ONE. MainWindow's Stream_StatusChanged used to wrap every
/// transport status in Dispatcher.BeginInvoke, and Connect_Click then had to queue its own capture-path
/// message *behind* that queued message or it was overwritten before anyone could read it. That ordering is
/// a behaviour with a reason recorded in its own comment, and a view model that cannot touch a Dispatcher
/// cannot reproduce it. So: Status is written from the UI thread and raises PropertyChanged synchronously;
/// TransportAnnounced carries the strings the transport raised from its own thread, and MainWindow queues
/// those behind each other exactly as it queued them before. Collapsing both into one property would have
/// silently reversed that order.
///
/// WHAT IS DELIBERATELY NOT HERE. No ZoneRenderer, no SceneLayers, no Matrix3D. The keep-map toggle is a
/// flag this type owns; clearing the map is an action on the window's own renderer, so SetKeepMap reports
/// that a clear is owed rather than reaching for the thing that needs clearing. Same reason the orientation
/// arrives as Core's Mat3 and not as a transform: the window converts it once, in one line, and this type
/// never learns which matrix type the UI renders with.
/// </summary>
public sealed class MainViewModel
{
    /// <summary>The zone fan's angular width, and the band a reading has to fall inside to be drawn or counted.</summary>
    public const double FieldOfViewDegrees = 45.0;
    public const double MaxDistanceMm = 4000.0;
    public const double MinDistanceMm = 20.0;

    /// <summary>Buckets in the accumulated map. A constructor argument to WorldMap, not a magic number.</summary>
    public const int MapBuckets = 16;

    /// <summary>The rates the toolbar offers, and the one §2 names as the default.</summary>
    public static readonly int[] RenderRateChoices = [15, 30, 60, 120];
    public const int DefaultRenderRateHz = 60;

    /// <summary>The port preselected when the list does not say otherwise.</summary>
    public const string DefaultPort = "COM12";

    private const int MaxRejectedLineChars = 80;

    private readonly SensorSource _source;

    /// <summary>
    /// Never opened. Exists only so RefreshPorts can ask the transport seam which ports exist. It stays a
    /// SerialSensorStream even in a --sim or --replay session, deliberately: the port list is a question
    /// about the machine, not about the chosen source, and asking SimulatedSensorStream would answer it with
    /// the single fake name "SIM". Moved here with the rest of the connection concerns at 6b.
    /// </summary>
    private readonly SerialSensorStream _portProbe = new();

    private ISensorStream? _stream;
    private string? _capturePath;
    private int _renderedFrames;
    private bool _keepMap;
    private bool _captureRequested;
    private int _renderRate = DefaultRenderRateHz;
    private string _status;

    /// <summary>
    /// Builds the state for one chosen sensor source. The source arrives as the same factory 6c introduced,
    /// unchanged: every Connect gets a live stream and every Disconnect disposes it, so a second Connect
    /// after a Disconnect still works and a replay replays again from the start.
    /// </summary>
    public MainViewModel(SensorSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;

        // The chosen source is announced rather than left implicit. A --sim or --replay session looks
        // identical to a serial one until something goes wrong, and the difference is not cosmetic: a
        // replay writes no capture file, and a simulator needs no device. Saying it in the status line is
        // what stops "why is no data arriving?" from becoming "is the port broken?".
        _status = source.Kind switch
        {
            SensorSourceKind.Simulated => "Simulated source: press Connect to start synthetic frames (no hardware)",
            SensorSourceKind.Replay => $"Replay source: press Connect to replay {source.ReplayPath}",
            _ => "Ready",
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// A frame the transport delivered, forwarded to whoever renders it. MainWindow subscribes and hands it
    /// to the coalescer, which is the window's own UI-thread mechanism (§2: "the timer is a UI-thread
    /// mechanism and can stay with the view"). The view model cannot hold the coalescer without the window
    /// reaching into it, and cannot hold a Dispatcher, so the frame crosses the seam as an event.
    /// </summary>
    public event EventHandler<SensorFrame>? FrameArrived;

    /// <summary>
    /// A status string the transport raised from its own thread. MainWindow queues these through
    /// Dispatcher.BeginInvoke, which is what preserves the ordering Connect_Click depends on. See the class
    /// comment: this is the reason Status alone is not enough.
    /// </summary>
    public event EventHandler<string>? TransportAnnounced;

    public SensorSourceKind SourceKind => _source.Kind;

    /// <summary>Raw capture is a property of the serial transport, so the toolbar's checkbox means something for exactly one source.</summary>
    public bool CaptureSupported => _source.CaptureSupported;

    public bool IsConnected => _stream is not null;

    /// <summary>The path of the capture the current session armed, or null. MainWindow reports it in the status line.</summary>
    public string? CapturePath => _capturePath;

    public bool KeepMap => _keepMap;

    public bool CaptureRequested => _captureRequested;

    public int RenderRate => _renderRate;

    public int RenderedFrames => _renderedFrames;

    public IReadOnlyList<string> Ports { get; private set; } = [];

    public string SelectedPort { get; private set; } = DefaultPort;

    public string OrientationReadout { get; private set; } = "Orientation: no IMU data";

    /// <summary>
    /// The status line, as written by this type from the UI thread. Cross-thread status does not come
    /// through here; it comes through TransportAnnounced.
    /// </summary>
    public string Status
    {
        get => _status;
        private set
        {
            _status = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        }
    }

    /// <summary>
    /// Asks the transport seam which ports exist and decides which one the picker should show. The caller
    /// passes what the picker currently holds, so a user's choice survives a Refresh - which is the whole
    /// reason this takes a parameter instead of reading a control.
    /// </summary>
    public void RefreshPorts(string currentSelection)
    {
        Ports = _portProbe.ListPorts().Select(p => p.Name).Order().ToList();
        SelectedPort = string.IsNullOrWhiteSpace(currentSelection)
            ? (Ports.Contains(DefaultPort) ? DefaultPort : Ports.Count > 0 ? Ports[0] : DefaultPort)
            : currentSelection;
    }

    /// <summary>
    /// Establishes a connection. Returns true when a live stream was established, which is what tells the
    /// view to start its render timer and relabel the button. Returns false when one already exists or the
    /// attempt failed; in the failure case the status line has already been written.
    ///
    /// THE TOGGLE IS NOT HERE. MainWindow's button is one control for both directions, so deciding which
    /// direction a press means is the view's business; Connect and Disconnect are both on this type, and the
    /// view calls one of them. A Connect that quietly disconnected instead would make the order of
    /// "stop the render timer" and "write the status line" depend on which class happened to notice the
    /// toggle, and that order is a behaviour with a reason recorded in Disconnect's own comment.
    ///
    /// WHAT IS UNCHANGED FROM THE WINDOW AND DELIBERATELY SO: the port/baud validation, the exception filter,
    /// the capture armed before Open(), and the fact that a failed Open() releases the stream it just built.
    /// Each of those is a behaviour with a reason recorded in its own comment; 6b moved them, it did not
    /// revise them.
    /// </summary>
    public bool Connect(string portName, string baudText)
    {
        if (_stream is not null)
        {
            return false;
        }

        // For a serial session the port and baud come from the toolbar. For --sim and --replay they are
        // ignored by the stream itself (SimulatedSensorStream paces itself; ReplaySensorStream reads the
        // log), but the toolbar is still what the user sees, so an empty port box is reported rather than
        // silently accepted and then ignored.
        if (string.IsNullOrWhiteSpace(portName) || !int.TryParse(baudText, out int baud) || baud <= 0)
        {
            Status = "Invalid port or baud rate";
            return false;
        }

        ISensorStream stream = _source.CreateStream();
        try
        {
            stream.FrameReceived += Stream_FrameReceived;
            stream.LineRejected += Stream_LineRejected;
            stream.StatusChanged += Stream_StatusChanged;

            // Armed BEFORE Open(): DataReceived can fire as soon as the port is open, so a capture attached
            // afterwards would miss the first burst - and the first burst is exactly where a framing
            // difference between device and replay would show up.
            if (_captureRequested && stream is SerialSensorStream serial)
            {
                StartCaptureFor(serial, portName, baudText);
            }

            stream.Open(portName, baud);

            _stream = stream;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            // The stream was never handed to Disconnect(), so it is released right here. Whether a failed
            // Open() leaks the port is defect D5, inside SerialSensorStream: it was preserved unchanged by
            // the Phase 3 extraction and fixed in 3d's own commit, and 6b does not revisit it.
            stream.Dispose();
            Status = ex.Message;
            return false;
        }
    }

    public void Disconnect()
    {
        ISensorStream? stream = _stream;
        _stream = null;
        long captured = 0;

        if (stream is not null)
        {
            // The count is read before Dispose, because Dispose is what closes the file and the count is
            // the only record of how much arrived. AutoFlush on the capture means the count and the file
            // agree.
            captured = stream is SerialSensorStream serial ? serial.CaptureCharacters : 0;

            stream.FrameReceived -= Stream_FrameReceived;
            stream.LineRejected -= Stream_LineRejected;
            stream.StatusChanged -= Stream_StatusChanged;
            stream.Dispose();
        }

        // The capture closes inside Dispose. Only a session that actually captured gets a different status
        // message, so the never-capturing path still reads exactly "Disconnected".
        string? capturePath = _capturePath;
        _capturePath = null;

        Status = capturePath is null
            ? "Disconnected"
            : $"Disconnected - captured {captured} characters to {capturePath}";
    }

    /// <summary>
    /// Applies the capture checkbox to whatever connection is live: checking it starts a capture, unchecking
    /// it closes the file. With no connection the choice is simply remembered and applied by Connect, so
    /// arming capture before plugging the device in is the normal way to use this.
    /// </summary>
    public void SetCaptureRequested(bool requested, string portName, string baudText)
    {
        _captureRequested = requested;

        if (_stream is not SerialSensorStream serial)
        {
            Status = requested ? "Capture armed: starts on Connect" : "Capture off";
            return;
        }

        if (requested)
        {
            StartCaptureFor(serial, portName, baudText);
            return;
        }

        serial.StopCapture();
        _capturePath = null;
        Status = "Capture stopped";
    }

    /// <summary>
    /// Records the keep-map choice and reports whether the accumulated map now has to be thrown away.
    ///
    /// WHY IT RETURNS A BOOL INSTEAD OF CLEARING THE MAP ITSELF. Clearing it is ZoneRenderer's job, and
    /// ZoneRenderer is constructed from WPF scene objects. Naming it here would make this type speak WPF and
    /// Core in the same public surface, which is the exact thing WpfConversionIsConfinedToOneType forbids.
    /// The flag is state and belongs here; the clearing is an action on the window's own renderer.
    /// </summary>
    public bool SetKeepMap(bool keepMap)
    {
        _keepMap = keepMap;
        return !keepMap;
    }

    /// <summary>
    /// Records the render rate, and reports whether the value was usable. The rate is a policy, so it lives
    /// here; the DispatcherTimer that consumes it is a UI-thread mechanism and stays with the window.
    /// </summary>
    public bool SetRenderRate(int hertz)
    {
        if (hertz <= 0)
        {
            return false;
        }

        _renderRate = hertz;
        return true;
    }

    /// <summary>
    /// The status line for one rendered frame.
    ///
    /// THE SAME TEXT THE WINDOW USED TO BUILD, CHARACTER FOR CHARACTER. The visible-zone count and the
    /// maximum are computed in one pass, because a ReadOnlySpan is not IEnumerable and the LINQ pair this
    /// replaced allocated an enumerator and a closure and walked the array twice. The loop's boundary is
    /// pinned against the LINQ it replaced by TheStatusLineLoopAgreesWithTheLinqItReplaced, and the band it
    /// counts against is this type's own MinDistanceMm / MaxDistanceMm rather than numbers copied into a
    /// test.
    /// </summary>
    public void FrameRendered(SensorFrame frame, long droppedFrames)
    {
        _renderedFrames++;

        ReadOnlySpan<int> distances = frame.Distances.Span;
        int visible = 0;
        int maxMm = int.MinValue;
        for (int i = 0; i < distances.Length; i++)
        {
            int mm = distances[i];
            if (mm >= MinDistanceMm && mm <= MaxDistanceMm)
            {
                visible++;
            }

            if (mm > maxMm)
            {
                maxMm = mm;
            }
        }

        ReadOnlySpan<int> imu = frame.Imu.Span;
        string imuText = frame.HasImu
            ? $" | gyro {imu[0]},{imu[1]},{imu[2]} accel {imu[3]},{imu[4]},{imu[5]} mag {imu[6]},{imu[7]},{imu[8]}"
            : string.Empty;

        // Phase 4's gate: the drop policy must be visible rather than mysterious. Without this number, a
        // coalescing UI that throws away 40 of 60 frames looks like a stuttering app with no explanation
        // attached to it. The count itself belongs to the coalescer, which stayed with the window, so the
        // window passes it in.
        string dropText = droppedFrames > 0 ? $" | dropped {droppedFrames}" : string.Empty;

        Status = $"Frames: {_renderedFrames} ({distances.Length} zones, {visible} visible, max {maxMm} mm){imuText}{dropText}";
    }

    /// <summary>
    /// DEFECT D1 (Phase 6a) lives in the format string below, and this is now the file that holds it.
    ///
    /// The three expressions were each physically correct and the variable names were correct; only the two
    /// printed labels were swapped - the readout printed the roll value under "Pitch" and the elevation
    /// value under "Roll". 6a's fix was the format string and nothing else, because changing an expression
    /// would change what is measured, which is a different change from naming it correctly. §7 finding 3
    /// corrected the plan's own description of the second expression: atan2(M12, M22) is rotation of the
    /// sensor about its own optical (-Z) axis, i.e. image roll, not "rotation about the up axis".
    /// RollAboutOpticalAxis_DoesNotChangeElevation_AndIsNotRotationAboutUp is the test that proves that
    /// correction.
    ///
    /// THE CONDITIONING CAVEAT, WHICH A LABEL FIX DOES NOT TOUCH. atan2(M12, M22) amplifies accelerometer
    /// noise by 1 / hypot(M12, M22). On the captured session that pair never exceeds 0.19 in magnitude
    /// (CapturedLog_SensorPointsAlmostStraightDown_WhichIsWhereTheAngleReadoutBreaks), so the Roll figure
    /// amplifies noise more than 5x and approaches the point where the angle is undefined outright - the
    /// gimbal-lock signature of an asin decomposition with M32 near 1. The labels are correct; the number
    /// under "Roll" is still the least trustworthy one on screen.
    ///
    /// WHY THE READOUT IS A STRING AND THE MATRIX IS NOT HELD HERE. The readout is presentation. The matrix
    /// that the 3D scene rotates by is Core's Mat3, and the window converts it once, in one line, through
    /// WpfMath - which is the only type the architecture guard permits to name Core and WPF together.
    /// </summary>
    public void SetOrientation(Mat3 rotation)
    {
        // Forward is the sensor's -Z axis; world axes are X east, Y up, Z south.
        double heading = Math.Atan2(-rotation.M31, rotation.M33) * 180.0 / Math.PI;
        if (heading < 0)
        {
            heading += 360;
        }

        double pitch = Math.Asin(Math.Clamp(-rotation.M32, -1, 1)) * 180.0 / Math.PI;
        double roll = Math.Atan2(rotation.M12, rotation.M22) * 180.0 / Math.PI;

        // D1 was: `Pitch: {roll}` and `Roll: {pitch}`. Each label now prints the value named by the variable
        // of the same name, which is the whole of the fix.
        OrientationReadout = $"Orientation\nHeading: {heading,6:0.0}°\nPitch:   {pitch,6:0.0}°\nRoll:    {roll,6:0.0}°";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OrientationReadout)));
    }

    private void StartCaptureFor(SerialSensorStream stream, string portName, string baudText)
    {
        int.TryParse(baudText, out int baud);
        string path = SerialSensorStream.DefaultCapturePath(portName, baud);

        try
        {
            stream.StartCapture(path);
            _capturePath = path;
            Status = $"Capturing to {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A capture that cannot be started is reported and the connection continues unchanged -
            // deliberately swallowed here rather than allowed to reach Connect's catch block, which would
            // dispose the stream and end a session over a storage problem.
            _capturePath = null;
            Status = $"Capture failed: {ex.Message}";
        }
    }

    private void Stream_FrameReceived(object? sender, SensorFrame frame) => FrameArrived?.Invoke(sender, frame);

    private void Stream_StatusChanged(object? sender, string status) => TransportAnnounced?.Invoke(sender, status);

    private void Stream_LineRejected(object? sender, string line)
        => TransportAnnounced?.Invoke(sender, $"Rejected line: {(line.Length > MaxRejectedLineChars ? line[..MaxRejectedLineChars] : line)}");
}
