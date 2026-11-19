using System.Windows.Media.Media3D;
using Xunit;

namespace EchoEnvironment.Tests;

/// <summary>
/// Numeric assertions with an explicit tolerance. Every geometry test in this suite compares
/// doubles; relying on xunit's exact-equality overload would make tests fail for reasons that
/// have nothing to do with the code under test.
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

    /// <summary>
    /// Compares all sixteen elements of a Matrix3D. Used to pin OrientationFilter.ToMatrix()'s
    /// exact layout, which is the highest-risk item in the Phase 2 Mat3 port.
    /// </summary>
    public static void Matrix(Matrix3D actual, Matrix3D expected, double tolerance = Geometry)
    {
        Close(expected.M11, actual.M11, tolerance, "M11");
        Close(expected.M12, actual.M12, tolerance, "M12");
        Close(expected.M13, actual.M13, tolerance, "M13");
        Close(expected.M14, actual.M14, tolerance, "M14");
        Close(expected.M21, actual.M21, tolerance, "M21");
        Close(expected.M22, actual.M22, tolerance, "M22");
        Close(expected.M23, actual.M23, tolerance, "M23");
        Close(expected.M24, actual.M24, tolerance, "M24");
        Close(expected.M31, actual.M31, tolerance, "M31");
        Close(expected.M32, actual.M32, tolerance, "M32");
        Close(expected.M33, actual.M33, tolerance, "M33");
        Close(expected.M34, actual.M34, tolerance, "M34");
        Close(expected.M41, actual.M41, tolerance, "M41");
        Close(expected.M42, actual.M42, tolerance, "M42");
        Close(expected.M43, actual.M43, tolerance, "M43");
        Close(expected.M44, actual.M44, tolerance, "M44");
    }

    /// <summary>
    /// The rows of OrientationFilter's matrix are the world coordinates of the three sensor axes.
    /// They must stay unit-length and mutually perpendicular for the scene to be a rigid rotation.
    /// This is the drift detector called for in the plan's property-test item.
    /// </summary>
    public static void OrthonormalRows(Matrix3D m, string context)
    {
        if (double.IsNaN(m.M11) || double.IsNaN(m.M22) || double.IsNaN(m.M33))
        {
            Assert.Fail($"{context}: matrix contains NaN.");
        }

        var row1 = new Vector3D(m.M11, m.M12, m.M13);
        var row2 = new Vector3D(m.M21, m.M22, m.M23);
        var row3 = new Vector3D(m.M31, m.M32, m.M33);

        Close(1, row1.Length, Geometry, $"{context}: |row1|");
        Close(1, row2.Length, Geometry, $"{context}: |row2|");
        Close(1, row3.Length, Geometry, $"{context}: |row3|");

        Close(0, Vector3D.DotProduct(row1, row2), Geometry, $"{context}: row1 . row2");
        Close(0, Vector3D.DotProduct(row1, row3), Geometry, $"{context}: row1 . row3");
        Close(0, Vector3D.DotProduct(row2, row3), Geometry, $"{context}: row2 . row3");

        Close(1, Math.Abs(m.Determinant), Geometry, $"{context}: |determinant|");
    }
}
