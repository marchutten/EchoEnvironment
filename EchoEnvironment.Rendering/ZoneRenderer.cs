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

    /// <summary>
    /// One frozen zero-scale shared by every zone a frame hides, where the code used to build a new
    /// ScaleTransform3D per hidden zone per frame. Frozen because it is shared: a Freezable referenced by
    /// many visuals must not be mutable, or mutating one zone's hiding would hide them all.
    /// </summary>
    private static readonly ScaleTransform3D HiddenScale = CreateHiddenScale();

    private readonly Model3DGroup _zoneGroup;
    private readonly UniformGrid _heatGrid;
    private readonly GeometryModel3D _lineModel;
    private readonly MeshGeometry3D _cubeMesh;
    private readonly List<GeometryModel3D> _zoneModels = [];

    /// <summary>
    /// One TranslateTransform3D per zone, created in EnsureZoneModels and mutated every frame. Held in a
    /// list parallel to _zoneModels so zone index, model and transform stay in step.
    /// </summary>
    private readonly List<TranslateTransform3D> _zoneTransforms = [];

    private readonly GeometryModel3D[] _mapModels;
    private readonly WorldMap _map;
    private readonly BrushCache _brushes;
    private readonly double _minDistanceMm;
    private readonly double _maxDistanceMm;
    private readonly double _fieldOfViewDegrees;
    private long _lastMapRender;

    public ZoneRenderer(
        Model3DGroup zoneGroup,
        Model3DGroup mapGroup,
        UniformGrid heatGrid,
        BrushCache brushes,
        double minDistanceMm,
        double maxDistanceMm,
        double fieldOfViewDegrees,
        int mapBuckets)
    {
        ArgumentNullException.ThrowIfNull(brushes);

        _zoneGroup = zoneGroup;
        _heatGrid = heatGrid;
        _brushes = brushes;
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
            material.Freeze();
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
                    // One frozen scale shared by every hidden zone, where this used to allocate a new
                    // ScaleTransform3D per invalid reading per frame. It stays a ScaleTransform3D, so a
                    // zone hidden this way is still distinguishable from one that was moved.
                    model.Transform = HiddenScale;
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

                // Mutate the zone's own transform instead of replacing it (MIGRATION_PLAN 5b). The old
                // line built a new TranslateTransform3D for all 64 zones every frame.
                TranslateTransform3D transform = _zoneTransforms[index];
                transform.OffsetX = position.X;
                transform.OffsetY = position.Y;
                transform.OffsetZ = position.Z;
                model.Transform = transform;

                // The old line mutated the material's Brush property, which required casting the model's
                // Material to DiffuseMaterial and allocated a new brush per zone per frame. Assigning a
                // shared frozen material is both cheaper and free of the cast.
                //
                // THE TRAP THIS COMMENT EXISTS FOR: the code it replaces mutated a single material object
                // that was referenced by both Material and BackMaterial, so front and back faces always
                // showed the same colour. Replacing only Material would leave BackMaterial painted with
                // the previous frame's colour. Both are assigned here, from the same cache entry.
                DiffuseMaterial material = _brushes.MaterialFor(mm, _maxDistanceMm);
                model.Material = material;
                model.BackMaterial = material;
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

        // The cells themselves were already reused between frames (pinned by
        // HeatGridCellsAreReusedBetweenFrames). What was not reused was their background: a fresh
        // SolidColorBrush per cell per frame, 64 of them, even when a cell showed the same distance as
        // the frame before and needed the identical colour. Now a cell that does not change its distance
        // keeps the same frozen brush instance across frames.
        for (int i = 0; i < distances.Length; i++)
        {
            var cell = (Border)_heatGrid.Children[i];
            int mm = distances[i];
            bool valid = mm >= _minDistanceMm && mm <= _maxDistanceMm;
            cell.Background = valid
                ? _brushes.BrushFor(mm, _maxDistanceMm)
                : _brushes.BrushFor(InvalidCellColor);
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
        _zoneTransforms.Clear();
        for (int i = 0; i < count; i++)
        {
            var material = new DiffuseMaterial(Brushes.White);
            var model = new GeometryModel3D(_cubeMesh, material) { BackMaterial = material };
            _zoneModels.Add(model);

            // One transform per zone, created here and mutated by UpdateFrame for the rest of the
            // renderer's life. Deliberately NOT frozen: a frozen transform cannot be mutated, and the
            // whole point is to mutate it instead of replacing it.
            _zoneTransforms.Add(new TranslateTransform3D());
            _zoneGroup.Children.Add(model);
        }
    }

    /// <summary>
    /// The shared hidden scale, built once. See the HiddenScale field for why one instance is enough.
    /// </summary>
    private static ScaleTransform3D CreateHiddenScale()
    {
        var scale = new ScaleTransform3D(0, 0, 0);
        scale.Freeze();
        return scale;
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
