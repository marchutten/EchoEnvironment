using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using EchoEnvironment.Core;
using EchoEnvironment.Rendering;

namespace EchoEnvironment
{
    /// <summary>
    /// Displays VL53L5CX distance frames as points on a sphere around the sensor.
    ///
    /// PHASE 6b: this is the view. It owns the scene graph, the camera, the zone renderer, the render timer
    /// and the frame coalescer - everything about turning a frame into pixels on a UI thread - and it owns no
    /// application state and no policy. The status text, the port list, connect/disconnect, the orientation
    /// readout, the keep-map choice, the capture request and the render rate all live in MainViewModel, which
    /// is constructed from the SensorSource the composition root chose.
    ///
    /// WHY THE VIEW MODEL IS WIRED BY HAND AND NOT BY BINDING. MainViewModel raises PropertyChanged for the
    /// two strings the toolbar displays, and the handlers below are what consume them. ICommand is not used:
    /// it lives in PresentationCore, and ArchitectureInvariantTests.WpfConversionIsConfinedToOneType forbids
    /// any public type in this assembly other than WpfMath, Window and Application from naming a Core type and
    /// a WPF type in the same public surface. MainViewModel speaks Core and Hardware, so it may not speak WPF
    /// either. See that type's own comment for the full reasoning.
    ///
    /// WHY THE VIEW STILL HOLDS THE COALESCER AND THE TIMER. §2: "the *policy* (rate, drop counter) belongs on
    /// the view model; the *timer* is a UI-thread mechanism and can stay with the view." The coalescer's
    /// policy lives in Core and is unchanged; the window holds the instance because draining it is the tick,
    /// and the tick is a UI-thread act. The rate that sets the interval is the view model's.
    /// </summary>
    public partial class MainWindow : Window
    {
        // Phase 6b: the window declares no scene constants of its own. The field of view, the band a reading
        // has to fall inside to be drawn, and the map bucket count are MainViewModel's, and the window reads
        // them from there. Before 6b they were declared here and the status line's arithmetic declared them
        // again in effect, which is two numbers that could drift apart while every zone still rendered.

        // LineHalfWidth moved to ScenePrimitives with AddLine, and SceneUnitsPerMm moved to ZoneRenderer with
        // UpdateFrame: both describe how a distance becomes a drawn thing, which is not something the window
        // needs to know.

        private readonly Model3DGroup _boardGroup = new();

        /// <summary>
        /// Phase 5 commit 4 (defect D8): the sensor viewport's layers, named and ordered by SceneLayers.
        /// This replaced the flat Model3DGroup named _points plus a map group local, whose meaning depended
        /// on the order things were appended to it.
        /// </summary>
        private readonly SceneLayers _layers;

        private readonly MatrixTransform3D _orientation = new(Matrix3D.Identity);

        /// <summary>
        /// The fixed mounting rotation of the DOF sensor (Core's Mat3), composed ahead of the live IMU
        /// orientation in SetOrientation. Identity unless --mount configured it, so the default is unchanged.
        /// </summary>
        private readonly Mat3 _mount = Mat3.Identity;

        /// <summary>
        /// Phase 6b: the orientation *matrix* is filtered here, on the UI thread, because the transform the
        /// scene rotates by is a WPF object and the filter's output has to reach it. The angles the readout
        /// prints are derived from the same Mat3 by MainViewModel, which never sees a Matrix3D.
        /// </summary>
        private readonly OrientationFilter _imuFilter = new();

        private long _lastImuTimestamp;

        /// <summary>
        /// The application state and policy this view draws (MIGRATION_PLAN 6b). Assigned in the constructor
        /// rather than by initialiser because the window needs it before it can populate the toolbar.
        /// </summary>
        private readonly MainViewModel _viewModel;

        /// <summary>
        /// Phase 5 commit 2: the zone models, the heat-grid cells, the map dictionary, the bucket geometries
        /// and the map throttle moved to ZoneRenderer; the camera angles, the drag state and the scene
        /// rotation moved to OrbitCameraController. Both fields are assigned in the constructor rather than by
        /// initialiser, because both need XAML-named elements, which do not exist until InitializeComponent has
        /// run.
        /// </summary>
        private readonly ZoneRenderer _zones;
        private readonly OrbitCameraController _orbit;

        /// <summary>
        /// Phase 5 commit 3 (defect D3): the shared frozen brushes and materials the renderer reuses across
        /// frames. A field rather than a static so the cache is per window, matching BrushCache's own
        /// reasoning about not sharing mutable state across a process.
        /// </summary>
        private readonly BrushCache _brushes = new();

        // Phase 4 (defect D2): frames arrive on the transport thread and are drained by this timer on the UI
        // thread, one per tick. The rate is a declared setting in the toolbar, so frame rate is a UI decision
        // instead of a consequence of the baud rate. 6b moved the *rate* to MainViewModel; the timer stayed
        // here because a DispatcherTimer is a UI-thread mechanism.
        private readonly FrameCoalescer _coalescer = new();
        private readonly DispatcherTimer _renderTimer = new();

        private readonly GeometryModel3D _gridModel = MeshFactory.CreateGridModel(3.98, 0.5);

        /// <summary>
        /// Builds the view for one chosen sensor source (MIGRATION_PLAN 6c) and the state that source implies
        /// (6b).
        ///
        /// The parameter is a SensorSource and not an ISensorStream on purpose: the connection lifecycle is
        /// create-on-connect / dispose-on-disconnect, so MainViewModel must be able to build a stream on every
        /// Connect and dispose it on every Disconnect. Injecting a ready-made stream would break the second
        /// Connect. The view model is not a second constructor parameter because it is derived from the source
        /// one-for-one; taking both would let a caller pair a view model with a different source.
        /// </summary>
        public MainWindow(SensorSource source)
        {
            ArgumentNullException.ThrowIfNull(source);
            _viewModel = new MainViewModel(source);

            // The fixed DOF-sensor mounting offset, configured at launch via --mount. Precomputed to a
            // matrix once; SetOrientation composes it with the live IMU orientation on every frame.
            _mount = source.Mount.ToMatrix();

            InitializeComponent();

            // Both collaborators take XAML-named elements, so neither can exist before InitializeComponent.
            // They are created before the scene graph because the view transforms below come from the orbit
            // controller.
            _orbit = new OrbitCameraController(Camera, SensorVisual, ViewBorder);

            // D8 (MIGRATION_PLAN 5c): the scene's layers are named and ordered by SceneLayers, once, in one
            // constructor. What used to sit here was a flat group built by appending - marker, map, zones,
            // then the transparent sphere "added last" with a comment explaining that WPF blends transparent
            // models over what was drawn before - while ShowGrid_Click inserted the grid at Children.Count - 1
            // of that same collection.
            //
            // The order that comment recorded is still the order that matters, and it is now guaranteed by
            // construction instead of by an insertion sequence: the transparent reference sphere is the last
            // layer. The order *between* the opaque layers (grid, map, marker, zones) is now declared by role
            // rather than by the sequence the old constructor happened to use, because WPF depth-tests opaque
            // models and the listing order does not decide which is in front.
            _layers = new SceneLayers(MeshFactory.CreateSensorMarker(), MeshFactory.CreateReferenceSphere(4.0));

            // The view transform goes on the zone group, not on the sensor layer: the marker sits still while
            // the zone fan rotates, exactly as it did when the marker was a separate child of the flat root.
            _layers.Zones.Group.Transform = _orbit.CreateViewTransform(false, _orientation);
            _zones = new ZoneRenderer(_layers.Zones.Group, _layers.Map.Group, HeatGrid, _brushes, MainViewModel.MinDistanceMm, MainViewModel.MaxDistanceMm, MainViewModel.FieldOfViewDegrees, MainViewModel.MapBuckets);

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

            // Phase 4: the render rate is a UI policy, not a side effect of how fast the serial side happens
            // to deliver (defect D2). The combo box is populated in code rather than in XAML so the numbers
            // and RenderInterval share one source - and since 6b that source is the view model's.
            foreach (int hz in MainViewModel.RenderRateChoices)
            {
                RenderRateComboBox.Items.Add(hz);
            }

            RenderRateComboBox.SelectedItem = MainViewModel.DefaultRenderRateHz;
            _renderTimer.Interval = RenderInterval(MainViewModel.DefaultRenderRateHz);
            _renderTimer.Tick += RenderTimer_Tick;

            _viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.Status))
                {
                    StatusText.Text = _viewModel.Status;
                }
                else if (e.PropertyName == nameof(MainViewModel.OrientationReadout))
                {
                    OrientationText.Text = _viewModel.OrientationReadout;
                }
            };

            // The transport raised these from its own thread. Queueing them through the Dispatcher is what
            // preserves the order Connect relies on: Open raises "Connected to COM14" asynchronously, so the
            // capture-path message has to land behind it or it is overwritten before anyone can read it.
            _viewModel.TransportAnnounced += (_, message) => Dispatcher.BeginInvoke(() => StatusText.Text = message);

            _viewModel.FrameArrived += (_, frame) => _coalescer.Write(frame);

            RefreshPorts();
            Closed += (_, _) => Disconnect();

            StatusText.Text = _viewModel.Status;

            // Raw capture is a property of the serial transport. For --sim and --replay the checkbox is
            // disabled rather than left clickable, because a control that accepts a request nothing can honour
            // reports a failure for a mistake the user was never allowed to make.
            if (!_viewModel.CaptureSupported)
            {
                CaptureCheckBox.IsEnabled = false;
                CaptureCheckBox.IsChecked = false;
            }
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

            // The arrow cases used to fall through to an UpdateCamera() call at the end of this method; each
            // Nudge now refreshes the camera itself, so nothing is owed here except marking the key handled.
            e.Handled = true;
        }

        private void View_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Focus stays here: it is the window's own focus, not part of the orbit's state.
            Focus();
            _orbit.BeginDrag(e.GetPosition(ViewBorder));
        }

        private void View_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e) => _orbit.EndDrag();

        private void View_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            // DragTo carries the _dragging guard, so a move that is not part of a drag is ignored here exactly
            // as it was when the check sat in this method.
            _orbit.DragTo(e.GetPosition(ViewBorder));
        }

        private void View_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e) => _orbit.Zoom(e.Delta);

        private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshPorts();

        /// <summary>
        /// Asks the view model for the port list and shows it. Phase 3 moved the question to the transport
        /// seam; 6b moved it to the view model. The App still does not reference System.IO.Ports at all.
        /// </summary>
        private void RefreshPorts()
        {
            _viewModel.RefreshPorts(PortComboBox.Text);
            PortComboBox.ItemsSource = _viewModel.Ports;
            PortComboBox.Text = _viewModel.SelectedPort;
        }

        /// <summary>
        /// Connect, or disconnect if already connected. The button is one control for both directions and the
        /// state is "the view model has a live stream", exactly as it was when this method owned the stream.
        ///
        /// WHAT 6b CHANGED: the decision, the stream's lifecycle and the status line are MainViewModel's.
        /// What is left here is the part only a view can do - start and stop the render timer, reset the IMU
        /// filter and the coalescer that belong to it, and relabel the button. The reset on connect is
        /// deliberately still here: the filter and the coalescer are this window's state, and a new session
        /// must not inherit either.
        ///
        /// ONE ORDERING DIFFERENCE, STATED RATHER THAN HIDDEN. The original Disconnect disposed the stream
        /// first and stopped the render timer afterwards. Disconnect below stops the timer first. The
        /// difference is not observable from the UI thread - no tick can run in the middle of this handler -
        /// but it is a real change in which side is quiesced first, and stopping the drain before the
        /// transport is torn down is the safer of the two orders: the transport thread can still be inside
        /// FrameArrived when Dispose runs, and a stopped timer means nothing renders what lands after it.
        /// </summary>
        private void Connect_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.IsConnected)
            {
                Disconnect();
                return;
            }

            if (!_viewModel.Connect(PortComboBox.Text, BaudTextBox.Text))
            {
                // The view model refused - invalid port or baud, or the open failed - and it has already
                // written the reason to the status line. Nothing was started, so nothing has to be stopped.
                return;
            }

            _imuFilter.Reset();
            _lastImuTimestamp = 0;
            _coalescer.Clear();
            _renderTimer.Start();
            ConnectButton.Content = "Disconnect";

            // The capture path has to be readable for the whole session: Disconnect's report goes to a window
            // that is already on its way out when the app is closed rather than disconnected, and a capture
            // whose location nobody knows is a capture that gets thrown away. Queued behind Open()'s own
            // status message, exactly as it was when this method owned the connection.
            if (_viewModel.CapturePath is not null)
            {
                Dispatcher.BeginInvoke(() => StatusText.Text = $"Connected - capturing to {_viewModel.CapturePath}");
            }
        }

        private void Disconnect()
        {
            // Quiesce the drain before tearing down the transport. The transport thread can still be inside
            // FrameArrived when Dispose runs, and a stopped timer means nothing renders what lands after it.
            StopRendering();
            _viewModel.Disconnect();
            ConnectButton.Content = "Connect";
        }

        private void StopRendering()
        {
            // Phase 4: the coalescer outlives the connection, so a frame that arrived just before the port
            // closed would otherwise be rendered by the next tick as if it belonged to the next connection.
            // Stopping the timer first means nothing drains it on the way out.
            _renderTimer.Stop();
            _coalescer.Clear();
        }

        private void RenderTimer_Tick(object? sender, EventArgs e)
        {
            // Exactly one frame per tick, which is the whole point: the render rate is a UI policy, not a
            // consequence of how fast the serial side happens to deliver.
            if (_coalescer.TryRead(out SensorFrame frame))
            {
                RenderFrame(frame);
            }
        }

        /// <summary>
        /// The body that used to run inside Dispatcher.BeginInvoke, unchanged except for where it runs.
        ///
        /// NO COPIES ANY MORE (MIGRATION_PLAN §12.8 item 4). SensorFrame stores the distances and the IMU as
        /// ReadOnlyMemory, and both consumers take ReadOnlySpan, so the frame's own storage is read in place
        /// and the hot path allocates nothing for these two values.
        ///
        /// WHY THAT IS SAFE. A span is a window onto somebody else's storage and cannot outlive it. Here the
        /// storage is the SensorFrame parameter, which is alive for the whole method, and nothing downstream
        /// keeps the span: OrientationFilter.Update indexes imu[0..8] into three Vec3 copies, and
        /// ZoneRenderer.UpdateFrame reads distances only for the duration of its own call. Neither stores a
        /// span in a field, and neither could.
        ///
        /// WHAT 6b MOVED OUT. The status-line arithmetic - the visible-zone count, the maximum, the IMU
        /// figures, the drop count - is now MainViewModel.FrameRendered, because it is application state and
        /// not drawing. The text produced is identical, which is what
        /// TheStatusLineLoopAgreesWithTheLinqItReplaced and the guards in ArchitectureInvariantTests are for.
        /// </summary>
        private void RenderFrame(SensorFrame frame)
        {
            if (frame.HasImu)
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                double dt = _lastImuTimestamp == 0 ? 0 : System.Diagnostics.Stopwatch.GetElapsedTime(_lastImuTimestamp, now).TotalSeconds;
                _lastImuTimestamp = now;
                Span<int> imu = stackalloc int[9];
                ImuAxisMap.ToSensorFrame(frame.Imu.Span, imu);
                SetOrientation(_imuFilter.Update(imu, dt));
                ImuChartView.Add(frame.Imu.Span);
            }

            _zones.UpdateFrame(frame.Distances.Span, _viewModel.KeepMap);
            _viewModel.FrameRendered(frame, _coalescer.DroppedFrames);
        }

        /// <summary>
        /// Sets the sensor viewport (e.g. from a 9 DOF sensor) as a sensor-to-world rotation matrix.
        ///
        /// Since 6b this is private. §2: "SetOrientation stops being a public method on a Window." Nothing
        /// outside this class ever called it - the only call site in the whole solution was RenderFrame - so
        /// narrowing it costs nothing and removes the window's last public behaviour.
        ///
        /// WHY IT STILL EXISTS HERE AT ALL. This converts Core's Mat3 to WPF's Matrix3D exactly once, and the
        /// transform it sets is the one the scene rotates by. The angle readout that used to sit in this
        /// method is now MainViewModel's, because the angles are a property of the orientation, not of
        /// whichever matrix type the UI happens to render with.
        /// </summary>
        private void SetOrientation(Mat3 rotation)
        {
            // Compose the fixed mounting offset with the live IMU orientation. Row-vector convention:
            // p * (M_mount * M_imu) applies the mount first, then the IMU. _mount is identity unless
            // --mount was given, so the default path is byte-for-byte what it was before this line existed.
            _orientation.Matrix = (_mount * rotation).ToMatrix3D();
        }

        private static TimeSpan RenderInterval(int hz) => TimeSpan.FromSeconds(1.0 / hz);

        private void RenderRate_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (RenderRateComboBox.SelectedItem is not int hz || !_viewModel.SetRenderRate(hz))
            {
                return;
            }

            // DispatcherTimer re-reads Interval live; a running timer picks up the new rate on its next tick,
            // so the rate can be changed while connected.
            _renderTimer.Interval = RenderInterval(_viewModel.RenderRate);
        }

        private void ShowGrid_Click(object sender, RoutedEventArgs e)
        {
            // D8, fixed. This used to read:
            //
            //     var root = (Model3DGroup)SensorVisual.Content;
            //     root.Children.Remove(_gridModel);
            //     root.Children.Insert(root.Children.Count - 1, _gridModel);
            //
            // The grid's position was computed as "one before the end of a collection owned by four different
            // concerns", which is how the rule "the transparent sphere must be last" got enforced: by an index
            // into somebody else's list. The grid now goes into the grid's own layer, and no caller reaches
            // into the viewport's content at all.
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
            // The choice is the view model's; clearing the accumulated map is an action on this window's own
            // renderer, which is why SetKeepMap reports that a clear is owed instead of doing it.
            if (_viewModel.SetKeepMap(KeepMapCheckBox.IsChecked == true))
            {
                _zones.ClearMap();
            }
        }

        // ---------------------------------------------------------------------------------------
        // The raw capture (Phase 5). Opt-in, decided per connection, and never able to break the
        // connection it observes. 6b moved the decision and the file handling to MainViewModel; what is
        // left here is reading the checkbox.
        // ---------------------------------------------------------------------------------------

        private void Capture_Click(object sender, RoutedEventArgs e)
            => _viewModel.SetCaptureRequested(CaptureCheckBox.IsChecked == true, PortComboBox.Text, BaudTextBox.Text);
    }
}
