using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using EchoEnvironment.Core;
using EchoEnvironment.Rendering;
using Xunit;

namespace EchoEnvironment.Tests;

/// <summary>
/// Tests for the axis gizmos - the fix for defect D9 (MIGRATION_PLAN 5d).
///
/// WHAT WAS WRONG, AND WHAT IS PROVABLE WITHOUT A REFERENCE FRAME. ScenePrimitives built its labelled
/// gizmo from hand-written literals, and read as drawing rather than as intent it produced:
///
///   a blue arm drawn along -Z, labelled "Y"
///   a green arm drawn along +Y, labelled "Z"
///
/// That is provable from the source alone. No knowledge of the world frame is needed to see that an arm
/// drawn along one axis and labelled with a different axis is wrong. So the central assertion here is a
/// pairing assertion: for every arm, the axis the arm is physically long along must be the axis the label
/// attached to it names. Before 5d that assertion would have failed on two of three arms.
///
/// WHAT THESE TESTS DELIBERATELY DO NOT CLAIM. They do not claim the two gizmos point the same way on
/// screen, because they do not and should not: AddAxes lives in _boardGroup, which carries the sensor's
/// attitude and rotates, while CreateWorldAxes lives in boardRoot, which is untransformed. A sensor gizmo
/// that pointed where the world gizmo points would be lying about the sensor. What 5d fixes is that a
/// letter means one axis and a colour means one letter, everywhere in this file. See AxisConvention.
///
/// WHY EVERY TEST RUNS ON AN STA THREAD. CreateAxisLabel builds a WPF Grid containing a TextBlock and
/// paints it into the model through a VisualBrush, so an axis label is a FrameworkElement in disguise.
/// Reading the letter back out of a label therefore requires the apartment that created it. That is not a
/// quirk of the test: it means the gizmo's labelling is the apartment-affine half of ScenePrimitives,
/// while AddLine and the boxes are not.
/// </summary>
public class ScenePrimitivesTests
{
    private const double GizmoLength = 0.5;
    private const double GizmoWidth = 0.01;

    private static readonly Color DarkX = Color.FromRgb(120, 40, 40);
    private static readonly Color DarkY = Color.FromRgb(40, 120, 40);
    private static readonly Color DarkZ = Color.FromRgb(40, 80, 120);

    // -----------------------------------------------------------------------------------------
    // The provable half of D9: an arm and the label attached to it must name the same axis.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// THE CENTRAL ASSERTION. AddAxes emits arm, label, arm, label, arm, label. For each pair, the axis
    /// the arm is physically long along - measured from the eight corners of its mesh, not from the code
    /// that built it - must be the axis the label's own text names.
    ///
    /// Before 5d this failed twice: the arm long along +Y carried the text "Z", and the arm long along -Z
    /// carried the text "Y".
    /// </summary>
    [Fact]
    public void EveryAxisArmIsLabelledWithTheAxisItIsActuallyDrawnAlong()
    {
        StaRunner.Run(() =>
        {
            var group = new Model3DGroup();
            ScenePrimitives.AddAxes(group, GizmoLength, GizmoWidth);

            Assert.Equal(6, group.Children.Count);

            var axes = AxisConvention.WorldAxes;
            for (int i = 0; i < axes.Count; i++)
            {
                AxisLabel axis = axes[i];

                var arm = Assert.IsAssignableFrom<GeometryModel3D>(group.Children[i * 2]);
                var labelModel = Assert.IsAssignableFrom<GeometryModel3D>(group.Children[i * 2 + 1]);

                string label = LabelTextOf(labelModel);
                BoxShape armShape = Measure(arm);
                BoxShape labelShape = Measure(labelModel);

                Assert.Equal(axis.Letter, label);
                Assert.Equal(axis.Letter, LongAxisOf(armShape));

                // The arm's measured end must agree with the convention's direction SCALED BY THE LENGTH
                // THE GIZMO WAS BUILT AT. AxisConvention.Direction is a unit vector; the arm is GizmoLength
                // long. The first version of this assertion compared the unit component (1) against the
                // measured end (0.5) and failed with "expected 1, got 0.5" - a bug in the assertion, not in
                // the gizmo, and the kind of bug that makes a passing geometry test look like a real defect.
                // The load-bearing part of the assertion is the SIGN and the AXIS: which side of the origin
                // the arm sits on, and which axis it is long along.
                Vector3D armEnd = SignedEndOf(armShape, LongAxisOf(armShape));
                AssertMath.Close(axis.Direction.X * GizmoLength, armEnd.X, AssertMath.Geometry, $"{axis.Letter}: arm end X");
                AssertMath.Close(axis.Direction.Y * GizmoLength, armEnd.Y, AssertMath.Geometry, $"{axis.Letter}: arm end Y");
                AssertMath.Close(axis.Direction.Z * GizmoLength, armEnd.Z, AssertMath.Geometry, $"{axis.Letter}: arm end Z");

                // The label sits on the same side of the origin as the arm it labels, so a reader follows
                // the arm to its letter rather than to the opposite end.
                AssertLabelOnSameSideAsArm(axis, labelShape, armShape);
            }
        });
    }

    /// <summary>
    /// The Z arm's move, stated as a measured fact rather than left implicit. AddAxes used to draw Z along
    /// -Z, which in this world frame is the NEGATIVE end of the Z axis (-Z is north, WorldFrame.North), so
    /// the gizmo labelled its own negative end with the bare letter "Z". Every arm now runs to its positive
    /// end. This is the pixel change 5d is allowed to make, and it is asserted so the change is on the
    /// record instead of discoverable only by looking.
    /// </summary>
    [Fact]
    public void ZArmNowPointsAlongPositiveZ_South_AndNoArmPointsAtNegativeZ()
    {
        StaRunner.Run(() =>
        {
            var group = new Model3DGroup();
            ScenePrimitives.AddAxes(group, GizmoLength, GizmoWidth);

            BoxShape zArm = Measure(Assert.IsAssignableFrom<GeometryModel3D>(group.Children[4]));

            Assert.Equal("Z", LongAxisOf(zArm));
            Assert.True(zArm.CenterZ > 0,
                $"the Z arm is centred at Z={zArm.CenterZ:0.###}; it must sit on the positive Z side, which is south in this world frame.");

            // Nothing in the gizmo may still be drawn at -Z, because -Z is north and no axis is named "north".
            foreach (GeometryModel3D child in group.Children.Cast<GeometryModel3D>())
            {
                BoxShape shape = Measure(child);
                Assert.False(shape.CenterZ < 0, $"a gizmo element is centred at Z={shape.CenterZ:0.###}; the -Z side is north and no arm should sit there.");
            }
        });
    }

    // -----------------------------------------------------------------------------------------
    // The two gizmos must agree on what a letter and a colour mean.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// THE DISAGREEMENT THE PLAN NAMED. AddAxes said DeepSkyBlue is the letter Y. CreateWorldAxes said
    /// blue is +Z. Both now read AxisConvention, so blue is Z in both, and the colours of the fixed world
    /// reference axes are the muted versions of the same three letters.
    /// </summary>
    [Fact]
    public void BothGizmosUseOneLetterToColourMapping()
    {
        StaRunner.Run(() =>
        {
            var gizmo = new Model3DGroup();
            ScenePrimitives.AddAxes(gizmo, GizmoLength, GizmoWidth);
            Model3DGroup reference = ScenePrimitives.CreateWorldAxes();

            var axes = AxisConvention.WorldAxes;
            for (int i = 0; i < axes.Count; i++)
            {
                AxisLabel axis = axes[i];

                var gizmoArm = Assert.IsAssignableFrom<GeometryModel3D>(gizmo.Children[i * 2]);
                var referenceArm = Assert.IsAssignableFrom<GeometryModel3D>(reference.Children[i]);

                Assert.Equal(axis.GizmoColor, ColorOf(gizmoArm));
                Assert.Equal(axis.ReferenceColor, ColorOf(referenceArm));

                // The same letter, in the same order, in both gizmos.
                Assert.Equal(axis.Letter, LongAxisOf(Measure(gizmoArm)));
                Assert.Equal(axis.Letter, LongAxisOf(Measure(referenceArm)));
            }

            // Three distinct letters must be three distinct colours, in both gizmos and between them, or
            // "one convention" would only mean "one convention that is unreadable".
            Color[] gizmoColours = [AxisConvention.X.GizmoColor, AxisConvention.Y.GizmoColor, AxisConvention.Z.GizmoColor];
            Color[] referenceColours = [AxisConvention.X.ReferenceColor, AxisConvention.Y.ReferenceColor, AxisConvention.Z.ReferenceColor];

            Assert.Equal(3, gizmoColours.Distinct().Count());
            Assert.Equal(3, referenceColours.Distinct().Count());
            Assert.Empty(gizmoColours.Intersect(referenceColours));
        });
    }

    /// <summary>
    /// The convention derives from Core's WorldFrame and not from a second copy of the numbers. This is
    /// the acceptance criterion MIGRATION_PLAN 5 lists: "Main view and board view use one axis convention.
    /// WorldFrame now exists as the single source of truth, but AddAxes and CreateWorldAxes have not yet
    /// been rewired to it" - rewiring is what this asserts.
    /// </summary>
    [Fact]
    public void AxisConventionDerivesEveryDirectionFromWorldFrame()
    {
        AssertMath.Close(WorldFrame.East.X, AxisConvention.X.Direction.X, AssertMath.Exact, "X direction is WorldFrame.East, X");
        AssertMath.Close(WorldFrame.East.Y, AxisConvention.X.Direction.Y, AssertMath.Exact, "X direction is WorldFrame.East, Y");
        AssertMath.Close(WorldFrame.East.Z, AxisConvention.X.Direction.Z, AssertMath.Exact, "X direction is WorldFrame.East, Z");

        AssertMath.Close(WorldFrame.Up.X, AxisConvention.Y.Direction.X, AssertMath.Exact, "Y direction is WorldFrame.Up, X");
        AssertMath.Close(WorldFrame.Up.Y, AxisConvention.Y.Direction.Y, AssertMath.Exact, "Y direction is WorldFrame.Up, Y");
        AssertMath.Close(WorldFrame.Up.Z, AxisConvention.Y.Direction.Z, AssertMath.Exact, "Y direction is WorldFrame.Up, Z");

        // Z is SOUTH, because north is -Z in this frame. The same reason Mat3.CreateOrientation negates its
        // north column and WorldMap measures azimuth as atan2(X, -Z).
        AssertMath.Close(WorldFrame.South.Z, AxisConvention.Z.Direction.Z, AssertMath.Exact, "Z direction is WorldFrame.South");
        AssertMath.Close(1, AxisConvention.Z.Direction.Z, AssertMath.Exact, "Z must point at +Z, not -Z");

        Assert.Equal(3, AxisConvention.WorldAxes.Count);
        Assert.Equal(new[] { "X", "Y", "Z" }, AxisConvention.WorldAxes.Select(a => a.Letter).ToArray());
    }

    /// <summary>
    /// The gizmo arms are long along their own axis and thin across it - measured from the mesh, not from
    /// the arguments that built it. A box that is long in the wrong dimension renders as a plausible gizmo
    /// pointing at the wrong axis, which is the failure mode the BoxExtents helper exists to make loud.
    /// </summary>
    [Fact]
    public void GizmoArmsAreLongAlongTheirOwnAxis_AndThinAcrossIt()
    {
        StaRunner.Run(() =>
        {
            var group = new Model3DGroup();
            ScenePrimitives.AddAxes(group, GizmoLength, GizmoWidth);

            for (int i = 0; i < 3; i++)
            {
                BoxShape shape = Measure(Assert.IsAssignableFrom<GeometryModel3D>(group.Children[i * 2]));
                string axis = LongAxisOf(shape);

                AssertMath.Close(GizmoLength, ExtentAlong(shape, axis), AssertMath.Geometry, $"{axis}: extent along its own axis");
                AssertMath.Close(GizmoWidth, ExtentAlong(shape, OtherAxis(axis, 0)), AssertMath.Geometry, $"{axis}: first perpendicular extent");
                AssertMath.Close(GizmoWidth, ExtentAlong(shape, OtherAxis(axis, 1)), AssertMath.Geometry, $"{axis}: second perpendicular extent");

                // The arm runs from the origin to `length` along its own axis: its midpoint is at
                // length / 2, and the end nearest the origin is the origin itself. An arm shifted off the
                // origin would render as a gizmo floating away from the board it labels.
                AssertMath.Close(GizmoLength / 2, MidPointOf(shape, axis), AssertMath.Geometry, $"{axis}: arm midpoint");
                AssertMath.Close(0, NearOriginOf(shape, axis), AssertMath.Geometry, $"{axis}: arm starts at the origin");
            }
        });
    }

    /// <summary>
    /// THE WORLD REFERENCE AXES ARE PIXEL-FOR-PIXEL WHAT THEY WERE BEFORE 5d, and that is worth pinning
    /// because it narrows the visual comparison a human has to make. 5d moved the labelled gizmo. It did
    /// not move the fixed reference axes: same three positions, same three extents, same three colours. So
    /// a screenshot diff after 5d should show the gizmo changed and the reference frame unchanged, and if
    /// the reference frame moved too, that is a regression rather than the intended change.
    /// </summary>
    [Fact]
    public void WorldReferenceAxesAreUnchangedByTheRewrite()
    {
        StaRunner.Run(() =>
        {
            Model3DGroup group = ScenePrimitives.CreateWorldAxes();

            Assert.Equal(3, group.Children.Count);

            var expected = new[]
            {
                (Letter: "X", Color: DarkX, Center: new Point3D(0.75, 0, 0)),
                (Letter: "Y", Color: DarkY, Center: new Point3D(0, 0.75, 0)),
                (Letter: "Z", Color: DarkZ, Center: new Point3D(0, 0, 0.75)),
            };

            for (int i = 0; i < expected.Length; i++)
            {
                var arm = Assert.IsAssignableFrom<GeometryModel3D>(group.Children[i]);
                BoxShape shape = Measure(arm);

                Assert.Equal(expected[i].Letter, LongAxisOf(shape));
                Assert.Equal(expected[i].Color, ColorOf(arm));
                AssertMath.Close(expected[i].Center.X, shape.CenterX, AssertMath.Geometry, $"{expected[i].Letter}: centre X");
                AssertMath.Close(expected[i].Center.Y, shape.CenterY, AssertMath.Geometry, $"{expected[i].Letter}: centre Y");
                AssertMath.Close(expected[i].Center.Z, shape.CenterZ, AssertMath.Geometry, $"{expected[i].Letter}: centre Z");

                AssertMath.Close(1.5, ExtentAlong(shape, expected[i].Letter), AssertMath.Geometry, $"{expected[i].Letter}: length");
                AssertMath.Close(0.004, ExtentAlong(shape, OtherAxis(expected[i].Letter, 0)), AssertMath.Geometry, $"{expected[i].Letter}: thickness");
                AssertMath.Close(0.004, ExtentAlong(shape, OtherAxis(expected[i].Letter, 1)), AssertMath.Geometry, $"{expected[i].Letter}: thickness");
            }
        });
    }

    /// <summary>
    /// The convention is addressable by letter, which is what lets a reader (or a later test) ask what "Z"
    /// means without reading the initialisers.
    /// </summary>
    [Fact]
    public void ConventionAnswersAQuestionAboutALetter()
    {
        Assert.Equal("X", AxisConvention.ByLetter("X").Letter);
        Assert.Equal("Y", AxisConvention.ByLetter("Y").Letter);
        Assert.Equal("Z", AxisConvention.ByLetter("Z").Letter);

        Assert.Throws<System.Collections.Generic.KeyNotFoundException>(() => AxisConvention.ByLetter("W"));
        Assert.ThrowsAny<System.ArgumentException>(() => AxisConvention.ByLetter(""));
    }

    /// <summary>
    /// AddAxes and CreateWorldAxes reject a null group rather than failing later inside WPF's scene graph.
    /// </summary>
    [Fact]
    public void AddAxesRejectsANullGroup()
    {
        StaRunner.Run(() => Assert.Throws<System.ArgumentNullException>(() => ScenePrimitives.AddAxes(null!, GizmoLength, GizmoWidth)));
    }

    /// <summary>
    /// THE NEGATIVE CONTROL: the detector above is run against the gizmo as it was built BEFORE 5d, and it
    /// must reject it.
    ///
    /// WHY THIS TEST EXISTS. Every other test in this file now passes. That alone does not prove the tests
    /// would have caught the defect they were written for - a test written after a fix can pass for reasons
    /// unrelated to the fix. So this test rebuilds the pre-5d gizmo from the literals ScenePrimitives used
    /// to contain, measures it with the same helpers, and asserts the pairing breaks on exactly two of the
    /// three arms. If someone weakens the pairing assertion until it tolerates a mislabelled arm, this test
    /// is the one that says so.
    ///
    /// THE TWO FAILURES IT MUST FIND, and which is which:
    ///   - the arm long along Z (the one built with `length` as its Z extent) carries the text "Y"
    ///   - the arm long along Y (the one built with `length` as its Y extent) carries the text "Z"
    /// The X arm is correct in the old gizmo, which is why the defect survived so long: two wrong labels out
    /// of three still looks like a working axis gizmo at a glance.
    ///
    /// WHY THIS IS NOT A TEST OF ScenePrimitives. It builds the old shape inline and never calls AddAxes. It
    /// tests the measurement, not the production code. It is here rather than in a scratch file because a
    /// detector with no demonstrated failure case is an assertion about a defect nobody can confirm existed.
    /// </summary>
    [Fact]
    public void ThePairingDetectorRejectsThePreFixGizmo()
    {
        StaRunner.Run(() =>
        {
            // ScenePrimitives.AddAxes verbatim as it stood before 5d, reproduced from the literals quoted in
            // ScenePrimitives' own doc comment.
            var legacy = new Model3DGroup();
            double h = GizmoLength / 2;

            legacy.Children.Add(MeshFactory.CreateColoredBox(new Point3D(h, 0, 0), GizmoLength, GizmoWidth, GizmoWidth, Colors.Red));
            legacy.Children.Add(MeshFactory.CreateColoredBox(new Point3D(0, 0, -h), GizmoWidth, GizmoWidth, GizmoLength, Colors.DeepSkyBlue));
            legacy.Children.Add(MeshFactory.CreateColoredBox(new Point3D(0, h, 0), GizmoWidth, GizmoLength, GizmoWidth, Colors.LimeGreen));
            legacy.Children.Add(ScenePrimitives.CreateAxisLabel("X", new Point3D(GizmoLength + 0.08, 0, 0), Colors.Red));
            legacy.Children.Add(ScenePrimitives.CreateAxisLabel("Y", new Point3D(0, 0, -(GizmoLength + 0.08)), Colors.DeepSkyBlue));
            legacy.Children.Add(ScenePrimitives.CreateAxisLabel("Z", new Point3D(0, GizmoLength + 0.08, 0), Colors.LimeGreen));

            // NOTE THE LAYOUT DIFFERENCE, which is itself part of what changed. AddAxes now emits arm, label,
            // arm, label, arm, label; the pre-5d gizmo appended all three arms and then all three labels. The
            // first version of this test reused AddAxes' interleaved indexing against the legacy layout and
            // read an ARM where it expected a label, failing with "Expected EmissiveMaterial, Actual
            // DiffuseMaterial". That failure is a useful accident: it shows the two layouts are genuinely
            // different, and that a test which assumes a scene graph's ordering will read the wrong object.
            var mismatches = new System.Collections.Generic.List<string>();
            for (int i = 0; i < 3; i++)
            {
                var arm = Assert.IsAssignableFrom<GeometryModel3D>(legacy.Children[i]);
                string label = LabelTextOf(Assert.IsAssignableFrom<GeometryModel3D>(legacy.Children[i + 3]));

                string drawnAxis = LongAxisOf(Measure(arm));
                if (drawnAxis != label)
                {
                    mismatches.Add($"arm drawn along {drawnAxis} labelled \"{label}\"");
                }
            }

            Assert.Equal(2, mismatches.Count);
            Assert.Contains("arm drawn along Z labelled \"Y\"", mismatches);
            Assert.Contains("arm drawn along Y labelled \"Z\"", mismatches);

            // And where the two mislabelled arms physically sat, because that is the half a reader sees: the
            // arm labelled "Y" was drawn at -Z, which is north - the side no axis is named for.
            BoxShape zLabelledArm = Measure(Assert.IsAssignableFrom<GeometryModel3D>(legacy.Children[2]));
            Assert.Equal("Y", LongAxisOf(zLabelledArm));
            Assert.True(zLabelledArm.CenterY > 0,
                $"the arm labelled \"Z\" sat at Y={zLabelledArm.CenterY:0.###}; the pre-5d gizmo drew it at +Y, i.e. up.");

            BoxShape yLabelledArm = Measure(Assert.IsAssignableFrom<GeometryModel3D>(legacy.Children[1]));
            Assert.Equal("Z", LongAxisOf(yLabelledArm));
            Assert.True(yLabelledArm.CenterZ < 0,
                $"the arm labelled \"Y\" sat at Z={yLabelledArm.CenterZ:0.###}; the pre-5d gizmo drew it at -Z, i.e. north.");

            // The pre-5d colours are the same three colours the convention still uses - so the colour half of
            // D9 was never about WHICH colours, it was about which LETTER a colour was attached to.
            Assert.Equal(Colors.DeepSkyBlue, ColorOf(Assert.IsAssignableFrom<GeometryModel3D>(legacy.Children[1])));
            Assert.Equal(AxisConvention.Z.GizmoColor, ColorOf(Assert.IsAssignableFrom<GeometryModel3D>(legacy.Children[1])));
        });
    }
    // -----------------------------------------------------------------------------------------
    // Measurement helpers. Everything below reads the produced geometry rather than the arguments that
    // produced it, because the defect was a mismatch between intent and drawing - and only the drawing is
    // what a user sees.
    // -----------------------------------------------------------------------------------------

    /// <summary>Min/max per axis over a box mesh's eight corners.</summary>
    private sealed record BoxShape(
        double MinX, double MaxX,
        double MinY, double MaxY,
        double MinZ, double MaxZ)
    {
        public double CenterX => (MinX + MaxX) / 2;
        public double CenterY => (MinY + MaxY) / 2;
        public double CenterZ => (MinZ + MaxZ) / 2;
    }

    /// <summary>
    /// Measures a gizmo shape's extents from its mesh corners.
    ///
    /// Deliberately permissive about the corner count, because the gizmo contains two different shapes: an
    /// arm is a box with 8 corners and an axis label is a flat quad with 4. The first version of this helper
    /// asserted 8 unconditionally and failed on the labels, which is the same class of mistake as the defect
    /// it is testing - assuming a shape is what its name suggests.
    /// </summary>
    private static BoxShape Measure(GeometryModel3D model)
    {
        var mesh = Assert.IsAssignableFrom<MeshGeometry3D>(model.Geometry!);
        Assert.True(mesh.Positions.Count >= 4, $"expected a box or a label quad; got {mesh.Positions.Count} positions.");

        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        double minZ = double.MaxValue, maxZ = double.MinValue;

        foreach (Point3D p in mesh.Positions)
        {
            minX = System.Math.Min(minX, p.X);
            maxX = System.Math.Max(maxX, p.X);
            minY = System.Math.Min(minY, p.Y);
            maxY = System.Math.Max(maxY, p.Y);
            minZ = System.Math.Min(minZ, p.Z);
            maxZ = System.Math.Max(maxZ, p.Z);
        }

        return new BoxShape(minX, maxX, minY, maxY, minZ, maxZ);
    }

    private static double ExtentAlong(BoxShape shape, string axis) => axis switch
    {
        "X" => shape.MaxX - shape.MinX,
        "Y" => shape.MaxY - shape.MinY,
        "Z" => shape.MaxZ - shape.MinZ,
        _ => throw new System.ArgumentOutOfRangeException(nameof(axis), axis, "Unknown axis."),
    };

    /// <summary>
    /// The axis a box is long along, decided by comparing its three extents. A gizmo arm is long along one
    /// axis and thin along the other two, so the maximum extent is unambiguous.
    /// </summary>
    private static string LongAxisOf(BoxShape shape)
    {
        double x = ExtentAlong(shape, "X");
        double y = ExtentAlong(shape, "Y");
        double z = ExtentAlong(shape, "Z");

        if (x > y && x > z)
        {
            return "X";
        }

        if (y > x && y > z)
        {
            return "Y";
        }

        if (z > x && z > y)
        {
            return "Z";
        }

        throw new System.InvalidOperationException(
            $"a gizmo arm is not longer along exactly one axis: extents X={x:0.####} Y={y:0.####} Z={z:0.####}.");
    }

    private static string OtherAxis(string axis, int which) => axis switch
    {
        "X" => which == 0 ? "Y" : "Z",
        "Y" => which == 0 ? "X" : "Z",
        "Z" => which == 0 ? "X" : "Y",
        _ => throw new System.ArgumentOutOfRangeException(nameof(axis), axis, "Unknown axis."),
    };


    /// <summary>
    /// The signed end of the arm along the given axis: the coordinate of whichever end is further from the
    /// origin, with its sign. This is what distinguishes an arm drawn at +Z from one drawn at -Z while both
    /// are "along Z".
    /// </summary>
    private static Vector3D SignedEndOf(BoxShape shape, string axis)
    {
        double Pick(double min, double max) => System.Math.Abs(max) >= System.Math.Abs(min) ? max : min;

        return axis switch
        {
            "X" => new Vector3D(Pick(shape.MinX, shape.MaxX), 0, 0),
            "Y" => new Vector3D(0, Pick(shape.MinY, shape.MaxY), 0),
            "Z" => new Vector3D(0, 0, Pick(shape.MinZ, shape.MaxZ)),
            _ => throw new System.ArgumentOutOfRangeException(nameof(axis), axis, "Unknown axis."),
        };
    }

    private static Color ColorOf(GeometryModel3D model)
    {
        var material = Assert.IsAssignableFrom<DiffuseMaterial>(model.Material);
        var brush = Assert.IsAssignableFrom<SolidColorBrush>(material.Brush);
        return brush.Color;
    }

    /// <summary>
    /// Reads the letter out of an axis label by walking the brush it is painted with: EmissiveMaterial ->
    /// VisualBrush -> Grid -> TextBlock.
    ///
    /// The letter is not stored on the model, so this is the only way to ask the drawn thing what it says.
    /// That is also why the label tests need an STA thread: the label's text lives in a FrameworkElement.
    /// </summary>
    private static string LabelTextOf(GeometryModel3D label)
    {
        var material = Assert.IsAssignableFrom<EmissiveMaterial>(label.Material);
        var visualBrush = Assert.IsAssignableFrom<VisualBrush>(material.Brush);
        var grid = Assert.IsAssignableFrom<Grid>(visualBrush.Visual);
        var text = Assert.IsAssignableFrom<TextBlock>(grid.Children[0]);

        return text.Text;
    }

    /// <summary>
    /// The label sits on the same side of the origin as the arm it names, and both agree with the direction
    /// the convention gives that letter. Asserted by sign, which is what a reader actually relies on when
    /// following an arm to its letter.
    /// </summary>
    private static void AssertLabelOnSameSideAsArm(AxisLabel axis, BoxShape labelShape, BoxShape armShape)
    {
        double wanted = Component(axis.Direction, axis.Letter);
        double wantedSign = System.Math.Sign(wanted);

        double armEnd = Component(SignedEndOf(armShape, axis.Letter), axis.Letter);
        Assert.True(System.Math.Sign(armEnd) == wantedSign,
            $"{axis.Letter}: the arm ends at {armEnd:0.###} along {axis.Letter}, but the convention points that axis at {wanted:0}.");

        double labelCentre = MidPointOf(labelShape, axis.Letter);
        Assert.True(System.Math.Sign(labelCentre) == wantedSign,
            $"{axis.Letter}: its label sits at {labelCentre:0.###}, the opposite side of the origin from the direction the letter names.");
    }

    /// <summary>
    /// A Vector3D component named by axis letter.
    ///
    /// WHY THIS HELPER EXISTS INSTEAD OF AN INDEXER. Vector3D exposes X, Y and Z as properties and has no
    /// indexer, so "the component for axis i" cannot be written as vector[i]. The first version of this file
    /// tried exactly that and produced three CS0021 errors. A switch is the honest spelling: it names the
    /// component it reads, and a wrong axis letter fails loudly instead of silently reading a neighbouring
    /// component.
    /// </summary>
    private static double Component(Vector3D vector, string axis) => axis switch
    {
        "X" => vector.X,
        "Y" => vector.Y,
        "Z" => vector.Z,
        _ => throw new System.ArgumentOutOfRangeException(nameof(axis), axis, "Unknown axis."),
    };

    /// <summary>
    /// The midpoint of a shape's extent along one axis - its centre in that dimension.
    ///
    /// Works for both gizmo shapes. An arm is a box whose centre sits at half its length; a label is a flat
    /// quad centred on the position CreateAxisLabel was given.
    /// </summary>
    private static double MidPointOf(BoxShape shape, string axis) => axis switch
    {
        "X" => (shape.MinX + shape.MaxX) / 2,
        "Y" => (shape.MinY + shape.MaxY) / 2,
        "Z" => (shape.MinZ + shape.MaxZ) / 2,
        _ => throw new System.ArgumentOutOfRangeException(nameof(axis), axis, "Unknown axis."),
    };

    /// <summary>
    /// The end of a shape closest to the origin along one axis. An arm that starts at the origin has this
    /// equal to zero; an arm that was shifted off the origin does not, and would render as a gizmo floating
    /// away from the board it labels.
    /// </summary>
    private static double NearOriginOf(BoxShape shape, string axis) => axis switch
    {
        "X" => System.Math.Min(System.Math.Abs(shape.MinX), System.Math.Abs(shape.MaxX)),
        "Y" => System.Math.Min(System.Math.Abs(shape.MinY), System.Math.Abs(shape.MaxY)),
        "Z" => System.Math.Min(System.Math.Abs(shape.MinZ), System.Math.Abs(shape.MaxZ)),
        _ => throw new System.ArgumentOutOfRangeException(nameof(axis), axis, "Unknown axis."),
    };
}



