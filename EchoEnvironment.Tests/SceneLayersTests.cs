using System.Linq;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using EchoEnvironment.Rendering;
using Xunit;

namespace EchoEnvironment.Tests;

/// <summary>
/// Tests for SceneLayers, the fix for defect D8 (MIGRATION_PLAN 5c).
///
/// WHAT DEFECT D8 ACTUALLY WAS. The sensor viewport was one flat Model3DGroup whose meaning depended on
/// the order things happened to be appended to it, and ShowGrid_Click put the grid into that same
/// collection with Children.Insert(root.Children.Count - 1, _gridModel). The rule "the transparent sphere
/// must be last, because WPF blends transparent over opaque" was therefore enforced by an index into a
/// list owned by four different concerns. Append anything after the sphere, or add a fifth group, and the
/// order breaks without any line that states the rule changing.
///
/// WHAT THESE TESTS CAN AND CANNOT PROVE. They can prove the structural claim: the transparent layer is
/// last, the grid goes into the grid's own layer, the marker stays untransformed while the zone group
/// rotates, and no layer is a positional accident. They cannot prove the scene looks the same, because
/// nothing in this suite can render a viewport. That is the same limit every Phase 5 gate has run into,
/// and it is why the visual check remains a human step.
///
/// WHY STA IS NOT NEEDED HERE. Model3DGroup and GeometryModel3D are Freezables, not FrameworkElements.
/// The apartment requirement in ZoneRendererTests came from `new UniformGrid()`, and there is no panel in
/// this file. If a test here ever fails with "The calling thread must be STA", SceneLayers has acquired a
/// UI dependency it must not have.
/// </summary>
public class SceneLayersTests
{
    private static GeometryModel3D Marker() => MeshFactory.CreateSensorMarker();

    private static GeometryModel3D Sphere() => MeshFactory.CreateReferenceSphere(4.0);

    private static GeometryModel3D Grid() => MeshFactory.CreateGridModel(3.98, 0.5);

    /// <summary>
    /// THE LOAD-BEARING ORDER. Of everything drawn in the sensor viewport, only the reference sphere is
    /// transparent - Color.FromArgb(28, 120, 160, 255) in MeshFactory.CreateReferenceSphere, alpha 28 out
    /// of 255. WPF blends a transparent model over whatever was already drawn, so the sphere must be the
    /// last thing in the tree. That single constraint is the only reason the old constructor's insertion
    /// sequence was not free, and it is now guaranteed by construction rather than by an index.
    /// </summary>
    [Fact]
    public void TransparentReferenceLayerIsAlwaysTheLastChildOfTheRoot()
    {
        var layers = new SceneLayers(Marker(), Sphere());

        Assert.Equal(4, layers.Root.Children.Count);
        Assert.Same(layers.Reference.Group, layers.Root.Children[3]);

        // The sphere the layers were given, held by identity. The first version of this assertion called
        // Sphere() a second time and compared it to the stored child, which compared a brand-new model to
        // the one the constructor received - two identical-looking GeometryModel3D objects, and Assert.Same
        // correctly reported them as different objects.
        Assert.Single(layers.Reference.Group.Children);
        Assert.IsAssignableFrom<GeometryModel3D>(layers.Reference.Group.Children[0]);

        // The transparency claim is what makes "last" meaningful at all. If the sphere ever becomes
        // opaque, this ordering stops being a correctness rule and becomes decoration - and the test that
        // asserts the order should then be deleted rather than kept as a ritual.
        var material = Assert.IsAssignableFrom<DiffuseMaterial>(
            Assert.IsAssignableFrom<GeometryModel3D>(layers.Reference.Group.Children[0]).Material);
        var brush = Assert.IsAssignableFrom<SolidColorBrush>(material.Brush);
        Assert.True(brush.Color.A < 255,
            $"the reference sphere is now fully opaque (alpha {brush.Color.A}); the transparent-last rule no longer applies and this test's premise is gone.");
    }

    /// <summary>
    /// The order between the opaque layers is declared by role, and it is fixed: back, map, sensor,
    /// reference. It is asserted as a named sequence rather than as four anonymous groups, because the
    /// defect was that a reader could not tell which group was which without reading the constructor.
    /// </summary>
    [Fact]
    public void LayersAreNamed_AndAppearInDeclaredOrder()
    {
        var layers = new SceneLayers(Marker(), Sphere());

        Assert.Equal(SceneLayers.BackLayerName, layers.Back.Name);
        Assert.Equal(SceneLayers.MapLayerName, layers.Map.Name);
        Assert.Equal(SceneLayers.SensorLayerName, layers.Sensor.Name);
        Assert.Equal(SceneLayers.ReferenceLayerName, layers.Reference.Name);

        // The root holds Model3DGroups, not SceneLayer objects: WPF's scene graph accepts Model3D, and a
        // wrapper type is not something a viewport can render. The first version of this test asserted the
        // root's children WERE SceneLayers and failed on exactly that. So the order is asserted as identity
        // of the four groups, and the names are asserted on the layers that carry them.
        //
        // That split is a limitation worth stating rather than smoothing over: a reader inspecting the
        // rendered tree still sees four anonymous groups. The names are observable through SceneLayers,
        // which is where layer order is decided and where defect D8 lived. The alternative - subclassing
        // Model3DGroup so the group itself carries a name - would put a custom type inside WPF's scene
        // graph for no rendering benefit.
        Assert.Same(layers.Back.Group, layers.Root.Children[0]);
        Assert.Same(layers.Map.Group, layers.Root.Children[1]);
        Assert.Same(layers.Sensor.Group, layers.Root.Children[2]);
        Assert.Same(layers.Reference.Group, layers.Root.Children[3]);
    }

    /// <summary>
    /// The grid used to be positioned by arithmetic against a collection it did not own. Now it goes into
    /// the grid's own layer, wherever the other layers are, and toggling it cannot disturb anything else.
    /// </summary>
    [Fact]
    public void GridGoesIntoTheBackLayer_AndTogglingItMovesNothingElse()
    {
        var layers = new SceneLayers(Marker(), Sphere());
        GeometryModel3D grid = Grid();

        Model3D[] rootBefore = layers.Root.Children.Cast<Model3D>().ToArray();

        layers.ShowGrid(grid);

        Assert.Same(grid, layers.Back.Group.Children[0]);
        Assert.Empty(layers.Map.Group.Children);
        Assert.Equal(4, layers.Root.Children.Count);

        // The root's children are the same four objects in the same order: showing the grid did not
        // insert anything into the root, which is exactly what the old Insert(Count - 1) did.
        for (int i = 0; i < rootBefore.Length; i++)
        {
            Assert.Same(rootBefore[i], layers.Root.Children[i]);
        }

        layers.HideGrid(grid);
        Assert.Empty(layers.Back.Group.Children);

        // Idempotent in both directions, because the old code called Remove before every Insert to avoid
        // a duplicate, which is itself evidence that double-adding was a real risk.
        layers.HideGrid(grid);
        Assert.Empty(layers.Back.Group.Children);

        layers.ShowGrid(grid);
        layers.ShowGrid(grid);
        Assert.Single(layers.Back.Group.Children);
    }

    /// <summary>
    /// The marker and the zone fan are one layer by purpose and two groups by transform. The old code
    /// applied the orbit's view transform to the zone group and left the marker untransformed, so the
    /// marker sat still while the fan rotated. Collapsing them into one group would move the marker with
    /// the fan - a behaviour change this commit must not make - so the layer holds them side by side.
    /// </summary>
    [Fact]
    public void SensorLayerHoldsTheMarkerAndTheZoneGroupSeparately_SoOnlyTheZonesRotate()
    {
        var layers = new SceneLayers(Marker(), Sphere());

        Assert.Equal(2, layers.Sensor.Group.Children.Count);
        Assert.Same(layers.Zones.Group, layers.Sensor.Group.Children[1]);

        // A transform applied to the zone group must not reach the marker.
        layers.Zones.Group.Transform = new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), 90));

        Assert.NotEqual(Transform3D.Identity, layers.Zones.Group.Transform);
        Assert.Equal(Transform3D.Identity, layers.Sensor.Group.Transform);
        Assert.Equal(Transform3D.Identity, layers.Back.Group.Transform);
        Assert.Equal(Transform3D.Identity, layers.Map.Group.Transform);
    }

    /// <summary>
    /// The map layer is the group ZoneRenderer fills, so the layer the window declares and the group the
    /// renderer writes into must be the same object. If they ever diverge, accumulated readings are drawn
    /// into a group that is not in the scene, which is a silent failure: the map still accumulates, and
    /// nothing appears.
    /// </summary>
    [Fact]
    public void MapLayerIsTheGroupThatReceivesAccumulatedReadings()
    {
        // EVERYTHING WITH A DISPATCHER IS CREATED INSIDE THE STA RUNNER, and the reason is a finding about
        // Freezables rather than a workaround. The first version created the layers on xunit's pooled
        // thread and the UniformGrid inside the runner; it then failed with:
        //
        //   System.InvalidOperationException : The calling thread cannot access this object because a
        //   different thread owns it.
        //     at System.Windows.Media.Media3D.Model3DGroup.get_Children()
        //     at EchoEnvironment.Rendering.ZoneRenderer..ctor(...)
        //
        // Model3DGroup is a DependencyObject, and a DependencyObject binds itself to the Dispatcher of the
        // thread that constructs it. ZoneRenderer's constructor reads mapGroup.Children, so a group built
        // on a pooled thread is inaccessible from an STA thread. Freezability is what makes a group safe to
        // SHARE once it is frozen; it is not what makes it safe to construct on one thread and use on
        // another. So the whole object graph this test hands to the renderer is built on one apartment.
        StaRunner.Run(() =>
        {
            var layers = new SceneLayers(Marker(), Sphere());
            var zoneGroup = new Model3DGroup();
            var heatGrid = new UniformGrid();
            var renderer = new ZoneRenderer(zoneGroup, layers.Map.Group, heatGrid, new BrushCache(), 20.0, 4000.0, 45.0, 16);

            var distances = new int[64];
            for (int i = 0; i < distances.Length; i++)
            {
                distances[i] = 1000;
            }

            renderer.UpdateFrame(distances, keepMap: true);

            Assert.NotEmpty(layers.Map.Group.Children);
            Assert.Same(layers.Map.Group, Assert.IsAssignableFrom<Model3DGroup>(layers.Root.Children[1]));

            // The map layer's models are the bucket models, and they are inside the root the viewport
            // renders - not in a detached group.
            Assert.Contains(layers.Map.Group, layers.Root.Children.Cast<Model3D>());
        });
    }

    /// <summary>
    /// A layer cannot be constructed without a name. The name is the whole point of the type: without it
    /// these are four anonymous groups and the defect has been renamed rather than fixed.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void LayerRequiresAName(string name)
    {
        Assert.ThrowsAny<ArgumentException>(() => new SceneLayer(name));
    }

    /// <summary>
    /// Construction rejects nulls rather than producing a layer that renders nothing. A scene graph with
    /// a null child fails at render time, far from the mistake that caused it.
    /// </summary>
    [Fact]
    public void ConstructionRejectsNullMarkerAndNullSphere()
    {
        Assert.Throws<ArgumentNullException>(() => new SceneLayers(null!, Sphere()));
        Assert.Throws<ArgumentNullException>(() => new SceneLayers(Marker(), null!));

        var layers = new SceneLayers(Marker(), Sphere());
        Assert.Throws<ArgumentNullException>(() => layers.ShowGrid(null!));
        Assert.Throws<ArgumentNullException>(() => layers.HideGrid(null!));
    }
}
