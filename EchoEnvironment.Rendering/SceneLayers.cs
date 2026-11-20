using System.Windows.Media.Media3D;

namespace EchoEnvironment.Rendering;

/// <summary>
/// The scene's layers, named, and the order between them declared in one place (defect D8,
/// MIGRATION_PLAN 5c).
///
/// THE DEFECT. Before this type existed, the sensor viewport was one flat Model3DGroup whose meaning
/// depended on the order things happened to be appended to it:
///
///   root.Children.Add(sensorMarker); root.Children.Add(mapGroup); root.Children.Add(zones);
///   root.Children.Add(referenceSphere);   // "Added last: WPF blends transparent models over what was
///                                         //  drawn before, so it must not come first."
///
/// and ShowGrid_Click did this to that same collection:
///
///   root.Children.Insert(root.Children.Count - 1, _gridModel);
///
/// That arithmetic is the defect. The grid's position was computed as "one before the end of a
/// collection owned by four different concerns", so the rule "the transparent sphere must be last" was
/// enforced by an index into somebody else's list. Add a fifth group and the grid silently moves;
/// append anything after the sphere and the blending rule breaks without a single line of the code that
/// states it changing. The order was a property of insertion sequence, not of the design, and the only
/// record of why it mattered was a comment on the line that performed the insertion.
///
/// WHAT CHANGED. Each concern now owns a layer, the layers are appended to the root exactly once in
/// SceneLayers' constructor in the order the design declares, and toggling the grid adds a model to the
/// grid's own layer. No caller computes an index, and no caller casts SensorVisual.Content back to a
/// Model3DGroup in order to reach into it.
///
/// WHY THE SPHERE IS STILL LAST, AND WHY THAT IS THE ONLY ORDER THAT IS LOAD-BEARING. WPF blends
/// transparent models over whatever was already drawn. The reference sphere is the only transparent
/// model in the sensor viewport - Color.FromArgb(28, 120, 160, 255) in MeshFactory - while the grid, the
/// map buckets, the marker and the zone models are all opaque, so depth testing decides which of them is
/// in front regardless of the order they are listed in. That is why the layers can be ordered by role
/// (grid, map, sensor, reference) rather than in the exact sequence the old constructor happened to use
/// (marker, map, zones, grid, sphere): the two differ in the order of opaque models only, and the one
/// constraint WPF actually imposes - transparent last - is preserved by construction. SceneLayersPutThe
/// TransparentReferenceLayerLast asserts the constraint rather than the accident.
///
/// WHY THE MARKER AND THE ZONES ARE ONE LAYER BUT NOT ONE GROUP. The old code applied the orbit's view
/// transform to the zone group and left the sensor marker untransformed, so the marker sits still while
/// the zone fan rotates. A layer is a grouping of *purpose*; a transform is a property of a group. Both
/// are preserved here by making the marker and the zone group two children of the sensor layer, with the
/// transform on the zone group only. Collapsing them into one group would move the marker with the fan,
/// which is a behaviour change this commit must not make.
///
/// WHY THIS LIVES IN RENDERING AND NOT IN THE WINDOW. The plan's sketch put the three layer fields on
/// MainWindow. It is wrong twice over. First, it names "world axes" as back-layer content, and the world
/// axes are not in this viewport at all - they are in boardRoot, in the second Viewport3D, which has its
/// own tree and its own transform. Second, a window holding four groups and the arithmetic that orders
/// them is the same defect wearing a field declaration: the next ShowGrid-like toggle would still be
/// computing a position. Putting the layers here makes the order a property of one constructor in the
/// rendering layer, which is where "what is drawn in front of what" belongs. SceneLayers names only WPF
/// types, so it is not a Core-to-WPF conversion point and WpfConversionIsConfinedToOneType is
/// unaffected.
/// </summary>
public sealed class SceneLayers
{
    /// <summary>The floor the scene sits on. Drawn first.</summary>
    public const string BackLayerName = "back";

    /// <summary>Accumulated map buckets, one model per distance bucket.</summary>
    public const string MapLayerName = "map";

    /// <summary>The sensor itself and what it measures: the marker, and the zone fan.</summary>
    public const string SensorLayerName = "sensor";

    /// <summary>The transparent reference sphere. Drawn last, because WPF blends transparent over opaque.</summary>
    public const string ReferenceLayerName = "reference";

    private readonly Model3DGroup _root = new();

    public SceneLayers(GeometryModel3D sensorMarker, GeometryModel3D referenceSphere)
    {
        ArgumentNullException.ThrowIfNull(sensorMarker);
        ArgumentNullException.ThrowIfNull(referenceSphere);

        Back = new SceneLayer(BackLayerName);
        Map = new SceneLayer(MapLayerName);
        Zones = new SceneLayer("zones");

        // The marker is untransformed and the zone group carries the orbit's view transform, so they
        // cannot share one group. See the class comment.
        Sensor = new SceneLayer(SensorLayerName, sensorMarker, Zones.Group);
        Reference = new SceneLayer(ReferenceLayerName, referenceSphere);

        // The one place the order between layers is decided. Nothing else in the codebase computes a
        // position in this collection.
        _root.Children.Add(Back.Group);
        _root.Children.Add(Map.Group);
        _root.Children.Add(Sensor.Group);
        _root.Children.Add(Reference.Group);
    }

    /// <summary>The group to hand to the viewport's ModelVisual3D.Content.</summary>
    public Model3DGroup Root => _root;

    /// <summary>Grid layer.</summary>
    public SceneLayer Back { get; }

    /// <summary>Map-bucket layer. This is the group ZoneRenderer fills.</summary>
    public SceneLayer Map { get; }

    /// <summary>The zone fan. The orbit's view transform goes on this group, not on the sensor layer.</summary>
    public SceneLayer Zones { get; }

    /// <summary>Marker and zones together, by purpose.</summary>
    public SceneLayer Sensor { get; }

    /// <summary>Transparent reference sphere. Always the last child of the root.</summary>
    public SceneLayer Reference { get; }

    /// <summary>
    /// Puts the grid in the back layer, and only in the back layer. Idempotent: showing a grid that is
    /// already shown does not add it twice.
    /// </summary>
    public void ShowGrid(GeometryModel3D grid)
    {
        ArgumentNullException.ThrowIfNull(grid);

        if (!Back.Group.Children.Contains(grid))
        {
            Back.Group.Children.Add(grid);
        }
    }

    /// <summary>Removes the grid from the back layer. Idempotent, so hiding an already-hidden grid is safe.</summary>
    public void HideGrid(GeometryModel3D grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        Back.Group.Children.Remove(grid);
    }
}

/// <summary>
/// A named layer: a Model3DGroup plus the name that says what it is for.
///
/// Model3DGroup has no Name property, so a "named layer" has to carry its own name for the naming to be
/// observable at all. Without it the layers are four anonymous groups and the defect this type exists to
/// fix has only been renamed, not fixed: a reader still cannot tell which group is which without reading
/// the constructor. The name is also what a failure message can point at.
///
/// Initial content is typed Model3D and not GeometryModel3D, because a layer's content is sometimes
/// itself a group: the sensor layer holds the marker (a GeometryModel3D) and the zone group (a
/// Model3DGroup) side by side, because the marker must stay untransformed while the zone group carries
/// the orbit's view transform. Typing the parameter as GeometryModel3D would make that composition
/// unexpressible, which is the whole reason the marker and the zones are one layer but not one group.
/// </summary>
public sealed class SceneLayer
{
    public SceneLayer(string name, params Model3D[] initialContent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(initialContent);

        Name = name;
        Group = new Model3DGroup();

        foreach (Model3D model in initialContent)
        {
            ArgumentNullException.ThrowIfNull(model);
            Group.Children.Add(model);
        }
    }

    public string Name { get; }

    public Model3DGroup Group { get; }
}
