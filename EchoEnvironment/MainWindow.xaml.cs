using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

        // LineHalfWidth moved to ScenePrimitives with AddLine: it is the thickness of a drawn line,
        // which is a rendering constant, not something the window needs to know.
        private const double SceneUnitsPerMm = 1.0 / 1000.0;

        private readonly Model3DGroup _boardGroup = new();
        private readonly Model3DGroup _points = new();
        private readonly MatrixTransform3D _orientation = new(Matrix3D.Identity);
        private readonly OrientationFilter _imuFilter = new();
        private long _lastImuTimestamp;
        private readonly List<GeometryModel3D> _zoneModels = [];
        private readonly MeshGeometry3D _cubeMesh = MeshFactory.CreateCubeMesh(0.02);
        private readonly GeometryModel3D _lineModel = MeshFactory.CreateLineModel();
        private const string DefaultPort = "COM12";
        private ISensorStream? _stream;

        /// <summary>
        /// Never opened. Exists only so RefreshPorts asks the transport seam which ports exist,
        /// instead of calling SerialPort directly — which is what lets the App drop its
        /// System.IO.Ports reference entirely.
        /// </summary>
        private readonly SerialSensorStream _portProbe = new();

        private int _frames;
        private const int MapBuckets = 16;
        private readonly Dictionary<int, (Point3D Position, int Bucket)> _map = [];
        private readonly Model3DGroup _mapGroup = new();
        private readonly GeometryModel3D[] _mapModels = new GeometryModel3D[MapBuckets];
        private long _lastMapRender;
        private readonly GeometryModel3D _gridModel = MeshFactory.CreateGridModel(3.98, 0.5);

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

            var root = new Model3DGroup();
            root.Children.Add(MeshFactory.CreateSensorMarker());
            _points.Transform = CreateViewTransform(false);
            for (int b = 0; b < MapBuckets; b++)
            {
                var material = new DiffuseMaterial(new SolidColorBrush(ColorScale.For(b * MaxDistanceMm / (MapBuckets - 1), MaxDistanceMm).ToColor()));
                _mapModels[b] = new GeometryModel3D { Material = material, BackMaterial = material };
                _mapGroup.Children.Add(_mapModels[b]);
            }

            root.Children.Add(_mapGroup);
            root.Children.Add(_points);
            // Added last: WPF blends transparent models over what was drawn before, so it must not come first.
            // Phase 5c replaces this positional rule with named layers; until then the comment is the
            // only thing recording why the order is not free.
            root.Children.Add(MeshFactory.CreateReferenceSphere(4.0));
            SensorVisual.Content = root;

            var boardRoot = new Model3DGroup();
            boardRoot.Children.Add(ScenePrimitives.CreateWorldAxes());
            _boardGroup.Transform = CreateViewTransform(true);
            _boardGroup.Children.Add(MeshFactory.CreateBoardModel());
            ScenePrimitives.AddAxes(_boardGroup, 0.5, 0.01);
            boardRoot.Children.Add(_boardGroup);
            BoardVisual.Content = boardRoot;

            UpdateCamera();
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
            OrientationText.Text = $"Orientation\nHeading: {heading,6:0.0}°\nPitch:   {roll,6:0.0}°\nRoll:    {pitch,6:0.0}°";
        }

        private Transform3DGroup CreateViewTransform(bool remapModel)
        {
            var group = new Transform3DGroup();
            if (remapModel)
            {
                group.Children.Add(new MatrixTransform3D(new Matrix3D(1, 0, 0, 0, 0, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1)));
            }
            else
            {
                group.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), 180)));
            }

            group.Children.Add(_orientation);
            group.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), 90)));
            return group;
        }

        private double _camYaw = 20;
        private double _camPitch = 20;
        private double _camDistance = 5;
        private Point _lastMouse;
        private bool _dragging;

        private void UpdateCamera()
        {
            double yaw = _camYaw * Math.PI / 180.0;
            double pitch = _camPitch * Math.PI / 180.0;
            var position = new Vector3D(
                _camDistance * Math.Cos(pitch) * Math.Sin(yaw),
                _camDistance * Math.Sin(pitch),
                _camDistance * Math.Cos(pitch) * Math.Cos(yaw));
            Camera.Position = new Point3D(position.X, position.Y, position.Z);
            Camera.LookDirection = -position;
        }

        private Quaternion _viewRotation = Quaternion.Identity;

        private void RotateView(Vector3D axis, double degrees)
        {
            bool shift = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;
            var delta = new Quaternion(axis, shift ? -degrees : degrees);
            _viewRotation = delta * _viewRotation;
            _viewRotation.Normalize();
            SensorVisual.Transform = new RotateTransform3D(new QuaternionRotation3D(_viewRotation));
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
                    _camYaw -= step;
                    break;
                case System.Windows.Input.Key.Right:
                    _camYaw += step;
                    break;
                case System.Windows.Input.Key.Up:
                    _camPitch = Math.Clamp(_camPitch + step, -89, 89);
                    break;
                case System.Windows.Input.Key.Down:
                    _camPitch = Math.Clamp(_camPitch - step, -89, 89);
                    break;
                case System.Windows.Input.Key.Space:
                    _viewRotation = Quaternion.Identity;
                    SensorVisual.Transform = Transform3D.Identity;
                    _camYaw = 20;
                    _camPitch = 20;
                    _camDistance = 5;
                    UpdateCamera();
                    e.Handled = true;
                    return;
                case System.Windows.Input.Key.X:
                    RotateView(new Vector3D(1, 0, 0), step);
                    e.Handled = true;
                    return;
                case System.Windows.Input.Key.Y:
                    RotateView(new Vector3D(0, 1, 0), step);
                    e.Handled = true;
                    return;
                case System.Windows.Input.Key.Z:
                    RotateView(new Vector3D(0, 0, 1), step);
                    e.Handled = true;
                    return;
                default:
                    return;
            }

            UpdateCamera();
            e.Handled = true;
        }

        private void View_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _dragging = true;
            Focus();
            _lastMouse = e.GetPosition(ViewBorder);
            ViewBorder.CaptureMouse();
        }

        private void View_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _dragging = false;
            ViewBorder.ReleaseMouseCapture();
        }

        private void View_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_dragging)
            {
                return;
            }

            Point p = e.GetPosition(ViewBorder);
            _camYaw -= (p.X - _lastMouse.X) * 0.5;
            _camPitch = Math.Clamp(_camPitch + (p.Y - _lastMouse.Y) * 0.5, -89, 89);
            _lastMouse = p;
            UpdateCamera();
        }

        private void View_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            _camDistance = Math.Clamp(_camDistance * (e.Delta > 0 ? 0.9 : 1.1), 0.5, 30);
            UpdateCamera();
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
                stream.Open(PortComboBox.Text, baud);

                _stream = stream;
                _imuFilter.Reset();
                _lastImuTimestamp = 0;
                _coalescer.Clear();
                _renderTimer.Start();
                ConnectButton.Content = "Disconnect";
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
            if (stream is not null)
            {
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

            ConnectButton.Content = "Connect";
            StatusText.Text = "Disconnected";
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

            // The two ToArray() copies exist because UpdateFrame and UpdateHeatmap still take
            // int[]. Retargeting them to ReadOnlyMemory is Phase 5's job; until then the hot
            // path pays two small allocations per frame, which is what it paid before too.
            int[] distances = frame.Distances.ToArray();
            UpdateFrame(distances);
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

        private void UpdateHeatmap(int[] distances, int size)
        {
            if (HeatGrid.Children.Count != distances.Length)
            {
                HeatGrid.Rows = size;
                HeatGrid.Columns = size;
                HeatGrid.Children.Clear();
                for (int i = 0; i < distances.Length; i++)
                {
                    HeatGrid.Children.Add(new Border
                    {
                        Margin = new Thickness(1),
                        Child = new TextBlock
                        {
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                            FontSize = size == 8 ? 10 : 14,
                            Foreground = Brushes.White
                        }
                    });
                }
            }

            for (int i = 0; i < distances.Length; i++)
            {
                var cell = (Border)HeatGrid.Children[i];
                int mm = distances[i];
                bool valid = mm >= MinDistanceMm && mm <= MaxDistanceMm;
                cell.Background = new SolidColorBrush(valid ? ColorScale.For(mm, MaxDistanceMm).ToColor() : Color.FromRgb(50, 50, 50));
                ((TextBlock)cell.Child).Text = valid ? mm.ToString() : "-";
            }
        }

        private void UpdateFrame(int[] distances)
        {
            int size = distances.Length == 64 ? 8 : 4;
            UpdateHeatmap(distances, size);
            EnsureZoneModels(distances.Length);
            double step = FieldOfViewDegrees / size;
            bool keepMap = KeepMapCheckBox.IsChecked == true;
            Matrix3D viewMatrix = _points.Transform.Value;

            var lines = new MeshGeometry3D();

            for (int row = 0; row < size; row++)
            {
                for (int col = 0; col < size; col++)
                {
                    int index = row * size + col;
                    GeometryModel3D model = _zoneModels[index];
                    int mm = distances[index];

                    if (mm < MinDistanceMm || mm > MaxDistanceMm)
                    {
                        model.Transform = new ScaleTransform3D(0, 0, 0);
                        continue;
                    }

                    double az = (col - (size - 1) / 2.0) * step * Math.PI / 180.0;
                    double el = ((size - 1) / 2.0 - row) * step * Math.PI / 180.0;

                    // Sensor looks along -Z; X is right, Y is up.
                    double r = mm * SceneUnitsPerMm;
                    var position = new Vector3D(
                        r * Math.Cos(el) * Math.Sin(az),
                        r * Math.Sin(el),
                        -r * Math.Cos(el) * Math.Cos(az));

                    model.Transform = new TranslateTransform3D(position);
                    ((DiffuseMaterial)model.Material).Brush = new SolidColorBrush(ColorScale.For(mm, MaxDistanceMm).ToColor());
                    ScenePrimitives.AddLine(lines, position);
                    if (keepMap)
                    {
                        AddToMap(viewMatrix.Transform(new Point3D(position.X, position.Y, position.Z)), mm);
                    }
                }
            }

            _lineModel.Geometry = lines;
            if (keepMap)
            {
                RenderMapIfDue();
            }
        }

        // Keeps only the latest reading per whole degree of world azimuth/elevation.
        private void AddToMap(Point3D world, int mm)
        {
            double r = new Vector3D(world.X, world.Y, world.Z).Length;
            int az = (int)Math.Round(Math.Atan2(world.X, -world.Z) * 180.0 / Math.PI);
            int el = (int)Math.Round(Math.Asin(Math.Clamp(world.Y / r, -1, 1)) * 180.0 / Math.PI);
            int bucket = (int)Math.Round(Math.Clamp(mm / MaxDistanceMm, 0, 1) * (MapBuckets - 1));
            _map[(az + 180) * 181 + el + 90] = (world, bucket);
        }

        private void RenderMapIfDue()
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_lastMapRender != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(_lastMapRender, now).TotalMilliseconds < 150)
            {
                return;
            }

            _lastMapRender = now;
            var meshes = new MeshGeometry3D[MapBuckets];
            for (int b = 0; b < MapBuckets; b++)
            {
                meshes[b] = new MeshGeometry3D();
            }

            foreach ((Point3D position, int bucket) in _map.Values)
            {
                MeshGeometry3D mesh = meshes[bucket];
                int start = mesh.Positions.Count;
                foreach (Point3D p in _cubeMesh.Positions)
                {
                    mesh.Positions.Add(new Point3D(position.X + p.X * 0.5, position.Y + p.Y * 0.5, position.Z + p.Z * 0.5));
                }

                foreach (int i in _cubeMesh.TriangleIndices)
                {
                    mesh.TriangleIndices.Add(start + i);
                }
            }

            for (int b = 0; b < MapBuckets; b++)
            {
                meshes[b].Freeze();
                _mapModels[b].Geometry = meshes[b];
            }
        }

        private void ShowGrid_Click(object sender, RoutedEventArgs e)
        {
            var root = (Model3DGroup)SensorVisual.Content;
            root.Children.Remove(_gridModel);
            if (ShowGridCheckBox.IsChecked == true)
            {
                root.Children.Insert(root.Children.Count - 1, _gridModel);
            }
        }

        private void KeepMap_Click(object sender, RoutedEventArgs e)
        {
            if (KeepMapCheckBox.IsChecked == true)
            {
                return;
            }

            _map.Clear();
            foreach (GeometryModel3D model in _mapModels)
            {
                model.Geometry = null;
            }
        }

        private void EnsureZoneModels(int count)
        {
            if (_zoneModels.Count == count)
            {
                return;
            }

            _points.Children.Clear();
            _points.Children.Add(_lineModel);
            _zoneModels.Clear();
            for (int i = 0; i < count; i++)
            {
                var material = new DiffuseMaterial(Brushes.White);
                var model = new GeometryModel3D(_cubeMesh, material) { BackMaterial = material };
                _zoneModels.Add(model);
                _points.Children.Add(model);
            }
        }

    }
}
