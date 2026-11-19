using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using EchoEnvironment.Core;

namespace EchoEnvironment.Rendering;

/// <summary>
/// The zone visuals: one model per sensor zone, the lines from the sensor to each valid zone, the
/// 2D heat grid on the right panel, and the accumulated map.
///
/// Extracted from MainWindow at Phase 5 commit 2 (MIGRATION_PLAN 5 maps EnsureZoneModels,
/// UpdateFrame, UpdateHeatmap, AddToMap and RenderMapIfDue here, plus Core/WorldMap.cs for the map
/// arithmetic). Bodies are copied element-for-element; the only arithmetic that moved elsewhere is
/// the azimuth/elevation/bucket computation, which is now WorldMap.Add.
///
/// Unlike MeshFactory and ScenePrimitives, this type is stateful: it holds the models across frames
/// and throttles map rendering on a stopwatch. That is why commit 1 left it in MainWindow. "Verbatim"
/// is good evidence for a static function; for a stateful object the evidence has to be that the
/// state transitions are the same, which is what ZoneRendererTests now asserts.
///
/// WHY THE MAP IS NOT EXPOSED. WorldMap lives in Core. A public ZoneRenderer member of type WorldMap
/// would name a Core type and a WPF type in the same public surface, which is the definition
/// WpfConversionIsConfinedToOneType uses for "converts Core to WPF" - and the guard exempts only
/// WpfMath and UI types. Rather than widen the exemption, the map stays a private field and the
/// public surface names only WPF and primitives: MainWindow says ClearMap(), never sees a WorldMap.
/// The invariant survives by changing the design, not by relaxing the rule.
///
/// The visual groups are passed in, not created here. MainWindow owns the visual tree - which model
/// group sits where in the scene graph is the window's decision, and 5c is about to restructure
/// exactly that. This class fills groups it is handed.
/// </summary>
public sealed class ZoneRenderer
{
    /// <summary>Edge length of a zone cube, in scene units. Was MainWindow's inline CreateCubeMesh(0.02).</summary>
    private const double CubeSize = 0.02;

    /// <summary>Scale applied to the cube mesh when stamping it into a map bucket.</summary>
    private const double MapCubeScale = 0.5;

    /// <summary>Map geometry is rebuilt at most this often, in milliseconds, regardless of frame rate.</summary>
    private const long MapRenderIntervalMs = 150;

    /// <summary>Cell background for a reading outside the valid range.</summary>
    private static readonly Color InvalidCellColor = Color.FromRgb(50, 50, 50);

    /// <summary>
    /// Millimetres to scene units. Moved here with UpdateFrame: it is the scale that turns a
    /// distance into a position in the scene, which is a rendering constant, not something the window
    /// needs to know.
    /// </summary>
    private const double SceneUnitsPerMm = 1.0 / 1000.0;

    private readonly Model3DGroup _zoneGroup;
    private readonly UniformGrid _heatGrid;
    private readonly GeometryModel3D _lineModel;
    private readonly MeshGeometry3D _cubeMesh;
    private readonly List<GeometryModel3D> _zoneModels = [];
    private readonly GeometryModel3D[] _mapModels;
    private readonly WorldMap _map;
    private readonly double _minDistanceMm;
    private readonly double _maxDistanceMm;
    private readonly double _fieldOfViewDegrees;
    private long _lastMapRender;

    public ZoneRenderer(
        Model3DGroup zoneGroup,
        Model3DGroup mapGroup,
        UniformGrid heatGrid,
        double minDistanceMm,
        double maxDistanceMm,
        double fieldOfViewDegrees,
        int mapBuckets)
    {
        _zoneGroup = zoneGroup;
        _heatGrid = heatGrid;
        _minDistanceMm = minDistanceMm;
        _maxDistanceMm = maxDistanceMm;
        _fieldOfViewDegrees = fieldOfViewDegrees;

        _cubeMesh = MeshFactory.CreateCubeMesh(CubeSize);
        _lineModel = MeshFactory.CreateLineModel();

        _map = new WorldMap(maxDistanceMm, mapBuckets);
        _mapModels = new GeometryModel3D[mapBuckets];
        for (int b = 0; b < mapBuckets; b++)
        {
            // One material per bucket, created once. The bucket index is the colour, so the ramp is
            // sampled at bucket boundaries rather than per reading.
            var material = new DiffuseMaterial(new SolidColorBrush(ColorScale.For(b * maxDistanceMm / (mapBuckets - 1), maxDistanceMm).ToColor()));
            _mapModels[b] = new GeometryModel3D { Material = material, BackMaterial = material };
            mapGroup.Children.Add(_mapModels[b]);
        }
    }

    /// <summary>
    /// Drops every accumulated reading and empties the bucket geometries. Called when the Keep-map
    /// checkbox is turned off.
    /// </summary>
    public void ClearMap()
    {
        _map.Clear();
        foreach (GeometryModel3D model in _mapModels)
        {
            model.Geometry = null;
        }
    }

    /// <summary>
    /// Repositions and recolours every zone model, rebuilds the zone lines, repaints the heat grid,
    /// and - when keepMap is true - accumulates the readings into the world map.
    ///
    /// keepMap is a parameter, not a field. In MainWindow it was read from KeepMapCheckBox.IsChecked
    /// inside UpdateFrame. Handing ZoneRenderer the CheckBox instead would put a UI control owned by
    /// the window inside a rendering class, which is a worse coupling than the one it removes: the
    /// renderer would then depend on the window's element names. A bool carries the same information
    /// with no reference at all, and it is what makes UpdateFrame callable from a test.
    /// </summary>
    public void UpdateFrame(int[] distances, bool keepMap)
    {
        int size = distances.Length == 64 ? 8 : 4;
        UpdateHeatmap(distances, size);
        EnsureZoneModels(distances.Length);
        double step = _fieldOfViewDegrees / size;
        Matrix3D viewMatrix = _zoneGroup.Transform.Value;

        var lines = new MeshGeometry3D();

        for (int row = 0; row < size; row++)
        {
            for (int col = 0; col < size; col++)
            {
                int index = row * size + col;
                GeometryModel3D model = _zoneModels[index];
                int mm = distances[index];

                if (mm < _minDistanceMm || mm > _maxDistanceMm)
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
                ((DiffuseMaterial)model.Material).Brush = new SolidColorBrush(ColorScale.For(mm, _maxDistanceMm).ToColor());
                ScenePrimitives.AddLine(lines, position);
                if (keepMap)
                {
                    Point3D world = viewMatrix.Transform(new Point3D(position.X, position.Y, position.Z));
                    _map.Add(new Vec3(world.X, world.Y, world.Z), mm);
                }
            }
        }

        _lineModel.Geometry = lines;
        if (keepMap)
        {
            RenderMapIfDue();
        }
    }

    private void UpdateHeatmap(int[] distances, int size)
    {
        if (_heatGrid.Children.Count != distances.Length)
        {
            _heatGrid.Rows = size;
            _heatGrid.Columns = size;
            _heatGrid.Children.Clear();
            for (int i = 0; i < distances.Length; i++)
            {
                _heatGrid.Children.Add(new Border
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
            var cell = (Border)_heatGrid.Children[i];
            int mm = distances[i];
            bool valid = mm >= _minDistanceMm && mm <= _maxDistanceMm;
            cell.Background = new SolidColorBrush(valid ? ColorScale.For(mm, _maxDistanceMm).ToColor() : InvalidCellColor);
            ((TextBlock)cell.Child).Text = valid ? mm.ToString() : "-";
        }
    }

    private void EnsureZoneModels(int count)
    {
        if (_zoneModels.Count == count)
        {
            return;
        }

        _zoneGroup.Children.Clear();
        _zoneGroup.Children.Add(_lineModel);
        _zoneModels.Clear();
        for (int i = 0; i < count; i++)
        {
            var material = new DiffuseMaterial(Brushes.White);
            var model = new GeometryModel3D(_cubeMesh, material) { BackMaterial = material };
            _zoneModels.Add(model);
            _zoneGroup.Children.Add(model);
        }
    }

    private void RenderMapIfDue()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastMapRender != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(_lastMapRender, now).TotalMilliseconds < MapRenderIntervalMs)
        {
            return;
        }

        _lastMapRender = now;
        var meshes = new MeshGeometry3D[_mapModels.Length];
        for (int b = 0; b < _mapModels.Length; b++)
        {
            meshes[b] = new MeshGeometry3D();
        }

        foreach ((Vec3 position, int bucket) in _map.Entries.Values)
        {
            MeshGeometry3D mesh = meshes[bucket];
            int start = mesh.Positions.Count;
            foreach (Point3D p in _cubeMesh.Positions)
            {
                mesh.Positions.Add(new Point3D(position.X + p.X * MapCubeScale, position.Y + p.Y * MapCubeScale, position.Z + p.Z * MapCubeScale));
            }

            foreach (int i in _cubeMesh.TriangleIndices)
            {
                mesh.TriangleIndices.Add(start + i);
            }
        }

        for (int b = 0; b < _mapModels.Length; b++)
        {
            meshes[b].Freeze();
            _mapModels[b].Geometry = meshes[b];
        }
    }
}
