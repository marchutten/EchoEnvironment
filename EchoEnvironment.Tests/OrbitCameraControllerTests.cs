using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
using EchoEnvironment.Core;
using EchoEnvironment.Rendering;
using Xunit;

namespace EchoEnvironment.Tests;

/// <summary>
/// Tests for the view transform that places a model group in a viewport — the thing a screenshot
/// actually shows (MIGRATION_PLAN §12.1's Board view row, §12.13).
///
/// WHY THIS FILE EXISTS. §12.1's Board view row has to be re-recorded from a current build before the
/// visual gate can be run against it. Until this file, nothing in the suite read
/// OrbitCameraController.CreateViewTransform at all: the axis tests measure the gizmo in its OWN model
/// space, which says what the code draws but not what a viewer sees. A board-view expectation written from
/// the gizmo's model coordinates would describe the scene the way the source spells it rather than the way
/// it renders — and §12.1's stale row is exactly that mistake already made once.
///
/// WHAT IS BEING PINNED, AND WHY IT IS THE HIGH-IMPACT RISK. Each composed matrix must be a ROTATION:
/// orthonormal rows, determinant +1, no translation. A determinant of -1 is a reflection, and a reflection
/// in a view transform mirrors the whole viewport while every number in the source stays plausible. That is
/// the risk MIGRATION_PLAN §4 rates High/High — "Mat3 row/column convention diverges from Matrix3D →
/// mirrored or transposed scene" — closed for the adapter by WpfMathTests but open for the view transform,
/// which is a second and independent place the two matrix conventions meet.
///
/// THE HALF THAT IS DELIBERATELY LEFT DIFFERENT, AND PINNED AS DIFFERENT. The board viewport and the sensor
/// viewport apply DIFFERENT transforms: the board group gets a model remap, the sensor group gets a 180°
/// turn about X. That is not defect D9 and this file does not pretend to fix it. D9 was about which LETTER
/// means which AXIS and which COLOUR means which letter, and 5d settled it in AxisConvention. The remap is a
/// geometry decision, §12.12 records that 5d deliberately did not touch it, and
/// BoardViewportStillHasItsOwnSceneTree_AndKeepsTheWorldAxesOutOfTheSensorLayers already refuses to let the
/// two trees be flattened into one. So the disagreement is asserted here as a fact to be preserved until
/// somebody changes it on purpose: a pinned difference is a decision, an unpinned one is an accident waiting
/// to be tidied away in a commit whose stated job is something else.
///
/// WHY THE ASSERTIONS ARE ABOUT WORLD AXES AND NOT ONLY ABOUT MATRIX ELEMENTS. The 16-element matrix
/// assertions are the reproducible half. But a matrix cannot be read against a window, and §12.1's row is a
/// sentence a human checks against a screenshot. The world-axis assertions say, in words a viewer can
/// verify: "the arm the convention calls Z appears pointing downward in the board view." That is the
/// sentence §12.1 needs.
///
/// WHY STA. OrbitCameraController's constructor takes a PerspectiveCamera, a ModelVisual3D and a Border —
/// three FrameworkElements. Unlike SceneLayers' Freezables, these need the apartment.
/// </summary>
public class OrbitCameraControllerTests
{
    private const double Tolerance = AssertMath.Geometry;

    /// <summary>
    /// The remap literal from OrbitCameraController.CreateViewTransform, written out so the composition can
    /// be asserted independently of the group that assembles it.
    /// </summary>
    private static readonly Matrix3D ModelRemap = new(
        0, -1, 0, 0,
        1, 0, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1);

    /// <summary>The 180° turn about X that the sensor viewport uses instead of the remap.</summary>
    private static readonly Matrix3D HalfTurnAboutX = new(
        1, 0, 0, 0,
        0, -1, 0, 0,
        0, 0, -1, 0,
        0, 0, 0, 1);

    /// <summary>The 90° turn about Y that both viewports apply last.</summary>
    private static readonly Matrix3D TurnAboutY = new(
        0, 0, -1, 0,
        0, 1, 0, 0,
        1, 0, 0, 0,
        0, 0, 0, 1);

    /// <summary>
    /// World -Y. Core's Vec3 defines no unary-minus operator, and WorldFrame names only East, Up, North and
    /// South — there is no "down" in the world frame because no axis is named for it. The board view's Z arm
    /// nevertheless points there after the remap, so the tests need the direction named rather than spelled
    /// as three literals at each call site.
    /// </summary>
    private static readonly Vec3 Down = new(0, -1, 0);

    private static readonly Vec3 West = new(-1, 0, 0);

    /// <summary>
    /// The transform the board viewport puts on _boardGroup: MainWindow passes remapModel: true with the
    /// sensor orientation at Matrix3D.Identity, which is the disconnected state §12.1's gate image shows.
    /// </summary>
    private static Matrix3D BoardViewTransform()
    {
        return CreateController().CreateViewTransform(remapModel: true, IdentityOrientation()).Value;
    }

    /// <summary>The transform the sensor viewport puts on the zone group: remapModel: false.</summary>
    private static Matrix3D SensorViewTransform()
    {
        return CreateController().CreateViewTransform(remapModel: false, IdentityOrientation()).Value;
    }

    private static OrbitCameraController CreateController()
    {
        var camera = new PerspectiveCamera();
        var sceneVisual = new ModelVisual3D();
        var surface = new Border();
        return new OrbitCameraController(camera, sceneVisual, surface);
    }

    private static MatrixTransform3D IdentityOrientation() => new(Matrix3D.Identity);

    // -----------------------------------------------------------------------------------------
    // The risk §4 rates High/High: a view transform that mirrors the scene instead of rotating it.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Both viewports' composed transforms are ROTATIONS, not reflections, and carry no translation.
    ///
    /// A determinant of -1 would mirror the viewport. Every source number would still look correct, every
    /// axis test would still pass, and the only symptom would be a scene that reads plausibly while facing
    /// the wrong way — which is why this is asserted from the produced matrix rather than trusted from the
    /// literals that built it.
    /// </summary>
    [Theory]
    [InlineData("board")]
    [InlineData("sensor")]
    public void EachViewportTransformIsARotation_AndCarriesNoTranslation(string viewport)
    {
        StaRunner.Run(() =>
        {
            Matrix3D m = viewport == "board" ? BoardViewTransform() : SensorViewTransform();

            AssertMath.Close(1.0, RotationDeterminant(m), Tolerance, $"{viewport} view: determinant must be +1 (rotation, not reflection)");

            // No translation: a view transform that shifted the scene would move the gizmo off the board it
            // labels, and the shift would be invisible in every model-space test.
            AssertMath.Close(0.0, m.OffsetX, Tolerance, $"{viewport} view: OffsetX");
            AssertMath.Close(0.0, m.OffsetY, Tolerance, $"{viewport} view: OffsetY");
            AssertMath.Close(0.0, m.OffsetZ, Tolerance, $"{viewport} view: OffsetZ");
            AssertMath.Close(1.0, m.M44, Tolerance, $"{viewport} view: M44");

            // Orthonormal rows: three unit vectors, mutually perpendicular. A drifting scale or a sheared
            // axis shows up here before it shows up as a visual regression.
            Vec3[] rows = [Row(m, 1), Row(m, 2), Row(m, 3)];
            for (int i = 0; i < 3; i++)
            {
                AssertMath.Close(1.0, rows[i].Length, Tolerance, $"{viewport} view: row {i + 1} length");
            }

            AssertMath.Close(0.0, AssertMath.Dot(rows[0], rows[1]), Tolerance, $"{viewport} view: row1 . row2");
            AssertMath.Close(0.0, AssertMath.Dot(rows[0], rows[2]), Tolerance, $"{viewport} view: row1 . row3");
            AssertMath.Close(0.0, AssertMath.Dot(rows[1], rows[2]), Tolerance, $"{viewport} view: row2 . row3");
        });
    }

    // -----------------------------------------------------------------------------------------
    // The screenshot expectation, stated as world axes -> viewport directions.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// THE SENTENCE §12.1's BOARD VIEW ROW NEEDS, measured rather than remembered.
    ///
    /// In the board viewport, with no IMU data (the disconnected gate state), the labelled gizmo sits in
    /// _boardGroup and therefore carries the remap. Each arm the convention points at a world axis lands on
    /// a fixed direction in the viewport. These are the directions a screenshot shows, and they are what
    /// §12.1's Board view row must record — the old row recorded the gizmo's MODEL coordinates instead,
    /// which is how it came to describe defect D9 as the expected appearance.
    ///
    /// The BoardViewport camera sits at (0.9, 0.7, 1.3) looking at the origin with up = +Y, so viewport +Y
    /// reads as up on screen, +X as toward the right, and -Z as away from the viewer.
    ///
    /// Note the Z arm in particular: 5d moved it from -Z to +Z in MODEL space, and the remap then turns it
    /// into a downward-pointing arm in the viewport. A model-space change and a screen-space direction are
    /// different claims, and conflating them is how a fix gets read as a regression.
    /// </summary>
    [Fact]
    public void DisconnectedBoardViewPointsEachGizmoArmAtAFixedViewportDirection()
    {
        StaRunner.Run(() =>
        {
            Matrix3D view = BoardViewTransform();

            AssertDirection("X arm", AssertMath.Transform(view, WorldFrame.East), Down, "east -> down");

            AssertDirection("Y arm", AssertMath.Transform(view, WorldFrame.Up), WorldFrame.North, "up -> north/away");

            AssertDirection("Z arm", AssertMath.Transform(view, WorldFrame.South), WorldFrame.East, "south -> east");
        });
    }

    /// <summary>
    /// The exact composed matrix for the board viewport, pinned element-for-element.
    ///
    /// This is what makes the screenshot expectation reproducible rather than a description: if the remap
    /// literals in CreateViewTransform change, this fails and the change is attributed to one commit instead
    /// of discovered afterwards in a screenshot diff.
    /// </summary>
    [Fact]
    public void DisconnectedBoardViewTransformIsTheRemapFollowedByTheNinetyDegreeTurn()
    {
        StaRunner.Run(() => AssertMath.Matrix(
            new Matrix3D(
                 0, -1, 0, 0,
                 0, 0, -1, 0,
                 1, 0, 0, 0,
                 0, 0, 0, 1),
            BoardViewTransform(),
            Tolerance,
            "board view transform"));
    }

    /// <summary>
    /// The exact composed matrix for the sensor viewport: the 180° turn about X followed by the same
    /// ninety-degree turn. Pinned alongside the board matrix so the two cannot drift silently.
    /// </summary>
    [Fact]
    public void DisconnectedSensorViewTransformIsTheHalfTurnFollowedByTheNinetyDegreeTurn()
    {
        StaRunner.Run(() => AssertMath.Matrix(
            new Matrix3D(
                 0, 0, -1, 0,
                 0, -1, 0, 0,
                -1, 0, 0, 0,
                 0, 0, 0, 1),
            SensorViewTransform(),
            Tolerance,
            "sensor view transform"));
    }

    /// <summary>
    /// The composed matrix is the two literals applied in the order the group lists them, so the order
    /// Transform3DGroup applies its children is pinned rather than assumed.
    ///
    /// WHY THIS IS NOT AN IDENTITY THAT HOLDS BY CONSTRUCTION: the production code builds a
    /// Transform3DGroup and reads its Value; the expected value here is built by applying the two literals
    /// one after the other to basis vectors. If WPF composed the children in the other order, the pinned
    /// matrices above would be wrong and this is the test that says so.
    /// </summary>
    [Fact]
    public void ViewportTransformIsTheFirstChildAppliedFirst()
    {
        StaRunner.Run(() =>
        {
            AssertMath.Matrix(Product(ModelRemap, TurnAboutY), BoardViewTransform(), Tolerance, "board: remap then turn");
            AssertMath.Matrix(Product(HalfTurnAboutX, TurnAboutY), SensorViewTransform(), Tolerance, "sensor: half turn then turn");
        });
    }

    /// <summary>
    /// THE OTHER HALF OF §12.1's BOARD VIEW ROW: the green slab itself, not just the gizmo's letters.
    ///
    /// §12.1's row says "Green board plane". Measured, the slab is 0.400 x 0.250 x 0.030 in MODEL space —
    /// thin along model Z, so its face normal is the optical axis (WorldFrame.SensorLookAxis) — and the board
    /// viewport's remap moves that normal to viewport -Y. The slab's two in-plane extents land on viewport Z
    /// (0.400) and viewport X (0.250).
    ///
    /// WHY THE SLAB IS THIN ON Z AND NOT ON Y. A VL53L5CX looks perpendicular to the board it is mounted on,
    /// so the board's face normal must be the optical axis. The slab used to be thin on model Y, which made
    /// the Y arm poke out of the board face while the sensor was documented to look along Z — a gizmo whose
    /// normal axis (Y) contradicted its look axis (Z). Making the slab thin on Z makes the arm perpendicular
    /// to the face the Z arm, which is what reads as "out of the front face".
    ///
    /// WHY THAT MATTERS FOR A SCREENSHOT EXPECTATION. The BoardViewport camera sits at (0.9, 0.7, 1.3)
    /// looking at the origin, so its line of sight is dominated by the Z and Y directions and is close to
    /// PERPENDICULAR to the slab's normal. A face whose normal is nearly perpendicular to the line of sight is
    /// seen nearly edge-on. So "green board plane" is true of the model and only partly true of the picture:
    /// the slab is there, and it is foreshortened. A gate image described as showing a board PLANE will
    /// otherwise be diffed against a picture showing a sliver, and the difference will be read as a regression
    /// when it is a consequence of the remap that §12.12 says 5d deliberately left alone.
    ///
    /// Asserted from the produced mesh and the produced transform, not from the literals in MeshFactory: the
    /// extents are read off the mesh corners, and the axis mapping is read off the composed matrix.
    /// </summary>
    [Fact]
    public void BoardSlabIsThinAlongTheOpticalAxis_AndTheRemapTurnsThatNormalSidewaysInView()
    {
        StaRunner.Run(() =>
        {
            GeometryModel3D board = MeshFactory.CreateBoardModel();
            var mesh = Assert.IsAssignableFrom<MeshGeometry3D>(board.Geometry!);
            // Flat shading (Phase 7) gives each of the 12 triangles its own three vertices, so a box is
            // 36 positions rather than the 8 shared corners it had before. The extents below are unchanged:
            // duplicating a vertex does not move it. See MeshFactoryNormalsTests for the normal assertions.
            Assert.Equal(36, mesh.Positions.Count);

            double extentX = Extent(mesh, p => p.X);
            double extentY = Extent(mesh, p => p.Y);
            double extentZ = Extent(mesh, p => p.Z);

            // The slab as built: wide along X, deep along Y, thin along Z. Its face normal is model Z, the
            // optical axis, so the arm perpendicular to the board face is the Z arm.
            AssertMath.Close(0.400, extentX, Tolerance, "board slab: model X extent");
            AssertMath.Close(0.250, extentY, Tolerance, "board slab: model Y extent");
            AssertMath.Close(0.030, extentZ, Tolerance, "board slab: model Z extent (the thin one, i.e. the face normal)");

            Matrix3D view = BoardViewTransform();

            // Where each model axis ends up in the viewport, so the slab's viewport extents follow.
            AssertDirection("slab normal", AssertMath.Transform(view, WorldFrame.South), WorldFrame.East, "face normal -> viewport +X");
            AssertDirection("slab width", AssertMath.Transform(view, WorldFrame.East), Down, "0.400 extent -> viewport Y");
            AssertDirection("slab depth", AssertMath.Transform(view, WorldFrame.Up), WorldFrame.North, "0.250 extent -> viewport Z");

            // So in the viewport the slab is thin along Y and broad along X and Z: a face pointing sideways,
            // which the camera at (0.9, 0.7, 1.3) sees strongly foreshortened.
            Vec3 cameraToOrigin = new Vec3(-0.9, -0.7, -1.3).Normalized();
            Vec3 faceNormalInView = AssertMath.Transform(view, WorldFrame.South);
            double facing = Math.Abs(AssertMath.Dot(faceNormalInView, cameraToOrigin));

            // facing is |cos(angle between the face normal and the line of sight)|. Near 1 means the face
            // looks straight at the camera; near 0 means it is edge-on. Asserted as a range rather than a
            // number because it is a statement about what a screenshot can be expected to show.
            Assert.True(facing < 0.6,
                $"the board face is {facing:0.###} of the way toward facing the camera; the row must say whether the slab reads as a plane or as a sliver.");
        });
    }

    private static double Extent(MeshGeometry3D mesh, Func<Point3D, double> component)
    {
        double min = double.MaxValue;
        double max = double.MinValue;
        foreach (Point3D p in mesh.Positions)
        {
            min = Math.Min(min, component(p));
            max = Math.Max(max, component(p));
        }

        return max - min;
    }

    // -----------------------------------------------------------------------------------------
    // The two viewports disagree, and that disagreement is a decision rather than an accident.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// THE DISAGREEMENT, PINNED SO IT IS A DECISION AND NOT DRIFT.
    ///
    /// The board viewport remaps; the sensor viewport half-turns. The two composed matrices are therefore
    /// different and world axes land on different directions in the two views. §12.12 records that closing
    /// this is a GEOMETRY change that 5d deliberately did not make, and Finding 6 is the on-screen evidence
    /// that it is visible on real hardware.
    ///
    /// Asserting the difference is not endorsing it. It is what stops a later tidy-up from making the two
    /// viewports agree by accident. If the remap is ever removed, it should be its own commit, and this test
    /// is the one that says so.
    /// </summary>
    [Fact]
    public void TheTwoViewportsApplyDifferentTransforms_AndThatIsRecordedNotFixed()
    {
        StaRunner.Run(() =>
        {
            Matrix3D board = BoardViewTransform();
            Matrix3D sensor = SensorViewTransform();

            Assert.NotEqual(board, sensor);

            // The disagreement is specifically about where world up goes, because that is what a viewer
            // notices: the same physical board appears oriented differently in the two windows.
            Vec3 boardUp = AssertMath.Transform(board, WorldFrame.Up);
            Vec3 sensorUp = AssertMath.Transform(sensor, WorldFrame.Up);

            AssertDirection("board view up", boardUp, WorldFrame.North, "board view: world up -> north");
            AssertDirection("sensor view up", sensorUp, Down, "sensor view: world up -> down");
        });
    }

    /// <summary>
    /// In the board viewport the fixed world reference axes carry NO transform while the gizmo carries the
    /// remap, so one window can contain two axis sets pointing different ways — and §12.1's row has to say
    /// which of the two it is describing.
    ///
    /// THE ASSERTION IS ABOUT THE SCENE GRAPH, NOT ABOUT A MATRIX COMPARED TO ITSELF. boardRoot is built in
    /// MainWindow and is unreachable from a test, so what is falsifiable here is the source fact: the
    /// reference axes are added to a group that is never given a transform, while the gizmo's group is.
    /// Asserting "the untransformed axes are untransformed" by comparing a constant to itself would be the
    /// decoration §4's test-that-asserts-nothing entry exists to warn against; asserting that the code never
    /// transforms that group is a claim that can fail.
    /// </summary>
    [Fact]
    public void BoardViewReferenceAxesAreUntransformed_WhileTheGizmoCarriesTheRemap()
    {
        StaRunner.Run(() =>
        {
            string appSource = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "EchoEnvironment", "MainWindow.xaml.cs"));

            // The reference axes live in boardRoot, and boardRoot is never given a transform.
            Assert.Contains("boardRoot.Children.Add(ScenePrimitives.CreateWorldAxes());", appSource, StringComparison.Ordinal);
            Assert.DoesNotContain("boardRoot.Transform", appSource, StringComparison.Ordinal);

            // The gizmo's group is.
            Assert.Contains("_boardGroup.Transform = _orbit.CreateViewTransform(true, _orientation);", appSource, StringComparison.Ordinal);

            // So inside one viewport: the reference axes keep their world directions, and the gizmo's arms
            // do not. If the gizmo's arms agreed with the world directions too, the remap would have
            // silently disappeared.
            Matrix3D view = BoardViewTransform();
            AssertDirection("gizmo X", AssertMath.Transform(view, WorldFrame.East), Down, "gizmo X moved off +X");
            AssertDirection("gizmo Y", AssertMath.Transform(view, WorldFrame.Up), WorldFrame.North, "gizmo Y moved off +Y");
            AssertDirection("gizmo Z", AssertMath.Transform(view, WorldFrame.South), WorldFrame.East, "gizmo Z moved off +Z");
        });
    }

    /// <summary>
    /// The orientation is applied between the remap and the ninety-degree turn, so a live attitude rotates
    /// the gizmo rather than being multiplied in after the fact.
    ///
    /// WHY THIS IS TESTED AT ALL: §12.10 Finding 6 is only explicable if the orientation sits inside the
    /// chain. With Identity the chain is indistinguishable — Identity multiplied anywhere is the same
    /// matrix — so every assertion above would pass even if the orientation were applied in the wrong
    /// place. A non-Identity orientation is what makes its position in the chain observable, and this is the
    /// only test that supplies one.
    ///
    /// THE ORIENTATION IS A 90° TURN ABOUT THE SENSOR'S OWN Z AXIS, chosen because it does not commute with
    /// the final turn. An orientation that commuted would make the two orderings identical and the test
    /// would assert nothing.
    /// </summary>
    [Fact]
    public void OrientationIsAppliedBetweenTheRemapAndTheTurn_NotAfterBoth()
    {
        StaRunner.Run(() =>
        {
            var orientation = new MatrixTransform3D(new Matrix3D(
                 0, 1, 0, 0,
                -1, 0, 0, 0,
                 0, 0, 1, 0,
                 0, 0, 0, 1));

            Matrix3D actual = CreateController().CreateViewTransform(remapModel: true, orientation).Value;

            // The chain as written: remap -> orientation -> turn.
            Matrix3D expected = Product(Product(ModelRemap, orientation.Matrix), TurnAboutY);
            AssertMath.Matrix(expected, actual, Tolerance, "board view with attitude: remap, orientation, turn");

            // The same rotation applied LAST is a different matrix, so the assertion above is falsifiable in
            // the direction that matters rather than merely consistent.
            Matrix3D orientationLast = Product(Product(ModelRemap, TurnAboutY), orientation.Matrix);
            Assert.NotEqual(orientationLast, actual);

            // And the attitude demonstrably changed something: with a non-Identity orientation the board
            // transform is no longer the disconnected one §12.1's row describes.
            Assert.NotEqual(BoardViewTransform(), actual);

            // Still a rotation with the attitude folded in — an orientation that produced a reflection would
            // mirror the board rather than tilt it.
            AssertMath.Close(1.0, RotationDeterminant(actual), Tolerance, "board view with attitude: determinant");
        });
    }

    // -----------------------------------------------------------------------------------------
    // Helpers.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Asserts a measured direction component-for-component, with a message that reads as a sentence about
    /// the viewport rather than as three numbers.
    /// </summary>
    private static void AssertDirection(string what, Vec3 measured, Vec3 expected, string reading)
    {
        AssertMath.Close(expected.X, measured.X, Tolerance, $"{what}: {reading} (X)");
        AssertMath.Close(expected.Y, measured.Y, Tolerance, $"{what}: {reading} (Y)");
        AssertMath.Close(expected.Z, measured.Z, Tolerance, $"{what}: {reading} (Z)");
    }

    private static Vec3 Row(Matrix3D m, int row) => row switch
    {
        1 => new Vec3(m.M11, m.M12, m.M13),
        2 => new Vec3(m.M21, m.M22, m.M23),
        3 => new Vec3(m.M31, m.M32, m.M33),
        _ => throw new ArgumentOutOfRangeException(nameof(row), row, "Matrix3D has three rows."),
    };

    /// <summary>
    /// Determinant of the 3x3 rotation part. Computed here rather than in AssertMath because nothing in
    /// production needs it: it exists only to tell a rotation from a reflection.
    /// </summary>
    private static double RotationDeterminant(Matrix3D m)
    {
        Vec3 a = Row(m, 1);
        Vec3 b = Row(m, 2);
        Vec3 c = Row(m, 3);

        return a.X * (b.Y * c.Z - b.Z * c.Y)
             - a.Y * (b.X * c.Z - b.Z * c.X)
             + a.Z * (b.X * c.Y - b.Y * c.X);
    }

    /// <summary>
    /// The matrix that applies `first` and then `second`, built by applying the two to the basis vectors
    /// through WPF's own Matrix3D.Transform and re-assembling the rows.
    ///
    /// WHY IT IS BUILT THIS INSTEAD OF WITH Matrix3D.Multiply. Matrix3D.Multiply's operand order is the
    /// single most likely place for a test to encode the opposite convention from the production code — and
    /// a test that composes matrices the wrong way still passes whenever the two operands happen to
    /// commute. Applying each matrix to a vector is the operation production actually performs, so building
    /// the product from those results cannot disagree with production about what the matrix means. Row j of
    /// the result is the image of basis vector j, which is the row-vector convention (v' = v * M) the whole
    /// codebase uses and that AssertMath.Transform documents.
    /// </summary>
    private static Matrix3D Product(Matrix3D first, Matrix3D second)
    {
        Vec3 e1 = new(1, 0, 0);
        Vec3 e2 = new(0, 1, 0);
        Vec3 e3 = new(0, 0, 1);

        Vec3 r1 = AssertMath.Transform(second, AssertMath.Transform(first, e1));
        Vec3 r2 = AssertMath.Transform(second, AssertMath.Transform(first, e2));
        Vec3 r3 = AssertMath.Transform(second, AssertMath.Transform(first, e3));

        return new Matrix3D(
            r1.X, r1.Y, r1.Z, 0,
            r2.X, r2.Y, r2.Z, 0,
            r3.X, r3.Y, r3.Z, 0,
            0, 0, 0, 1);
    }

    /// <summary>
    /// Walks up from this test assembly to the repository root, the same anchor
    /// ArchitectureInvariantTests and FrameCoalescerTests use. Duplicated rather than shared for the reason
    /// those two files already record: making one guard depend on another test class's private helper couples
    /// unrelated guards to one file's internals.
    /// </summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(typeof(OrbitCameraControllerTests).Assembly.Location)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EchoEnvironment.Tests.csproj")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        string? repoRoot = directory!.Parent?.FullName;
        Assert.NotNull(repoRoot);

        return repoRoot!;
    }
}
