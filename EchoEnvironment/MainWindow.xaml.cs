using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using EchoEnvironment.Core;
using EchoEnvironment.Hardware;
using EchoEnvironment.Rendering;

namespace EchoEnvironment
{
    /// <summary>
    /// Displays VL53L5CX distance frames as points on a sphere around the sensor.
    /// </summary>
    public partial class MainWindow : Window
    {
        private const double FieldOfViewDegrees = 45.0;
        private const double MaxDistanceMm = 4000.0;
        private const double MinDistanceMm = 20.0;

        // LineHalfWidth moved to ScenePrimitives with AddLine, and SceneUnitsPerMm moved to
        // ZoneRenderer with UpdateFrame: both describe how a distance becomes a drawn thing, which is
        // not something the window needs to know.

        private readonly Model3DGroup _boardGroup = new();

        /// <summary>
        /// Phase 5 commit 4 (defect D8): the sensor viewport's layers, named and ordered by SceneLayers.
        /// This replaced the flat Model3DGroup named _points plus a map group local, whose meaning depended
        /// on the order things were appended to it.
        /// </summary>
        private readonly SceneLayers _layers;

        private readonly MatrixTransform3D _orientation = new(Matrix3D.Identity);
        private readonly OrientationFilter _imuFilter = new();
        private long _lastImuTimestamp;
        private const string DefaultPort = "COM12";
        private ISensorStream? _stream;
        private string? _capturePath;

        /// <summary>
        /// Never opened. Exists only so RefreshPorts asks the transport seam which ports exist,
        /// instead of calling SerialPort directly — which is what lets the App drop its
        /// System.IO.Ports reference entirely.
        /// </summary>
        private readonly SerialSensorStream _portProbe = new();

        private int _frames;
        private const int MapBuckets = 16;
        private readonly GeometryModel3D _gridModel = MeshFactory.CreateGridModel(3.98, 0.5);

        // Phase 5 commit 2: the zone models, the heat-grid cells, the map dictionary, the bucket
        // geometries and the map throttle moved to ZoneRenderer; the camera angles, the drag state and
        // the scene rotation moved to OrbitCameraController. Both fields are assigned in the
        // constructor rather than by initialiser, because both need XAML-named elements, which do not
        // exist until InitializeComponent has run.
        private readonly ZoneRenderer _zones;
        private readonly OrbitCameraController _orbit;

        /// <summary>
        /// Phase 5 commit 3 (defect D3): the shared frozen brushes and materials the renderer reuses
        /// across frames. A field rather than a static so the cache is per window, matching BrushCache's
        /// own reasoning about not sharing mutable state across a process.
        /// </summary>
        private readonly BrushCache _brushes = new();

        // Phase 4 (defect D2): frames arrive on the transport thread and are drained by this timer on
        // the UI thread, one per tick. The rate is a declared setting in the toolbar, so frame rate is
        // a UI decision instead of a consequence of the baud rate.
        private readonly FrameCoalescer _coalescer = new();
        private readonly DispatcherTimer _renderTimer = new();
        private static readonly int[] RenderRateChoices = [15, 30, 60, 120];
        private const int DefaultRenderRateHz = 60;

        public MainWindow()
        {
            InitializeComponent();

            // Both collaborators take XAML-named elements, so neither can exist before
            // InitializeComponent. They are created before the scene graph because the view transforms
            // below come from the orbit controller.
            _orbit = new OrbitCameraController(Camera, SensorVisual, ViewBorder);

            // D8 (MIGRATION_PLAN 5c): the scene's layers are named and ordered by SceneLayers, once, in
            // one constructor. What used to sit here was a flat group built by appending - marker, map,
            // zones, then the transparent sphere "added last" with a comment explaining that WPF blends
            // transparent models over what was drawn before - while ShowGrid_Click inserted the grid at
            // Children.Count - 1 of that same collection.
            //
            // The order that comment recorded is still the order that matters, and it is now guaranteed
            // by construction instead of by an insertion sequence: the transparent reference sphere is
            // the last layer. The order *between* the opaque layers (grid, map, marker, zones) is now
            // declared by role rather than by the sequence the old constructor happened to use, because
            // WPF depth-tests opaque models and the listing order does not decide which is in front.
            _layers = new SceneLayers(MeshFactory.CreateSensorMarker(), MeshFactory.CreateReferenceSphere(4.0));

            // The view transform goes on the zone group, not on the sensor layer: the marker sits still
            // while the zone fan rotates, exactly as it did when the marker was a separate child of the
            // flat root.
            _layers.Zones.Group.Transform = _orbit.CreateViewTransform(false, _orientation);
            _zones = new ZoneRenderer(_layers.Zones.Group, _layers.Map.Group, HeatGrid, _brushes, MinDistanceMm, MaxDistanceMm, FieldOfViewDegrees, MapBuckets);

            SensorVisual.Content = _layers.Root;

            var boardRoot = new Model3DGroup();
            boardRoot.Children.Add(ScenePrimitives.CreateWorldAxes());
            _boardGroup.Transform = _orbit.CreateViewTransform(true, _orientation);
            _boardGroup.Children.Add(MeshFactory.CreateBoardModel());
            ScenePrimitives.AddAxes(_boardGroup, 0.5, 0.01);
            boardRoot.Children.Add(_boardGroup);
            BoardVisual.Content = boardRoot;

            _orbit.UpdateCamera();
            PreviewKeyDown += Window_PreviewKeyDown;

            // Phase 4: the render rate is a UI policy declared here, not a side effect of how fast
            // the serial side happens to deliver (defect D2). The combo box is populated in code
            // rather than in XAML so the numbers and RenderInterval share one source.
            foreach (int hz in RenderRateChoices)
            {
                RenderRateComboBox.Items.Add(hz);
            }

            RenderRateComboBox.SelectedItem = DefaultRenderRateHz;
            _renderTimer.Interval = RenderInterval(DefaultRenderRateHz);
            _renderTimer.Tick += RenderTimer_Tick;

            RefreshPorts();
            Closed += (_, _) => Disconnect();
        }

        /// <summary>
        /// Sets the sensor viewport (e.g. from a 9 DOF sensor) as a sensor-to-world rotation matrix.
        ///
        /// Takes Core's Mat3 and converts to WPF's Matrix3D exactly once, here. The angle readout
        /// below reads Mat3 directly: the angles are a property of the orientation, not of whichever
        /// matrix type the UI happens to render with.
        ///
        /// DEFECT D1, FIXED AT PHASE 6a. The three expressions below were each physically correct and
        /// the variable names were correct; only the two printed labels were swapped — the readout
        /// printed the roll value under "Pitch" and the elevation value under "Roll". The fix is the
        /// format string, and nothing else: changing an expression here would change what is measured,
        /// which is a different change from naming it correctly.
        ///
        /// WHY THE EXPRESSIONS ARE NOT MERELY RENAMED. `asin(-M32)` is the elevation of the sensor's
        /// look direction above horizontal — a genuine pitch. `atan2(M12, M22)` is the angle of world up
        /// projected into the sensor's own XY plane, i.e. image roll: rotation about the sensor's own
        /// optical (-Z) axis. MIGRATION_PLAN 6a originally described the second one as "rotation about
        /// the up axis"; §7 finding 3 corrected that, and
        /// RollAboutOpticalAxis_DoesNotChangeElevation_AndIsNotRotationAboutUp is the test that proves
        /// the correction, because a board rolled about its optical axis while level is NOT rotated about
        /// the world up axis and still reads 30 degrees.
        ///
        /// THE CONDITIONING CAVEAT, WHICH A LABEL FIX DOES NOT TOUCH. `atan2(M12, M22)` amplifies
        /// accelerometer noise by 1 / hypot(M12, M22). On the captured session that pair never exceeds
        /// 0.19 in magnitude (CapturedLog_SensorPointsAlmostStraightDown_WhichIsWhereTheAngleReadoutBreaks),
        /// so the Roll figure amplifies noise more than 5x and approaches the point where the angle is
        /// undefined outright — the gimbal-lock signature of an asin decomposition with M32 near 1. The
        /// labels are now correct; the number under "Roll" is still the least trustworthy one on screen.
        /// </summary>
        public void SetOrientation(Mat3 rotation)
        {
            _orientation.Matrix = rotation.ToMatrix3D();

            // Forward is the sensor's -Z axis; world axes are X east, Y up, Z south.
            double heading = Math.Atan2(-rotation.M31, rotation.M33) * 180.0 / Math.PI;
            if (heading < 0)
            {
                heading += 360;
            }

            double pitch = Math.Asin(Math.Clamp(-rotation.M32, -1, 1)) * 180.0 / Math.PI;
            double roll = Math.Atan2(rotation.M12, rotation.M22) * 180.0 / Math.PI;

            // D1 was here: `Pitch: {roll}` and `Roll: {pitch}`. Each label now prints the value named by
            // the variable of the same name, which is the whole of the fix.
            OrientationText.Text = $"Orientation\nHeading: {heading,6:0.0}°\nPitch:   {pitch,6:0.0}°\nRoll:    {roll,6:0.0}°";
        }

        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (System.Windows.Input.Keyboard.FocusedElement is TextBox or ComboBox or System.Windows.Controls.Primitives.TextBoxBase)
            {
                return;
            }

            const double step = 5;
            switch (e.Key)
            {
                case System.Windows.Input.Key.Left:
                    _orbit.NudgeYaw(-step);
                    break;
                case System.Windows.Input.Key.Right:
                    _orbit.NudgeYaw(step);
                    break;
                case System.Windows.Input.Key.Up:
                    _orbit.NudgePitch(step);
                    break;
                case System.Windows.Input.Key.Down:
                    _orbit.NudgePitch(-step);
                    break;
                case System.Windows.Input.Key.Space:
                    _orbit.Reset();
                    e.Handled = true;
                    return;
                case System.Windows.Input.Key.X:
                    _orbit.RotateScene(new Vector3D(1, 0, 0), step);
                    e.Handled = true;
                    return;
                case System.Windows.Input.Key.Y:
                    _orbit.RotateScene(new Vector3D(0, 1, 0), step);
                    e.Handled = true;
                    return;
                case System.Windows.Input.Key.Z:
                    _orbit.RotateScene(new Vector3D(0, 0, 1), step);
                    e.Handled = true;
                    return;
                default:
                    return;
            }

            // The arrow cases used to fall through to an UpdateCamera() call at the end of this
            // method; each Nudge now refreshes the camera itself, so nothing is owed here except
            // marking the key handled.
            e.Handled = true;
        }

        private void View_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Focus stays here: it is the window's own focus, not part of the orbit's state.
            Focus();
            _orbit.BeginDrag(e.GetPosition(ViewBorder));
        }

        private void View_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _orbit.EndDrag();
        }

        private void View_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            // DragTo carries the _dragging guard, so a move that is not part of a drag is ignored
            // here exactly as it was when the check sat in this method.
            _orbit.DragTo(e.GetPosition(ViewBorder));
        }

        private void View_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            _orbit.Zoom(e.Delta);
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshPorts();

        private void RefreshPorts()
        {
            string selected = PortComboBox.Text;
            // Phase 3: the port list comes from the transport seam, not from SerialPort directly.
            // The App no longer references System.IO.Ports at all.
            PortComboBox.ItemsSource = _portProbe.ListPorts().Select(p => p.Name).Order().ToList();
            PortComboBox.Text = string.IsNullOrWhiteSpace(selected)
                ? (PortComboBox.Items.Contains(DefaultPort) ? DefaultPort : PortComboBox.Items.Count > 0 ? PortComboBox.Items[0]?.ToString() : DefaultPort)
                : selected;
        }

        private void Connect_Click(object sender, RoutedEventArgs e)
        {
            if (_stream is not null)
            {
                Disconnect();
                return;
            }

            if (string.IsNullOrWhiteSpace(PortComboBox.Text) || !int.TryParse(BaudTextBox.Text, out int baud) || baud <= 0)
            {
                StatusText.Text = "Invalid port or baud rate";
                return;
            }

            var stream = new SerialSensorStream();
            try
            {
                stream.FrameReceived += Stream_FrameReceived;
                stream.LineRejected += Stream_LineRejected;
                stream.StatusChanged += Stream_StatusChanged;

                // Armed BEFORE Open(): DataReceived can fire as soon as the port is open, so a capture
                // attached afterwards would miss the first burst — and the first burst is exactly where
                // a framing difference between device and replay would show up.
                if (CaptureCheckBox.IsChecked == true)
                {
                    StartCaptureFor(stream);
                }

                stream.Open(PortComboBox.Text, baud);

                _stream = stream;
                _imuFilter.Reset();
                _lastImuTimestamp = 0;
                _coalescer.Clear();
                _renderTimer.Start();
                ConnectButton.Content = "Disconnect";

                // Open raised "Connected to COM12" through Dispatcher.BeginInvoke, which lands after
                // anything this method sets synchronously - so the capture path has to be queued behind
                // it or it is overwritten before anyone can read it. The path has to be readable for the
                // whole session: Disconnect's report goes to a window that is already on its way out when
                // the app is closed rather than disconnected, and a capture whose location nobody knows
                // is a capture that gets thrown away.
                if (_capturePath is not null)
                {
                    Dispatcher.BeginInvoke(() => StatusText.Text = $"Connected - capturing to {_capturePath}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                // The stream was never handed to Disconnect(), so it is released right here.
                // Whether a failed Open() leaks the port is defect D5, inside SerialSensorStream:
                // it is preserved unchanged by this extraction and fixed in 3d's own commit.
                stream.Dispose();
                StatusText.Text = ex.Message;
            }
        }

        private void Disconnect()
        {
            ISensorStream? stream = _stream;
            _stream = null;
            long captured = 0;
            if (stream is not null)
            {
                // The count is read before Dispose, because Dispose is what closes the file and the
                // count is the only record of how much arrived. AutoFlush on the capture means the
                // count and the file agree.
                captured = stream is SerialSensorStream serial ? serial.CaptureCharacters : 0;

                stream.FrameReceived -= Stream_FrameReceived;
                stream.LineRejected -= Stream_LineRejected;
                stream.StatusChanged -= Stream_StatusChanged;
                stream.Dispose();
            }

            // Phase 4: the coalescer outlives the connection, so a frame that arrived just before the
            // port closed would otherwise be rendered by the next tick as if it belonged to the next
            // connection. Stopping the timer first means nothing drains it on the way out.
            _renderTimer.Stop();
            _coalescer.Clear();

            // The capture closes inside Dispose. Only a session that actually captured gets a
            // different status message, so the never-capturing path still reads exactly "Disconnected".
            string? capturePath = _capturePath;
            _capturePath = null;

            ConnectButton.Content = "Connect";
            StatusText.Text = capturePath is null
                ? "Disconnected"
                : $"Disconnected - captured {captured} characters to {capturePath}";
        }

        // Phase 3: the transport's own thread raises these (see ISensorStream's threading contract),
        // so every one of them marshals through Dispatcher exactly as Port_DataReceived did before
        // the seam existed. The obligation to marshal stayed with the consumer, not the stream.
        private void Stream_StatusChanged(object? sender, string status)
        {
            Dispatcher.BeginInvoke(() => StatusText.Text = status);
        }

        private void Stream_LineRejected(object? sender, string line)
        {
            string rejected = line.Length > 80 ? line[..80] : line;
            Dispatcher.BeginInvoke(() => StatusText.Text = $"Rejected line: {rejected}");
        }

        // Phase 4 (defect D2): this no longer marshals. It hands the frame to a one-slot coalescer
        // and returns, so the transport thread is never queued behind a render and the UI never holds
        // more than one frame of pending work. The Dispatcher.BeginInvoke that used to sit here is
        // what made latency grow with frame rate: each frame queued its own full render.
        // ISensorStream's threading contract still says the consumer marshals — it now does that by
        // not touching UI state here at all, and by rendering from a timer that already runs on the
        // UI thread.
        private void Stream_FrameReceived(object? sender, SensorFrame frame)
        {
            _coalescer.Write(frame);
        }

        private void RenderTimer_Tick(object? sender, EventArgs e)
        {
            // Exactly one frame per tick, which is the whole point: the render rate is a UI policy,
            // not a consequence of how fast the serial side happens to deliver.
            if (_coalescer.TryRead(out SensorFrame frame))
            {
                RenderFrame(frame);
            }
        }

        /// <summary>
        /// The body that used to run inside Dispatcher.BeginInvoke, unchanged except for where it runs.
        /// </summary>
        private void RenderFrame(SensorFrame frame)
        {
            _frames++;
            if (frame.HasImu)
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                double dt = _lastImuTimestamp == 0 ? 0 : System.Diagnostics.Stopwatch.GetElapsedTime(_lastImuTimestamp, now).TotalSeconds;
                _lastImuTimestamp = now;
                SetOrientation(_imuFilter.Update(frame.Imu.ToArray(), dt));
            }

            // The ToArray() copy exists because ZoneRenderer.UpdateFrame still takes int[].
            // Retargeting it to ReadOnlyMemory is Phase 5's remaining job; until then the hot path pays
            // one small allocation per frame, which is what it paid before too.
            int[] distances = frame.Distances.ToArray();
            _zones.UpdateFrame(distances, KeepMapCheckBox.IsChecked == true);
            ReadOnlySpan<int> imu = frame.Imu.Span;
            string imuText = frame.HasImu
                ? $" | gyro {imu[0]},{imu[1]},{imu[2]} accel {imu[3]},{imu[4]},{imu[5]} mag {imu[6]},{imu[7]},{imu[8]}"
                : string.Empty;

            // Phase 4's gate: the drop policy must be visible rather than mysterious. Without this
            // number, a coalescing UI that throws away 40 of 60 frames looks like a stuttering app
            // with no explanation attached to it.
            long dropped = _coalescer.DroppedFrames;
            string dropText = dropped > 0 ? $" | dropped {dropped}" : string.Empty;
            StatusText.Text = $"Frames: {_frames} ({distances.Length} zones, {distances.Count(d => d >= MinDistanceMm && d <= MaxDistanceMm)} visible, max {distances.Max()} mm){imuText}{dropText}";
        }

        private static TimeSpan RenderInterval(int hz) => TimeSpan.FromSeconds(1.0 / hz);

        private void RenderRate_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (RenderRateComboBox.SelectedItem is not int hz || hz <= 0)
            {
                return;
            }

            // DispatcherTimer re-reads Interval live; a running timer picks up the new rate on its
            // next tick, so the rate can be changed while connected.
            _renderTimer.Interval = RenderInterval(hz);
        }




        private void ShowGrid_Click(object sender, RoutedEventArgs e)
        {
            // D8, fixed. This used to read:
            //
            //     var root = (Model3DGroup)SensorVisual.Content;
            //     root.Children.Remove(_gridModel);
            //     root.Children.Insert(root.Children.Count - 1, _gridModel);
            //
            // The grid's position was computed as "one before the end of a collection owned by four
            // different concerns", which is how the rule "the transparent sphere must be last" got
            // enforced: by an index into somebody else's list. The grid now goes into the grid's own
            // layer, and no caller reaches into the viewport's content at all.
            if (ShowGridCheckBox.IsChecked == true)
            {
                _layers.ShowGrid(_gridModel);
            }
            else
            {
                _layers.HideGrid(_gridModel);
            }
        }

        private void KeepMap_Click(object sender, RoutedEventArgs e)
        {
            if (KeepMapCheckBox.IsChecked == true)
            {
                return;
            }

            _zones.ClearMap();
        }

        // ---------------------------------------------------------------------------------------
        // The raw capture (Phase 5). Opt-in, decided per connection, and never able to break the
        // connection it observes.
        // ---------------------------------------------------------------------------------------

        private void Capture_Click(object sender, RoutedEventArgs e) => ApplyCapture();

        /// <summary>
        /// Applies the Capture checkbox to whatever connection is live: checking it starts a capture,
        /// unchecking it closes the file. With no connection the choice is simply remembered and
        /// applied by Connect_Click, so arming capture before plugging the device in is the normal way
        /// to use this.
        /// </summary>
        private void ApplyCapture()
        {
            if (_stream is not SerialSensorStream serial)
            {
                StatusText.Text = CaptureCheckBox.IsChecked == true
                    ? "Capture armed: starts on Connect"
                    : "Capture off";
                return;
            }

            if (CaptureCheckBox.IsChecked == true)
            {
                StartCaptureFor(serial);
                return;
            }

            serial.StopCapture();
            _capturePath = null;
            StatusText.Text = "Capture stopped";
        }

        /// <summary>
        /// Starts a capture on a live stream. A capture that cannot be started is reported and the
        /// connection continues unchanged — deliberately swallowed here rather than allowed to reach
        /// Connect_Click's catch block, which would dispose the stream and end a session over a
        /// storage problem.
        /// </summary>
        private void StartCaptureFor(SerialSensorStream stream)
        {
            int.TryParse(BaudTextBox.Text, out int baud);
            string path = SerialSensorStream.DefaultCapturePath(PortComboBox.Text, baud);

            try
            {
                stream.StartCapture(path);
                _capturePath = path;
                StatusText.Text = $"Capturing to {path}";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _capturePath = null;
                StatusText.Text = $"Capture failed: {ex.Message}";
            }
        }


    }
}
