using System.Windows.Media.Media3D;
using EchoEnvironment.Core;
using Xunit;
using EchoEnvironment.Rendering;

namespace EchoEnvironment.Tests;

/// <summary>
/// Round-trip tests for the Core/WPF boundary.
///
/// MIGRATION_PLAN 4 rates "Mat3 row/column convention diverges from Matrix3D -> mirrored or
/// transposed scene" as High likelihood / High impact, and names this exact test as the mitigation:
/// "Vec3 -> Mat3 -> Matrix3D applied to basis vectors must match OrientationFilter's current output
/// element-for-element."
///
/// The scene is the thing at risk, and no parser test can see it. These are the tests that stand
/// between Phase 2 and a silently mirrored 3D view.
/// </summary>
public class WpfMathTests
{
    private const double Tolerance = 1e-12;

    [Fact]
    public void ToMatrix3D_CopiesEveryElement_AndFillsTheFourthRowWithIdentity()
    {
        var m = new Mat3(1, 2, 3, 4, 5, 6, 7, 8, 9);
        Matrix3D w = m.ToMatrix3D();

        AssertMath.Close(1, w.M11, Tolerance, "M11");
        AssertMath.Close(2, w.M12, Tolerance, "M12");
        AssertMath.Close(3, w.M13, Tolerance, "M13");
        AssertMath.Close(4, w.M21, Tolerance, "M21");
        AssertMath.Close(5, w.M22, Tolerance, "M22");
        AssertMath.Close(6, w.M23, Tolerance, "M23");
        AssertMath.Close(7, w.M31, Tolerance, "M31");
        AssertMath.Close(8, w.M32, Tolerance, "M32");
        AssertMath.Close(9, w.M33, Tolerance, "M33");

        // Mat3 has no fourth row; the adapter supplies the identity's. WPF spells the translation
        // elements OffsetX/OffsetY/OffsetZ - M41/M42/M43 do not exist on Matrix3D.
        AssertMath.Close(0, w.M14, Tolerance, "M14");
        AssertMath.Close(0, w.M24, Tolerance, "M24");
        AssertMath.Close(0, w.M34, Tolerance, "M34");
        AssertMath.Close(0, w.OffsetX, Tolerance, "OffsetX");
        AssertMath.Close(0, w.OffsetY, Tolerance, "OffsetY");
        AssertMath.Close(0, w.OffsetZ, Tolerance, "OffsetZ");
        AssertMath.Close(1, w.M44, Tolerance, "M44");
    }

    [Fact]
    public void ToMatrix3D_IsNotTransposed()
    {
        // An asymmetric matrix is the only kind that can tell a transpose apart from a copy.
        var m = new Mat3(11, 12, 13, 21, 22, 23, 31, 32, 33);
        Matrix3D w = m.ToMatrix3D();

        AssertMath.Close(12, w.M12, Tolerance, "M12 must stay 12, not become 21");
        AssertMath.Close(21, w.M21, Tolerance, "M21 must stay 21, not become 12");
        AssertMath.Close(13, w.M13, Tolerance, "M13");
        AssertMath.Close(31, w.M31, Tolerance, "M31");
        AssertMath.Close(23, w.M23, Tolerance, "M23");
        AssertMath.Close(32, w.M32, Tolerance, "M32");
    }

    [Fact]
    public void Vec3RoundTrip_IsLossless()
    {
        var v = new Vec3(1.25, -3.5, 7.75);
        Vector3D w = v.ToVector3D();
        Vec3 back = w.ToVec3();

        AssertMath.Close(v.X, back.X, Tolerance, "X");
        AssertMath.Close(v.Y, back.Y, Tolerance, "Y");
        AssertMath.Close(v.Z, back.Z, Tolerance, "Z");

        AssertMath.Close(v.X, w.X, Tolerance, "Vec3 -> Vector3D X");
        AssertMath.Close(v.Y, w.Y, Tolerance, "Vec3 -> Vector3D Y");
        AssertMath.Close(v.Z, w.Z, Tolerance, "Vec3 -> Vector3D Z");
    }

    /// <summary>
    /// The behavioural half, and the one that actually protects the rendered scene: transforming a
    /// basis vector through Core must give the same world direction as transforming it through WPF.
    /// If Mat3 and Matrix3D disagreed about which axis is which, this is where it shows up.
    /// </summary>
    // Plain string literals, not nameof(...): these are switch cases in PoseMatrix, not symbols,
    // so there is nothing for nameof to resolve against.
    [Theory]
    [InlineData("FlatFacingNorth")]
    [InlineData("FlatFacingEast")]
    [InlineData("TiltedUp345")]
    [InlineData("InvertedFacingNorth")]
    [InlineData("GeneralPose")]
    public void CoreAndWpfTransformTheSameBasisVectorsToTheSameWorldDirections(string poseName)
    {
        Mat3 core = PoseMatrix(poseName);
        Matrix3D wpf = core.ToMatrix3D();

        Vec3[] basis = [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1), new(0, 0, -1)];

        for (int i = 0; i < basis.Length; i++)
        {
            Vec3 viaCore = AssertMath.Transform(core, basis[i]);
            Vec3 viaWpf = AssertMath.Transform(wpf, basis[i]);

            AssertMath.Close(viaCore.X, viaWpf.X, 1e-12, $"{poseName}: basis {i} X");
            AssertMath.Close(viaCore.Y, viaWpf.Y, 1e-12, $"{poseName}: basis {i} Y");
            AssertMath.Close(viaCore.Z, viaWpf.Z, 1e-12, $"{poseName}: basis {i} Z");
        }
    }

    /// <summary>
    /// The mirrored-scene detector. Dropping the negation on the north column, or transposing the
    /// matrix, produces a reflection or a wrong-handed scene that renders plausibly but is wrong.
    /// A right-handed sensor frame must stay right-handed after the conversion.
    /// </summary>
    [Fact]
    public void ConvertedOrientationIsARotationNotAReflection()
    {
        Mat3 core = PoseMatrix("GeneralPose");
        Matrix3D wpf = core.ToMatrix3D();

        AssertMath.Close(1, AssertMath.Determinant(core), 1e-9, "Mat3 determinant");
        AssertMath.Close(1, wpf.Determinant, 1e-9, "Matrix3D determinant");

        // Cross product of the first two rows must equal the third: right-handed, not mirrored.
        Vec3 row1 = new(core.M11, core.M12, core.M13);
        Vec3 row2 = new(core.M21, core.M22, core.M23);
        Vec3 row3 = new(core.M31, core.M32, core.M33);

        Vec3 rebuilt = Vec3.Cross(row1, row2);
        AssertMath.Close(row3.X, rebuilt.X, 1e-9, "row1 x row2 == row3, X");
        AssertMath.Close(row3.Y, rebuilt.Y, 1e-9, "row1 x row2 == row3, Y");
        AssertMath.Close(row3.Z, rebuilt.Z, 1e-9, "row1 x row2 == row3, Z");
    }

    [Fact]
    public void WorldFrameConstantsAreOrthonormalAndRightHanded()
    {
        // WorldFrame is now the single source of the axis convention. If East/Up/North ever stop
        // being a right-handed orthonormal triple, every matrix built from them is wrong.
        AssertMath.Close(1, WorldFrame.East.Length, Tolerance, "|East|");
        AssertMath.Close(1, WorldFrame.Up.Length, Tolerance, "|Up|");
        AssertMath.Close(1, WorldFrame.North.Length, Tolerance, "|North|");

        AssertMath.Close(0, AssertMath.Dot(WorldFrame.East, WorldFrame.Up), Tolerance, "East . Up");
        AssertMath.Close(0, AssertMath.Dot(WorldFrame.East, WorldFrame.North), Tolerance, "East . North");
        AssertMath.Close(0, AssertMath.Dot(WorldFrame.Up, WorldFrame.North), Tolerance, "Up . North");

        // North is -Z, not +Z. This is the convention the whole codebase depends on and the reason
        // Mat3.CreateOrientation negates its north column.
        AssertMath.Close(0, WorldFrame.North.X, Tolerance, "North.X");
        AssertMath.Close(0, WorldFrame.North.Y, Tolerance, "North.Y");
        AssertMath.Close(-1, WorldFrame.North.Z, Tolerance, "North.Z must be -1");

        // East x Up == South == -North. The first run of this test asserted East x Up == North and
        // failed with "expected -1, got 1", and the test was the thing that was wrong: +x cross +y is
        // +z by definition, so a frame with X east, Y up and Z south is simply right-handed. North is
        // the -Z axis, which is exactly why Mat3.CreateOrientation negates its north column.
        // Asserting the reverse here would have written a falsehood into the suite as documentation.
        Vec3 cross = Vec3.Cross(WorldFrame.East, WorldFrame.Up);
        AssertMath.Close(WorldFrame.South.X, cross.X, Tolerance, "East x Up == South, X");
        AssertMath.Close(WorldFrame.South.Y, cross.Y, Tolerance, "East x Up == South, Y");
        AssertMath.Close(WorldFrame.South.Z, cross.Z, Tolerance, "East x Up == South, Z");

        AssertMath.Close(-WorldFrame.North.X, cross.X, Tolerance, "East x Up == -North, X");
        AssertMath.Close(-WorldFrame.North.Y, cross.Y, Tolerance, "East x Up == -North, Y");
        AssertMath.Close(-WorldFrame.North.Z, cross.Z, Tolerance, "East x Up == -North, Z");
    }

    [Fact]
    public void CreateOrientationNegatesTheNorthColumn_AndItsRowsAreTheSensorAxes()
    {
        // Mat3.CreateOrientation is the one place the layout is decided, so it is tested directly
        // and not only through the filter.
        Vec3 east = new(1, 0, 0);
        Vec3 up = new(0, 1, 0);
        Vec3 north = new(0, 0, -1);

        Mat3 m = Mat3.CreateOrientation(east, up, north);

        // Columns are (east, up, -north) in sensor coordinates. -north = (0,0,1).
        AssertMath.Close(1, m.M11, Tolerance, "column 1 = east, X");
        AssertMath.Close(0, m.M21, Tolerance, "column 1 = east, Y");
        AssertMath.Close(0, m.M31, Tolerance, "column 1 = east, Z");

        AssertMath.Close(0, m.M12, Tolerance, "column 2 = up, X");
        AssertMath.Close(1, m.M22, Tolerance, "column 2 = up, Y");
        AssertMath.Close(0, m.M32, Tolerance, "column 2 = up, Z");

        AssertMath.Close(0, m.M13, Tolerance, "column 3 = -north, X");
        AssertMath.Close(0, m.M23, Tolerance, "column 3 = -north, Y");
        AssertMath.Close(1, m.M33, Tolerance, "column 3 = -north, Z (the negation)");

        // A level board facing north is the identity orientation.
        AssertMath.Close(1, m.M11, Tolerance, "level board M11");
        AssertMath.Close(1, m.M22, Tolerance, "level board M22");
        AssertMath.Close(1, m.M33, Tolerance, "level board M33");
        AssertMath.Close(1, AssertMath.Determinant(m), Tolerance, "determinant");
    }

    private static Mat3 PoseMatrix(string poseName)
    {
        // Same poses and same count scales as OrientationFilterTests, kept local so this file does
        // not depend on another test class's private fixtures.
        Pose pose = poseName switch
        {
            "FlatFacingNorth" => new(new(1, 0, 0), new(0, 1, 0), new(0, 0, 1), 500),
            "FlatFacingEast" => new(new(0, 0, 1), new(0, 1, 0), new(-1, 0, 0), 500),
            "TiltedUp345" => new(new(1, 0, 0), new(0, 0.8, 0.6), new(0, -0.6, 0.8), 500),
            "InvertedFacingNorth" => new(new(-1, 0, 0), new(0, -1, 0), new(0, 0, 1), 500),
            "GeneralPose" => new(new(2.0 / 3, 1.0 / 3, 2.0 / 3), new(-2.0 / 3, 2.0 / 3, 1.0 / 3),
                                 new(-1.0 / 3, -2.0 / 3, 2.0 / 3), 1500),
            _ => throw new ArgumentOutOfRangeException(nameof(poseName), poseName, "Unknown pose."),
        };

        Vec3 upInSensor = new(pose.X.Y, pose.Y.Y, pose.Z.Y);
        Vec3 northInSensor = new(-pose.X.Z, -pose.Y.Z, -pose.Z.Z);

        int[] imu =
        [
            0, 0, 0,
            (int)Math.Round(upInSensor.X * pose.Scale), (int)Math.Round(upInSensor.Y * pose.Scale), (int)Math.Round(upInSensor.Z * pose.Scale),
            (int)Math.Round(northInSensor.X * pose.Scale), (int)Math.Round(northInSensor.Y * pose.Scale), (int)Math.Round(northInSensor.Z * pose.Scale),
        ];

        return new OrientationFilter().Update(imu, 0.1);
    }

    private sealed record Pose(Vec3 X, Vec3 Y, Vec3 Z, double Scale);
}
