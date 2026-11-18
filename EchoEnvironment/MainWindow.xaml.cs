using System.IO;
using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;

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
        private const double LineHalfWidth = 0.003;
        private const double SceneUnitsPerMm = 1.0 / 1000.0;

        private readonly Model3DGroup _boardGroup = new();
        private readonly Model3DGroup _points = new();
        private readonly MatrixTransform3D _orientation = new(Matrix3D.Identity);
        private readonly OrientationFilter _imuFilter = new();
        private long _lastImuTimestamp;
        private readonly List<GeometryModel3D> _zoneModels = [];
        private readonly MeshGeometry3D _cubeMesh = CreateCubeMesh(0.02);
        private readonly GeometryModel3D _lineModel = CreateLineModel();
        private readonly System.Text.StringBuilder _rxBuffer = new();
        private const string DefaultPort = "COM12";
        private SerialPort? _port;
        private int _frames;

        private static int IndexOfNewline(System.Text.StringBuilder sb)
        {
            for (int i = 0; i < sb.Length; i++)
            {
                if (sb[i] is '\n' or '\r')
                {
                    return i;
                }
            }

            return -1;
        }

        public MainWindow()
        {
            InitializeComponent();

            var root = new Model3DGroup();
            root.Children.Add(CreateSensorMarker());
            _points.Transform = _orientation;
            root.Children.Add(_points);
            // Added last: WPF blends transparent models over what was drawn before, so it must not come first.
            root.Children.Add(CreateReferenceSphere(4.0));
            SensorVisual.Content = root;

            var boardRoot = new Model3DGroup();
            boardRoot.Children.Add(CreateWorldAxes());
            _boardGroup.Transform = _orientation;
            _boardGroup.Children.Add(CreateBoardModel());
            AddAxes(_boardGroup, 0.5, 0.01);
            boardRoot.Children.Add(_boardGroup);
            BoardVisual.Content = boardRoot;

            UpdateCamera();
            PreviewKeyDown += Window_PreviewKeyDown;
            RefreshPorts();
            Closed += (_, _) => Disconnect();
        }

        /// <summary>
        /// Sets the sensor viewport (e.g. from a 9 DOF sensor) as a sensor-to-world rotation matrix.
        /// </summary>
        public void SetOrientation(Matrix3D rotation)
        {
            _orientation.Matrix = rotation;

            // Forward is the sensor's -Z axis; world axes are X east, Y up, Z south.
            double heading = Math.Atan2(-rotation.M31, rotation.M33) * 180.0 / Math.PI;
            if (heading < 0)
            {
                heading += 360;
            }

            double pitch = Math.Asin(Math.Clamp(-rotation.M32, -1, 1)) * 180.0 / Math.PI;
            double roll = Math.Atan2(rotation.M12, rotation.M22) * 180.0 / Math.PI;
            OrientationText.Text = $"Orientation\nHeading: {heading,6:0.0}°\nPitch:   {pitch,6:0.0}°\nRoll:    {roll,6:0.0}°";
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
            PortComboBox.ItemsSource = SerialPort.GetPortNames().Order().ToList();
            PortComboBox.Text = string.IsNullOrWhiteSpace(selected)
                ? (PortComboBox.Items.Contains(DefaultPort) ? DefaultPort : PortComboBox.Items.Count > 0 ? PortComboBox.Items[0]?.ToString() : DefaultPort)
                : selected;
        }

        private void Connect_Click(object sender, RoutedEventArgs e)
        {
            if (_port is not null)
            {
                Disconnect();
                return;
            }

            if (string.IsNullOrWhiteSpace(PortComboBox.Text) || !int.TryParse(BaudTextBox.Text, out int baud) || baud <= 0)
            {
                StatusText.Text = "Invalid port or baud rate";
                return;
            }

            try
            {
                var port = new SerialPort(PortComboBox.Text, baud) { NewLine = "\n", ReadTimeout = 1000 };
                port.DataReceived += Port_DataReceived;
                port.Open();
                _port = port;
                _imuFilter.Reset();
                _lastImuTimestamp = 0;
                ConnectButton.Content = "Disconnect";
                StatusText.Text = $"Connected to {port.PortName}";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                StatusText.Text = ex.Message;
            }
        }

        private void Disconnect()
        {
            SerialPort? port = _port;
            _port = null;
            if (port is not null)
            {
                port.DataReceived -= Port_DataReceived;
                port.Dispose();
            }

            ConnectButton.Content = "Connect";
            StatusText.Text = "Disconnected";
        }

        private void Port_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            if (sender is not SerialPort port)
            {
                return;
            }

            try
            {
                _rxBuffer.Append(port.ReadExisting());
                int newline;
                while ((newline = IndexOfNewline(_rxBuffer)) >= 0)
                {
                    string line = _rxBuffer.ToString(0, newline);
                    _rxBuffer.Remove(0, newline + 1);
                    if (SensorFrameParser.TryParse(line, out int[] distances, out int[]? imu))
                    {
                        Dispatcher.BeginInvoke(() =>
                        {
                            _frames++;
                            if (imu is not null)
                            {
                                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                                double dt = _lastImuTimestamp == 0 ? 0 : System.Diagnostics.Stopwatch.GetElapsedTime(_lastImuTimestamp, now).TotalSeconds;
                                _lastImuTimestamp = now;
                                SetOrientation(_imuFilter.Update(imu, dt));
                            }
                            UpdateFrame(distances);
                            string imuText = imu is null
                                ? string.Empty
                                : $" | gyro {imu[0]},{imu[1]},{imu[2]} accel {imu[3]},{imu[4]},{imu[5]} mag {imu[6]},{imu[7]},{imu[8]}";
                            StatusText.Text = $"Frames: {_frames} ({distances.Length} zones, {distances.Count(d => d >= MinDistanceMm && d <= MaxDistanceMm)} visible, max {distances.Max()} mm){imuText}";
                        });
                    }
                    else if (!string.IsNullOrWhiteSpace(line))
                    {
                        string rejected = line.Length > 80 ? line[..80] : line;
                        Dispatcher.BeginInvoke(() => StatusText.Text = $"Rejected line: {rejected}");
                    }
                }

                if (_rxBuffer.Length > 10000)
                {
                    _rxBuffer.Clear();
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                // Port closed while reading.
            }
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
                cell.Background = new SolidColorBrush(valid ? DistanceColor(mm) : Color.FromRgb(50, 50, 50));
                ((TextBlock)cell.Child).Text = valid ? mm.ToString() : "-";
            }
        }

        private void UpdateFrame(int[] distances)
        {
            int size = distances.Length == 64 ? 8 : 4;
            UpdateHeatmap(distances, size);
            EnsureZoneModels(distances.Length);
            double step = FieldOfViewDegrees / size;

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
                    ((DiffuseMaterial)model.Material).Brush = new SolidColorBrush(DistanceColor(mm));
                    AddLine(lines, position);
                }
            }

            _lineModel.Geometry = lines;
        }

        // WPF 3D has no line primitive, so each line is two crossed thin quads.
        private static void AddLine(MeshGeometry3D mesh, Vector3D end)
        {
            Vector3D dir = end;
            dir.Normalize();
            Vector3D side = Vector3D.CrossProduct(dir, Math.Abs(dir.Y) > 0.99 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0));
            side.Normalize();
            Vector3D up = Vector3D.CrossProduct(dir, side);
            up.Normalize();

            foreach (Vector3D offset in new[] { side * LineHalfWidth, up * LineHalfWidth })
            {
                int i = mesh.Positions.Count;
                mesh.Positions.Add(new Point3D(-offset.X, -offset.Y, -offset.Z));
                mesh.Positions.Add(new Point3D(offset.X, offset.Y, offset.Z));
                mesh.Positions.Add(new Point3D(end.X + offset.X, end.Y + offset.Y, end.Z + offset.Z));
                mesh.Positions.Add(new Point3D(end.X - offset.X, end.Y - offset.Y, end.Z - offset.Z));
                int[] tri = [0, 1, 2, 0, 2, 3];
                foreach (int t in tri)
                {
                    mesh.TriangleIndices.Add(i + t);
                }
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

        private static Color DistanceColor(double mm)
        {
            double t = Math.Clamp(mm / MaxDistanceMm, 0, 1);
            byte r = (byte)(255 * (1 - t));
            byte b = (byte)(255 * t);
            return Color.FromRgb(r, (byte)(200 * (1 - Math.Abs(2 * t - 1))), b);
        }

        private static MeshGeometry3D CreateBoxMesh(Point3D center, double sx, double sy, double sz)
        {
            MeshGeometry3D unit = CreateCubeMesh(1.0);
            var mesh = new MeshGeometry3D();
            foreach (Point3D p in unit.Positions)
            {
                mesh.Positions.Add(new Point3D(center.X + p.X * sx, center.Y + p.Y * sy, center.Z + p.Z * sz));
            }

            foreach (int i in unit.TriangleIndices)
            {
                mesh.TriangleIndices.Add(i);
            }

            mesh.Freeze();
            return mesh;
        }

        private static GeometryModel3D CreateColoredBox(Point3D center, double sx, double sy, double sz, Color color)
        {
            var material = new DiffuseMaterial(new SolidColorBrush(color));
            var model = new GeometryModel3D(CreateBoxMesh(center, sx, sy, sz), material) { BackMaterial = material };
            model.Freeze();
            return model;
        }

        private static GeometryModel3D CreateBoardModel()
        {
            // Sensor looks along -Z, so the front edge of the board is at -Z.
            return CreateColoredBox(new Point3D(0, 0, 0), 0.4, 0.03, 0.25, Color.FromRgb(20, 110, 60));
        }

        private static void AddAxes(Model3DGroup group, double length, double width)
        {
            double h = length / 2;
            group.Children.Add(CreateColoredBox(new Point3D(h, 0, 0), length, width, width, Colors.Red));
            group.Children.Add(CreateColoredBox(new Point3D(0, 0, -h), width, width, length, Colors.DeepSkyBlue));
            group.Children.Add(CreateColoredBox(new Point3D(0, h, 0), width, length, width, Colors.LimeGreen));
            group.Children.Add(CreateAxisLabel("X", new Point3D(length + 0.08, 0, 0), Colors.Red));
            group.Children.Add(CreateAxisLabel("Y", new Point3D(0, 0, -(length + 0.08)), Colors.DeepSkyBlue));
            group.Children.Add(CreateAxisLabel("Z", new Point3D(0, length + 0.08, 0), Colors.LimeGreen));
        }

        private static GeometryModel3D CreateAxisLabel(string text, Point3D center, Color color)
        {
            var host = new Grid { Width = 64, Height = 64 };
            host.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 52,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(color),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            host.Measure(new Size(64, 64));
            host.Arrange(new Rect(0, 0, 64, 64));

            const double s = 0.06;
            var mesh = new MeshGeometry3D();
            mesh.Positions.Add(new Point3D(center.X - s, center.Y - s, center.Z));
            mesh.Positions.Add(new Point3D(center.X + s, center.Y - s, center.Z));
            mesh.Positions.Add(new Point3D(center.X + s, center.Y + s, center.Z));
            mesh.Positions.Add(new Point3D(center.X - s, center.Y + s, center.Z));
            mesh.TextureCoordinates.Add(new Point(0, 1));
            mesh.TextureCoordinates.Add(new Point(1, 1));
            mesh.TextureCoordinates.Add(new Point(1, 0));
            mesh.TextureCoordinates.Add(new Point(0, 0));
            foreach (int i in new[] { 0, 1, 2, 0, 2, 3 })
            {
                mesh.TriangleIndices.Add(i);
            }

            var material = new EmissiveMaterial(new VisualBrush(host));
            return new GeometryModel3D(mesh, material) { BackMaterial = material };
        }

        private static Model3DGroup CreateWorldAxes()
        {
            var group = new Model3DGroup();
            double length = 1.5;
            double h = length / 2;
            group.Children.Add(CreateColoredBox(new Point3D(h, 0, 0), length, 0.004, 0.004, Color.FromRgb(120, 40, 40)));
            group.Children.Add(CreateColoredBox(new Point3D(0, h, 0), 0.004, length, 0.004, Color.FromRgb(40, 120, 40)));
            group.Children.Add(CreateColoredBox(new Point3D(0, 0, h), 0.004, 0.004, length, Color.FromRgb(40, 80, 120)));
            return group;
        }

        private static GeometryModel3D CreateLineModel()
        {
            var material = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(200, 200, 200)));
            return new GeometryModel3D { Material = material, BackMaterial = material };
        }

        private static GeometryModel3D CreateSensorMarker()
        {
            var material = new DiffuseMaterial(Brushes.Orange);
            var model = new GeometryModel3D(CreateCubeMesh(0.015), material) { BackMaterial = material };
            model.Freeze();
            return model;
        }

        private static GeometryModel3D CreateReferenceSphere(double radius)
        {
            var mesh = new MeshGeometry3D();
            const int rings = 12;
            const int segments = 24;

            for (int i = 0; i <= rings; i++)
            {
                double phi = Math.PI * i / rings;
                for (int j = 0; j <= segments; j++)
                {
                    double theta = 2 * Math.PI * j / segments;
                    mesh.Positions.Add(new Point3D(
                        radius * Math.Sin(phi) * Math.Cos(theta),
                        radius * Math.Cos(phi),
                        radius * Math.Sin(phi) * Math.Sin(theta)));
                }
            }

            for (int i = 0; i < rings; i++)
            {
                for (int j = 0; j < segments; j++)
                {
                    int a = i * (segments + 1) + j;
                    int b = a + segments + 1;
                    mesh.TriangleIndices.Add(a);
                    mesh.TriangleIndices.Add(b);
                    mesh.TriangleIndices.Add(a + 1);
                    mesh.TriangleIndices.Add(a + 1);
                    mesh.TriangleIndices.Add(b);
                    mesh.TriangleIndices.Add(b + 1);
                }
            }

            var brush = new SolidColorBrush(Color.FromArgb(28, 120, 160, 255));
            var material = new DiffuseMaterial(brush);
            var model = new GeometryModel3D(mesh, material) { BackMaterial = material };
            model.Freeze();
            return model;
        }

        private static MeshGeometry3D CreateCubeMesh(double size)
        {
            double h = size / 2;
            var mesh = new MeshGeometry3D();
            Point3D[] p =
            [
                new(-h, -h, -h), new(h, -h, -h), new(h, h, -h), new(-h, h, -h),
                new(-h, -h, h), new(h, -h, h), new(h, h, h), new(-h, h, h)
            ];
            foreach (Point3D point in p)
            {
                mesh.Positions.Add(point);
            }

            int[] tri =
            [
                0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7,
                0, 1, 5, 0, 5, 4, 3, 7, 6, 3, 6, 2,
                1, 2, 6, 1, 6, 5, 0, 4, 7, 0, 7, 3
            ];
            foreach (int i in tri)
            {
                mesh.TriangleIndices.Add(i);
            }

            mesh.Freeze();
            return mesh;
        }
    }
}
