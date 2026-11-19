namespace EchoEnvironment.Core;

/// <summary>
/// Core's own 3D vector.
///
/// Core compiles as plain net10.0 and must never reference PresentationCore, so it cannot use
/// System.Windows.Media.Media3D.Vector3D. This type mirrors exactly the members OrientationFilter
/// used before the extraction, which is what made the port mechanical rather than a rewrite.
/// Convert to and from WPF's Vector3D only through WpfMath, at the UI boundary.
///
/// DEVIATION FROM THE PLAN: MIGRATION_PLAN 2a declares `public void Normalize();` on a
/// `readonly struct`. That does not compile - in a readonly struct no member may assign to an
/// instance field. Normalizing therefore returns a new Vec3 instead of mutating in place. Every
/// call site that used `v.Normalize()` becomes `v = v.Normalized()`, which is also safer: the
/// WPF version silently mutated a struct that other code might still be holding.
/// </summary>
public readonly struct Vec3
{
    public Vec3(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public double X { get; }
    public double Y { get; }
    public double Z { get; }

    public double LengthSquared => X * X + Y * Y + Z * Z;

    public double Length => Math.Sqrt(LengthSquared);

    /// <summary>Right-handed cross product, identical in sign and component order to Vector3D.CrossProduct.</summary>
    public static Vec3 Cross(Vec3 a, Vec3 b) =>
        new(a.Y * b.Z - a.Z * b.Y,
            a.Z * b.X - a.X * b.Z,
            a.X * b.Y - a.Y * b.X);

    /// <summary>
    /// Scales to unit length. Callers always guard against a zero-length input first
    /// (OrientationFilter checks LengthSquared before every call), so the divide-by-zero case is
    /// unreachable here; it is deliberately not special-cased, so the arithmetic stays identical
    /// to the Vector3D version it replaces.
    /// </summary>
    public Vec3 Normalized() => this / Length;

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Vec3 operator *(Vec3 v, double scale) => new(v.X * scale, v.Y * scale, v.Z * scale);

    public static Vec3 operator /(Vec3 v, double scale) => new(v.X / scale, v.Y / scale, v.Z / scale);
}
