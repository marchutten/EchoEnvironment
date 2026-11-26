namespace EchoEnvironment.Core;

/// <summary>
/// Core's own 3x3 rotation matrix.
///
/// Row-major, row-vector convention: v' = v * M. Row j holds where sensor axis j ends up in the
/// world. This is the layout OrientationFilter has always produced, and it is what
/// WpfMath.ToMatrix3D reproduces element-for-element in a Matrix3D.
///
/// Only the 3x3 rotation part is stored. An orientation has no translation and no perspective
/// row, so the fourth row (Matrix3D's OffsetX/OffsetY/OffsetZ and M44) is not modelled here at
/// all - it is supplied by the adapter. That removes the possibility of Core ever carrying a
/// translation it was never meant to have.
/// </summary>
public readonly struct Mat3
{
    public Mat3(
        double m11, double m12, double m13,
        double m21, double m22, double m23,
        double m31, double m32, double m33)
    {
        M11 = m11;
        M12 = m12;
        M13 = m13;
        M21 = m21;
        M22 = m22;
        M23 = m23;
        M31 = m31;
        M32 = m32;
        M33 = m33;
    }

    public double M11 { get; }
    public double M12 { get; }
    public double M13 { get; }

    public double M21 { get; }
    public double M22 { get; }
    public double M23 { get; }

    public double M31 { get; }
    public double M32 { get; }
    public double M33 { get; }

    /// <summary>The rotation that changes nothing: composing any matrix with this returns the other one.</summary>
    public static readonly Mat3 Identity = new(1, 0, 0, 0, 1, 0, 0, 0, 1);

    /// <summary>
    /// Builds the sensor-to-world rotation whose columns are the three world axes expressed in
    /// sensor coordinates - which makes its rows the sensor axes expressed in world coordinates.
    ///
    /// The north column is NEGATED, and that is not a mistake: the world frame is
    /// X = east, Y = up, -Z = north (see WorldFrame), so the third column must be -north to land
    /// on +Z. Drop the minus sign here and the whole 3D view mirrors, silently.
    /// WpfMathTests.CreateOrientation_IsNotTransposed_AndNegatesNorth pins this.
    /// </summary>
    public static Mat3 CreateOrientation(Vec3 east, Vec3 up, Vec3 north) =>
        new(east.X, up.X, -north.X,
            east.Y, up.Y, -north.Y,
            east.Z, up.Z, -north.Z);

    /// <summary>
    /// Row-vector composition: (a * b) is the matrix such that v * (a * b) == (v * a) * b. This is
    /// the order SensorMountOrientation relies on to compose a fixed mount with the live IMU matrix
    /// as p * (M_mount * M_imu). Result element (i,j) is the dot of row i of a with column j of b.
    /// </summary>
    public static Mat3 operator *(in Mat3 a, in Mat3 b) =>
        new(
            a.M11 * b.M11 + a.M12 * b.M21 + a.M13 * b.M31,
            a.M11 * b.M12 + a.M12 * b.M22 + a.M13 * b.M32,
            a.M11 * b.M13 + a.M12 * b.M23 + a.M13 * b.M33,

            a.M21 * b.M11 + a.M22 * b.M21 + a.M23 * b.M31,
            a.M21 * b.M12 + a.M22 * b.M22 + a.M23 * b.M32,
            a.M21 * b.M13 + a.M22 * b.M23 + a.M23 * b.M33,

            a.M31 * b.M11 + a.M32 * b.M21 + a.M33 * b.M31,
            a.M31 * b.M12 + a.M32 * b.M22 + a.M33 * b.M32,
            a.M31 * b.M13 + a.M32 * b.M23 + a.M33 * b.M33);
}
