using System.Windows.Media.Media3D;
using EchoEnvironment.Core;
using Xunit;

namespace EchoEnvironment.Tests;

/// <summary>
/// Numeric assertions with an explicit tolerance. Every geometry test in this suite compares
/// doubles; relying on xunit's exact-equality overload would make tests fail for reasons that
/// have nothing to do with the code under test.
///
/// Two matrix flavours on purpose:
///   - Mat3 overloads test Core directly, with no WPF in the path.
///   - Matrix3D overloads test the WpfMath adapter, which is the only place the two may meet.
/// Keeping them separate is what stops the adapter from being both the thing under test and the
/// lens used to look at it.
/// </summary>
internal static class AssertMath
{
    /// <summary>For values that should agree to floating-point precision (clamping, dt handling).</summary>
    public const double Exact = 1e-12;

    /// <summary>For values that pass through normalization and cross products.</summary>
    public const double Geometry = 1e-9;

    public static void Close(double expected, double actual, double tolerance, string what)
    {
        if (double.IsNaN(actual) || double.IsInfinity(actual))
        {
            Assert.Fail($"{what}: expected {expected:R}, got {actual} (not a finite number).");
        }

        if (Math.Abs(expected - actual) > tolerance)
        {
            Assert.Fail($"{what}: expected {expected:R}, got {actual:R} (tolerance {tolerance:R}, error {Math.Abs(expected - actual):R}).");
        }
    }

    public static void WithinRelative(double expected, double actual, double relativeTolerance, string what)
    {
        double tolerance = Math.Abs(expected) * relativeTolerance;
        Close(expected, actual, tolerance, what);
    }

    /// <summary>Compares the nine elements of Core's Mat3.</summary>
    public static void Matrix(in Mat3 expected, in Mat3 actual, double tolerance, string what)
    {
        Close(expected.M11, actual.M11, tolerance, $"{what}.M11");
        Close(expected.M12, actual.M12, tolerance, $"{what}.M12");
        Close(expected.M13, actual.M13, tolerance, $"{what}.M13");
        Close(expected.M21, actual.M21, tolerance, $"{what}.M21");
        Close(expected.M22, actual.M22, tolerance, $"{what}.M22");
        Close(expected.M23, actual.M23, tolerance, $"{what}.M23");
        Close(expected.M31, actual.M31, tolerance, $"{what}.M31");
        Close(expected.M32, actual.M32, tolerance, $"{what}.M32");
        Close(expected.M33, actual.M33, tolerance, $"{what}.M33");
    }

    /// <summary>
    /// Compares Core's Mat3 against the Matrix3D the adapter produced from it. Used to prove
    /// WpfMath.ToMatrix3D copies the rotation unchanged rather than transposing it.
    /// </summary>
    public static void Matrix(in Mat3 expected, Matrix3D actual, double tolerance, string what)
    {
        Close(expected.M11, actual.M11, tolerance, $"{what}.M11");
        Close(expected.M12, actual.M12, tolerance, $"{what}.M12");
        Close(expected.M13, actual.M13, tolerance, $"{what}.M13");
        Close(expected.M21, actual.M21, tolerance, $"{what}.M21");
        Close(expected.M22, actual.M22, tolerance, $"{what}.M22");
        Close(expected.M23, actual.M23, tolerance, $"{what}.M23");
        Close(expected.M31, actual.M31, tolerance, $"{what}.M31");
        Close(expected.M32, actual.M32, tolerance, $"{what}.M32");
        Close(expected.M33, actual.M33, tolerance, $"{what}.M33");
    }

    /// <summary>
    /// Compares all sixteen elements of a Matrix3D.
    /// Note the naming trap that cost a compile error here once: WPF's Matrix3D spells the fourth
    /// row's translation elements OffsetX/OffsetY/OffsetZ. M41/M42/M43 do not exist.
    /// </summary>
    public static void Matrix(Matrix3D expected, Matrix3D actual, double tolerance, string what)
    {
        Close(expected.M11, actual.M11, tolerance, $"{what}.M11");
        Close(expected.M12, actual.M12, tolerance, $"{what}.M12");
        Close(expected.M13, actual.M13, tolerance, $"{what}.M13");
        Close(expected.M14, actual.M14, tolerance, $"{what}.M14");
        Close(expected.M21, actual.M21, tolerance, $"{what}.M21");
        Close(expected.M22, actual.M22, tolerance, $"{what}.M22");
        Close(expected.M23, actual.M23, tolerance, $"{what}.M23");
        Close(expected.M24, actual.M24, tolerance, $"{what}.M24");
        Close(expected.M31, actual.M31, tolerance, $"{what}.M31");
        Close(expected.M32, actual.M32, tolerance, $"{what}.M32");
        Close(expected.M33, actual.M33, tolerance, $"{what}.M33");
        Close(expected.M34, actual.M34, tolerance, $"{what}.M34");
        Close(expected.OffsetX, actual.OffsetX, tolerance, $"{what}.OffsetX");
        Close(expected.OffsetY, actual.OffsetY, tolerance, $"{what}.OffsetY");
        Close(expected.OffsetZ, actual.OffsetZ, tolerance, $"{what}.OffsetZ");
        Close(expected.M44, actual.M44, tolerance, $"{what}.M44");
    }

    /// <summary>
    /// Applies the matrix to a vector using the row-vector convention the whole codebase uses:
    /// v' = v * M, so x' = M11*x + M21*y + M31*z.
    ///
    /// This lives here rather than on Mat3 on purpose. Production never transforms a vector with
    /// this matrix - it hands the matrix to WPF and lets WPF do it. Adding Transform() to Core
    /// would be API nobody calls; having it here is what lets the tests assert behaviour instead of
    /// only numbers.
    /// </summary>
    public static Vec3 Transform(in Mat3 m, in Vec3 v) =>
        new(v.X * m.M11 + v.Y * m.M21 + v.Z * m.M31,
            v.X * m.M12 + v.Y * m.M22 + v.Z * m.M32,
            v.X * m.M13 + v.Y * m.M23 + v.Z * m.M33);

    /// <summary>Same, through WPF, so the two implementations can be compared against each other.</summary>
    public static Vec3 Transform(Matrix3D m, in Vec3 v)
    {
        Vector3D r = m.Transform(v.ToVector3D());
        return new Vec3(r.X, r.Y, r.Z);
    }

    /// <summary>
    /// Determinant of Core's Mat3. Computed here rather than on Mat3 because nothing in production
    /// needs it; it exists purely so the tests can tell a rotation from a reflection.
    /// </summary>
    public static double Determinant(in Mat3 m) =>
        m.M11 * (m.M22 * m.M33 - m.M23 * m.M32)
      - m.M12 * (m.M21 * m.M33 - m.M23 * m.M31)
      + m.M13 * (m.M21 * m.M32 - m.M22 * m.M31);

    /// <summary>
    /// The rows of the orientation matrix are the world coordinates of the three sensor axes.
    /// They must stay unit-length and mutually perpendicular for the scene to be a rigid rotation.
    /// This is the drift detector called for in the plan's property-test item.
    /// </summary>
    public static void OrthonormalRows(in Mat3 m, string context)
    {
        if (double.IsNaN(m.M11) || double.IsNaN(m.M22) || double.IsNaN(m.M33))
        {
            Assert.Fail($"{context}: matrix contains NaN.");
        }

        var row1 = new Vec3(m.M11, m.M12, m.M13);
        var row2 = new Vec3(m.M21, m.M22, m.M23);
        var row3 = new Vec3(m.M31, m.M32, m.M33);

        Close(1, row1.Length, Geometry, $"{context}: |row1|");
        Close(1, row2.Length, Geometry, $"{context}: |row2|");
        Close(1, row3.Length, Geometry, $"{context}: |row3|");

        Close(0, Dot(row1, row2), Geometry, $"{context}: row1 . row2");
        Close(0, Dot(row1, row3), Geometry, $"{context}: row1 . row3");
        Close(0, Dot(row2, row3), Geometry, $"{context}: row2 . row3");

        Close(1, Math.Abs(Determinant(m)), Geometry, $"{context}: |determinant|");
    }

    /// <summary>Dot product, local to the tests: production never uses one, so Core does not carry one.</summary>
    public static double Dot(in Vec3 a, in Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
}
