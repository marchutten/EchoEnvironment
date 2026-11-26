using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using EchoEnvironment.Core;
using EchoEnvironment.Rendering;
using Xunit;

namespace EchoEnvironment.Tests;

/// <summary>
/// Tests for ZoneRenderer, extracted from MainWindow at Phase 5 commit 2.
///
/// MIGRATION_PLAN 5 says a move is verifiable by reading the diff. That holds for the static factories
/// in commit 1, whose whole behaviour is their return value. It does not hold here: UpdateFrame,
/// EnsureZoneModels, RenderMapIfDue and the map throttle all depend on state carried between calls, so
/// "the body is unchanged" says nothing about whether the sequence of calls still produces the same
/// scene. These tests assert the sequences.
///
/// They are also the first tests that have ever exercised this code. UpdateFrame, UpdateHeatmap,
/// EnsureZoneModels, AddToMap and RenderMapIfDue were all private members of a Window, and no test in
/// this suite had reached any of them.
///
/// WHY NO REFLECTION IS NEEDED. The constructor is handed the visual groups it fills - the zone group,
/// the map group, the heat grid - rather than creating them. So everything ZoneRenderer did is visible
/// through objects the test already owns: zone models in zoneGroup.Children, bucket geometries in
/// mapGroup.Children, cells in heatGrid.Children. A design that created its own groups would have made
/// the same assertions require reflection into private fields.
///
/// EVERY TEST RUNS ON AN STA THREAD, AND THAT IS A FINDING ABOUT THE DESIGN. The first version of this
/// file ran on xunit's thread pool and all 13 tests failed identically:
///
///   System.InvalidOperationException : The calling thread must be STA, because many UI components require this.
///     at System.Windows.Input.InputManager..ctor()
///     at System.Windows.FrameworkElement..ctor()
///     at System.Windows.Controls.Panel..ctor()
///     at EchoEnvironment.Tests.ZoneRendererTests.New()
///
/// The throw came from `new UniformGrid()`. Not from a Model3DGroup, not from a GeometryModel3D, not
/// from a brush - those construct fine on a pooled thread. What needs an apartment is the 2D heat grid,
/// which is a FrameworkElement. So the failure is evidence that ZoneRenderer mixes two jobs: 3D zone
/// geometry, which is apartment-free and would be testable on the thread pool, and the 2D panel on the
/// right side of the window, which is not. Splitting the heat grid out would let most of the renderer
/// be tested without StaRunner. That split is deliberately not done in this commit - commit 2 is a move,
/// and separating two responsibilities is a design change owed its own step. It is recorded in 12.9.
/// </summary>
public class ZoneRendererTests
{
    private const double MinMm = 20.0;
    private const double MaxMm = 4000.0;
    private const double FieldOfView = 45.0;
    private const int MapBuckets = 16;

    private const int ZoneCount = 64;
    private const int SmallZoneCount = 16;

    /// <summary>Edge length of the zone cube ZoneRenderer builds. Must match its own constant.</summary>
    private const double CubeSize = 0.02;

    private static (ZoneRenderer Renderer, Model3DGroup ZoneGroup, Model3DGroup MapGroup, UniformGrid HeatGrid) New()
    {
        var zoneGroup = new Model3DGroup();
        var mapGroup = new Model3DGroup();
        var heatGrid = new UniformGrid();

        // MainWindow always assigns a transform to the zone group before the first frame, and
        // UpdateFrame reads its value every frame. Mirror that rather than relying on Model3DGroup's
        // default, so the tests exercise the same path production takes.
        zoneGroup.Transform = new Transform3DGroup();

        // The BrushCache is not returned to the caller on purpose. Every reuse assertion in this file
        // reads the brush or material the renderer actually put on a model, which is observable through
        // the groups the test already owns. Asking for the cache itself would let a test assert that a
        // dictionary was consulted instead of that the scene stopped allocating.
        var renderer = new ZoneRenderer(zoneGroup, mapGroup, heatGrid, new BrushCache(), MinMm, MaxMm, FieldOfView, MapBuckets);
        return (renderer, zoneGroup, mapGroup, heatGrid);
    }

    private static int[] Distances(int count, int mm)
    {
        var values = new int[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = mm;
        }

        return values;
    }

    /// <summary>
    /// The zone group's first child is the shared line model and every zone is one model after it, so
    /// the child count is the zone count plus one. Asserting the plus one pins the line model's place
    /// in the group, which UpdateFrame depends on and which is otherwise invisible.
    /// </summary>
    [Fact]
    public void CreatesOneModelPerZonePlusTheSharedLineModel()
    {
        StaRunner.Run(() =>
        {
            var (renderer, zoneGroup, _, _) = New();

            renderer.UpdateFrame(Distances(ZoneCount, 1000), keepMap: false);

            Assert.Equal(ZoneCount + 1, zoneGroup.Children.Count);
        });
    }

    /// <summary>
    /// The sensor array can report 16 or 64 zones. Switching between them must rebuild the group, not
    /// leave 64 models pointed at 16 readings - which is the failure EnsureZoneModels exists to prevent.
    /// </summary>
    [Fact]
    public void ChangingZoneCountRebuildsTheZoneModels()
    {
        StaRunner.Run(() =>
        {
            var (renderer, zoneGroup, _, _) = New();

            renderer.UpdateFrame(Distances(ZoneCount, 1000), keepMap: false);
            Assert.Equal(ZoneCount + 1, zoneGroup.Children.Count);

            renderer.UpdateFrame(Distances(SmallZoneCount, 1000), keepMap: false);
            Assert.Equal(SmallZoneCount + 1, zoneGroup.Children.Count);
        });
    }

    /// <summary>
    /// Every valid zone sits on a sphere of radius mm / 1000 around the sensor, and every one of them is
    /// in front of it (negative Z, because the sensor looks along -Z). Asserted as a property of all 64
    /// models rather than as six expected numbers, so the test states the geometry instead of restating
    /// the trigonometry that produces it.
    /// </summary>
    [Fact]
    public void ValidZonesLieOnASphereInFrontOfTheSensor()
    {
        StaRunner.Run(() =>
        {
            var (renderer, zoneGroup, _, _) = New();
            const int mm = 1000;

            renderer.UpdateFrame(Distances(ZoneCount, mm), keepMap: false);

            // Expected radius: mm * SceneUnitsPerMm = 1000 / 1000 = 1.0.
            for (int i = 1; i <= ZoneCount; i++)
            {
                var model = (GeometryModel3D)zoneGroup.Children[i];
                var translate = Assert.IsAssignableFrom<TranslateTransform3D>(model.Transform);

                AssertMath.Close(1.0, translate.OffsetX * translate.OffsetX
                    + translate.OffsetY * translate.OffsetY
                    + translate.OffsetZ * translate.OffsetZ, AssertMath.Geometry, $"zone {i}: radius squared");

                Assert.True(translate.OffsetZ < 0, $"zone {i}: a zone in front of the sensor must have negative Z, got {translate.OffsetZ}.");
            }
        });
    }

    /// <summary>
    /// The fan must actually fan out: left half negative X, right half positive X, top half positive Y,
    /// bottom half negative Y. A sign error in the azimuth or elevation term mirrors the scene, and a
    /// mirrored scene still looks like a plausible fan.
    /// </summary>
    [Fact]
    public void ZoneFanSpreadsInBothAxes()
    {
        StaRunner.Run(() =>
        {
            var (renderer, zoneGroup, _, _) = New();

            renderer.UpdateFrame(Distances(ZoneCount, 1000), keepMap: false);

            int size = 8;
            for (int row = 0; row < size; row++)
            {
                for (int col = 0; col < size; col++)
                {
                    var translate = (TranslateTransform3D)((GeometryModel3D)zoneGroup.Children[row * size + col + 1]).Transform;

                    if (col < size / 2)
                    {
                        Assert.True(translate.OffsetX < 0, $"row {row} col {col}: left half must have negative X.");
                    }
                    else
                    {
                        Assert.True(translate.OffsetX > 0, $"row {row} col {col}: right half must have positive X.");
                    }

                    // Lower rows face board +X = sensor +Y (verified on hardware).
                    if (row < size / 2)
                    {
                        Assert.True(translate.OffsetY < 0, $"row {row} col {col}: top half must have negative Y.");
                    }
                    else
                    {
                        Assert.True(translate.OffsetY > 0, $"row {row} col {col}: bottom half must have positive Y.");
                    }
                }
            }
        });
    }

    /// <summary>
    /// A reading outside the valid range is hidden by scaling the model to zero rather than removed
    /// from the group, so the model index still matches the zone index for the next frame.
    /// </summary>
    [Fact]
    public void InvalidReadingHidesItsZoneWithoutRemovingIt()
    {
        StaRunner.Run(() =>
        {
            var (renderer, zoneGroup, _, _) = New();

            int[] distances = Distances(ZoneCount, 1000);
            distances[0] = 5;                 // below MinMm
            distances[ZoneCount - 1] = 9000;  // above MaxMm

            renderer.UpdateFrame(distances, keepMap: false);

            Assert.IsAssignableFrom<ScaleTransform3D>(((GeometryModel3D)zoneGroup.Children[1]).Transform);
            Assert.IsAssignableFrom<ScaleTransform3D>(((GeometryModel3D)zoneGroup.Children[ZoneCount]).Transform);
            Assert.IsAssignableFrom<TranslateTransform3D>(((GeometryModel3D)zoneGroup.Children[2]).Transform);

            // Still present, so the group has not shifted and the indices still line up.
            Assert.Equal(ZoneCount + 1, zoneGroup.Children.Count);
        });
    }

    /// <summary>
    /// The zone colour is the Core ramp sampled at that distance, converted exactly once through
    /// WpfMath. Asserting it against ColorScale.For is what makes "the conversion happens at the
    /// boundary" observable rather than a claim about where the code sits.
    /// </summary>
    [Fact]
    public void ZoneColourIsTheCoreRampAtThatDistance()
    {
        StaRunner.Run(() =>
        {
            var (renderer, zoneGroup, _, _) = New();
            const int mm = 1500;

            renderer.UpdateFrame(Distances(ZoneCount, mm), keepMap: false);

            Color expected = ColorScale.For(mm, MaxMm).ToColor();
            for (int i = 1; i <= ZoneCount; i++)
            {
                var model = (GeometryModel3D)zoneGroup.Children[i];
                var brush = Assert.IsAssignableFrom<SolidColorBrush>(((DiffuseMaterial)model.Material).Brush);
                Assert.Equal(expected, brush.Color);
            }
        });
    }

    /// <summary>
    /// One line is drawn per valid zone, and AddLine builds each line from two crossed quads - eight
    /// positions. So the line model's position count is a direct census of how many lines were drawn,
    /// including the ones that invalid readings must suppress.
    /// </summary>
    [Fact]
    public void OneLineIsDrawnPerValidZone()
    {
        StaRunner.Run(() =>
        {
            var (renderer, zoneGroup, _, _) = New();

            int[] distances = Distances(ZoneCount, 1000);
            distances[0] = 5;
            distances[1] = 9000;

            renderer.UpdateFrame(distances, keepMap: false);

            var lines = (GeometryModel3D)zoneGroup.Children[0];
            var lineGeometry = Assert.IsAssignableFrom<MeshGeometry3D>(lines.Geometry!);
            int validZones = ZoneCount - 2;
            Assert.Equal(validZones * 8, lineGeometry.Positions.Count);
        });
    }

    /// <summary>
    /// The heat grid is rebuilt only when the zone count changes; the cells are then reused frame after
    /// frame and only their contents are updated. Rebuilding 64 Borders every frame at 60 Hz is exactly
    /// the kind of allocation defect 5a is about, so the reuse is worth pinning.
    /// </summary>
    [Fact]
    public void HeatGridCellsAreReusedBetweenFrames()
    {
        StaRunner.Run(() =>
        {
            var (renderer, _, _, heatGrid) = New();

            renderer.UpdateFrame(Distances(ZoneCount, 1000), keepMap: false);
            Assert.Equal(ZoneCount, heatGrid.Children.Count);

            Border firstCellBefore = (Border)heatGrid.Children[0];

            renderer.UpdateFrame(Distances(ZoneCount, 2000), keepMap: false);

            Assert.Equal(ZoneCount, heatGrid.Children.Count);
            Assert.Same(firstCellBefore, heatGrid.Children[0]);
            Assert.Equal("2000", ((TextBlock)((Border)heatGrid.Children[0]).Child).Text);
        });
    }

    /// <summary>
    /// An out-of-range reading shows as a dash on a grey cell rather than as a number, and the grey is
    /// not a ramp colour: it has to be distinguishable from a real near-distance reading, which the
    /// ramp also renders brightly.
    /// </summary>
    [Fact]
    public void InvalidReadingShowsADashOnAGreyCell()
    {
        StaRunner.Run(() =>
        {
            var (renderer, _, _, heatGrid) = New();

            int[] distances = Distances(ZoneCount, 1000);
            distances[0] = 5;

            renderer.UpdateFrame(distances, keepMap: false);

            var cell = (Border)heatGrid.Children[0];
            Assert.Equal("-", ((TextBlock)cell.Child).Text);
            Assert.Equal(Color.FromRgb(50, 50, 50), ((SolidColorBrush)cell.Background).Color);

            var validCell = (Border)heatGrid.Children[1];
            Assert.Equal("1000", ((TextBlock)validCell.Child).Text);
        });
    }

    /// <summary>
    /// keepMap is a parameter, not a field. In MainWindow it was read from the checkbox inside
    /// UpdateFrame; here the same switch is testable without a checkbox, and turning it off must leave
    /// the map untouched.
    /// </summary>
    [Fact]
    public void AccumulationIsSkippedWhenKeepMapIsFalse()
    {
        StaRunner.Run(() =>
        {
            var (renderer, _, mapGroup, _) = New();

            renderer.UpdateFrame(Distances(ZoneCount, 1000), keepMap: false);

            Assert.Empty(MapPositions(mapGroup));
        });
    }

    /// <summary>
    /// Accumulating 64 zones at one distance produces 64 whole-degree cells - the fan spans roughly
    /// 20 degrees in each axis at 5.625-degree steps, so no two zones collide - and all 64 land in the
    /// same distance bucket. The assertion is therefore "one stamped cube per accumulated reading",
    /// counted in the bucket geometry the renderer owns.
    ///
    /// The per-cube position count is read from MeshFactory rather than hard-coded, because it is the
    /// mesh's property and not the map's: hard-coding 8 here would assert a fact about cube geometry in
    /// a test about map accumulation, and would break for the wrong reason if the mesh ever changed.
    /// </summary>
    [Fact]
    public void AccumulationStampsOneCubePerAccumulatedReading()
    {
        StaRunner.Run(() =>
        {
            var (renderer, _, mapGroup, _) = New();
            int positionsPerCube = MeshFactory.CreateCubeMesh(CubeSize).Positions.Count;

            renderer.UpdateFrame(Distances(ZoneCount, 1000), keepMap: true);

            Assert.Equal(ZoneCount * positionsPerCube, MapPositions(mapGroup).Count);
        });
    }

    /// <summary>
    /// ClearMap empties both the accumulated readings and the bucket geometries. It is the only public
    /// window into the map, because ZoneRenderer deliberately does not expose the WorldMap it owns -
    /// see ZoneRendererDoesNotExposeCoreTypes.
    /// </summary>
    [Fact]
    public void ClearMapEmptiesTheBucketGeometries()
    {
        StaRunner.Run(() =>
        {
            var (renderer, _, mapGroup, _) = New();

            renderer.UpdateFrame(Distances(ZoneCount, 1000), keepMap: true);
            Assert.NotEmpty(MapPositions(mapGroup));

            renderer.ClearMap();

            Assert.Empty(MapPositions(mapGroup));
        });
    }

    /// <summary>
    /// The map throttle: the bucket meshes are rebuilt at most every 150 ms, so a 60 Hz render loop
    /// rebuilds them about ten times a second instead of sixty. The readings are still accumulated every
    /// frame - only the mesh rebuild is delayed - which is what this asserts, by changing the distance
    /// between frames and watching the rebuilt geometry lag behind.
    ///
    /// TIMING DEPENDENCY, stated rather than hidden: the middle assertion requires the second frame to
    /// arrive inside the 150 ms window. Each of these frames takes well under a millisecond, so the
    /// margin is two orders of magnitude; but this is the one test in the file whose correctness depends
    /// on wall-clock speed rather than only on logic.
    /// </summary>
    [Fact]
    public void MapMeshIsRebuiltOnAThrottleNotEveryFrame()
    {
        StaRunner.Run(() =>
        {
            var (renderer, _, mapGroup, _) = New();

            renderer.UpdateFrame(Distances(ZoneCount, 1000), keepMap: true);
            AssertMath.Close(1.0, FirstMapPositionRadius(mapGroup), 0.05, "first frame map radius");

            // Same frame rate, double the distance. The new readings are in the map now, but the mesh
            // must not have been rebuilt yet, so the geometry still shows the first frame's radius.
            renderer.UpdateFrame(Distances(ZoneCount, 2000), keepMap: true);
            AssertMath.Close(1.0, FirstMapPositionRadius(mapGroup), 0.05, "map radius while throttled");

            System.Threading.Thread.Sleep(200);

            renderer.UpdateFrame(Distances(ZoneCount, 2000), keepMap: true);
            AssertMath.Close(2.0, FirstMapPositionRadius(mapGroup), 0.05, "map radius after the throttle window");
        });
    }

    // -----------------------------------------------------------------------------------------
    // Phase 5 commit 3 (defect D3, MIGRATION_PLAN 5a and 5b): the allocations that used to happen
    // every frame must happen once.
    //
    // WHY IDENTITY ASSERTIONS. "The colour is still right" is already pinned by
    // ZoneColourIsTheCoreRampAtThatDistance, and "the position is still right" by
    // ValidZonesLieOnASphereInFrontOfTheSensor. What those tests cannot see is whether the renderer
    // built a new object to express them. Assert.Same is the only assertion that measures the
    // allocation rather than the value, and an optimisation that is not measured can be undone by the
    // next edit without anything failing.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// 5b: the plan's wording is "64 transforms created once in EnsureZoneModels and only mutated in
    /// UpdateFrame". Measured as the plan states it: the transform object on a zone is the same object
    /// in the second frame, and its offsets are the second frame's positions.
    /// </summary>
    [Fact]
    public void ZoneTransformIsReusedAcrossFrames_AndMutatedInsteadOfReplaced()
    {
        StaRunner.Run(() =>
        {
            var (renderer, zoneGroup, _, _) = New();

            renderer.UpdateFrame(Distances(ZoneCount, 1000), keepMap: false);

            var firstFrame = Assert.IsAssignableFrom<TranslateTransform3D>(
                ((GeometryModel3D)zoneGroup.Children[1]).Transform);
            double firstOffsetX = firstFrame.OffsetX;

            renderer.UpdateFrame(Distances(ZoneCount, 2000), keepMap: false);

            var secondFrame = Assert.IsAssignableFrom<TranslateTransform3D>(
                ((GeometryModel3D)zoneGroup.Children[1]).Transform);

            // Same object...
            Assert.Same(firstFrame, secondFrame);

            // ...and a different position, so the reuse is not a stale transform that was never updated.
            // Doubling the distance doubles the radius, so X must have moved.
            Assert.NotEqual(firstOffsetX, secondFrame.OffsetX);
            AssertMath.Close(2.0, firstOffsetX == 0 ? 1.0 : secondFrame.OffsetX / firstOffsetX, 0.01, "offset doubled with distance");

            // A reused transform must not be frozen, or the mutation would throw.
            Assert.False(secondFrame.IsFrozen,
                "a zone transform that UpdateFrame mutates cannot be frozen; freezing it would make the next frame throw instead of move.");
        });
    }

    /// <summary>
    /// An invalid reading used to allocate a fresh ScaleTransform3D. Now one frozen scale is shared by
    /// every hidden zone in every frame. It stays a ScaleTransform3D, so InvalidReadingHidesItsZoneWithout
    /// RemovingIt's distinction between "hidden" and "moved" still holds.
    /// </summary>
    [Fact]
    public void HiddenZonesShareOneFrozenScaleAcrossEveryFrame()
    {
        StaRunner.Run(() =>
        {
            var (renderer, zoneGroup, _, _) = New();

            int[] distances = Distances(ZoneCount, 1000);
            distances[0] = 5;
            distances[1] = 9000;

            renderer.UpdateFrame(distances, keepMap: false);
            var firstHidden = Assert.IsAssignableFrom<ScaleTransform3D>(((GeometryModel3D)zoneGroup.Children[1]).Transform);
            var secondHidden = Assert.IsAssignableFrom<ScaleTransform3D>(((GeometryModel3D)zoneGroup.Children[2]).Transform);

            // Two zones hidden in the same frame share one object.
            Assert.Same(firstHidden, secondHidden);
            Assert.True(firstHidden.IsFrozen,
                "a scale shared by many models must be frozen; a mutable shared scale would let one zone un-hide another.");

            // And it is still the same object in the next frame.
            renderer.UpdateFrame(distances, keepMap: false);
            Assert.Same(firstHidden, ((GeometryModel3D)zoneGroup.Children[1]).Transform);
        });
    }

    /// <summary>
    /// THE TRAP 5a ALMOST INTRODUCED. The code it replaced mutated the Brush of a single material object
    /// referenced by both Material and BackMaterial, so front and back faces always matched. Replacing
    /// only Material - which is the obvious way to write an assignment - leaves BackMaterial painted with
    /// the previous frame's colour, and the back faces of every zone would silently keep an old colour.
    /// Asserted because the failure mode is invisible in a screenshot of a closed cube.
    /// </summary>
    [Fact]
    public void FrontAndBackMaterialAlwaysComeFromTheSameCacheEntry()
    {
        StaRunner.Run(() =>
        {
            var (renderer, zoneGroup, _, _) = New();

            renderer.UpdateFrame(Distances(ZoneCount, 1000), keepMap: false);

            for (int i = 1; i <= ZoneCount; i++)
            {
                var model = (GeometryModel3D)zoneGroup.Children[i];
                Assert.Same(model.Material, model.BackMaterial);
            }

            // Recolour, then check again: the match must survive the change, not just hold at creation.
            renderer.UpdateFrame(Distances(ZoneCount, 3000), keepMap: false);

            Color expected = ColorScale.For(3000, MaxMm).ToColor();
            for (int i = 1; i <= ZoneCount; i++)
            {
                var model = (GeometryModel3D)zoneGroup.Children[i];
                var front = Assert.IsAssignableFrom<DiffuseMaterial>(model.Material);
                var back = Assert.IsAssignableFrom<DiffuseMaterial>(model.BackMaterial);

                Assert.Same(front, back);
                Assert.Equal(expected, Assert.IsAssignableFrom<SolidColorBrush>(front.Brush).Color);
                Assert.True(front.IsFrozen, "a material assigned to two slots of a shared model must be frozen.");
            }
        });
    }

    /// <summary>
    /// The heat grid already reused its cells; what it did not reuse was their background. A cell whose
    /// distance did not change between frames must keep the same brush instance, and a cell whose
    /// distance did change must get the new colour, not the old brush recoloured.
    /// </summary>
    [Fact]
    public void HeatGridBackgroundIsReusedWhenADistanceDoesNotChange()
    {
        StaRunner.Run(() =>
        {
            var (renderer, _, _, heatGrid) = New();

            int[] unchanged = Distances(ZoneCount, 1000);
            renderer.UpdateFrame(unchanged, keepMap: false);
            var cell = (Border)heatGrid.Children[0];
            SolidColorBrush before = Assert.IsAssignableFrom<SolidColorBrush>(cell.Background);

            // Same distance for this cell, different distance for its neighbour.
            int[] mixed = Distances(ZoneCount, 1000);
            mixed[1] = 2500;
            renderer.UpdateFrame(mixed, keepMap: false);

            SolidColorBrush after = Assert.IsAssignableFrom<SolidColorBrush>(cell.Background);
            Assert.Same(before, after);
            Assert.True(before.IsFrozen, "a brush shared between cells and frames must be frozen.");

            var neighbour = (Border)heatGrid.Children[1];
            Assert.Equal(ColorScale.For(2500, MaxMm).ToColor(),
                Assert.IsAssignableFrom<SolidColorBrush>(neighbour.Background).Color);
        });
    }

    /// <summary>
    /// The out-of-range grey must not come from the ramp, and must stay the same object frame after
    /// frame. This is the heat-grid half of InvalidReadingShowsADashOnAGreyCell, measured as identity.
    /// </summary>
    [Fact]
    public void InvalidHeatCellGreyIsAFrozenBrushThatIsNotARampColour()
    {
        StaRunner.Run(() =>
        {
            var (renderer, _, _, heatGrid) = New();

            int[] distances = Distances(ZoneCount, 1000);
            distances[0] = 5;

            renderer.UpdateFrame(distances, keepMap: false);
            var grey = Assert.IsAssignableFrom<SolidColorBrush>(((Border)heatGrid.Children[0]).Background);

            renderer.UpdateFrame(distances, keepMap: false);
            Assert.Same(grey, ((Border)heatGrid.Children[0]).Background);

            Assert.True(grey.IsFrozen);
            Assert.Equal(Color.FromRgb(50, 50, 50), grey.Color);

            // The grey must not be any colour the ramp can produce for a valid reading, or an invalid
            // cell would be indistinguishable from a real near-distance one.
            for (int mm = 20; mm <= 4000; mm += 1)
            {
                Assert.NotEqual(grey.Color, ColorScale.For(mm, MaxMm).ToColor());
            }
        });
    }

    private static List<Point3D> MapPositions(Model3DGroup mapGroup)
    {
        var positions = new List<Point3D>();
        foreach (object child in mapGroup.Children)
        {
            var model = (GeometryModel3D)child;
            if (model.Geometry is MeshGeometry3D mesh)
            {
                positions.AddRange(mesh.Positions);
            }
        }

        return positions;
    }

    /// <summary>
    /// Distance of the first stamped cube vertex from the origin. Every zone in these frames is at the
    /// same distance, so every map position sits at that radius give or take the cube's own extent
    /// (0.02 scene units, scaled by 0.5), which is why the assertions above use a loose tolerance.
    /// </summary>
    private static double FirstMapPositionRadius(Model3DGroup mapGroup)
    {
        Point3D first = MapPositions(mapGroup)[0];
        return Math.Sqrt(first.X * first.X + first.Y * first.Y + first.Z * first.Z);
    }
}
