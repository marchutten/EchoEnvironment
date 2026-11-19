using System.Windows.Media.Media3D;
using EchoEnvironment.Core;

namespace EchoEnvironment;

/// <summary>
/// The ONLY place in the codebase allowed to convert between Core's math and WPF's math.
///
/// EchoEnvironment.Core compiles as plain net10.0 and must never reference PresentationCore.
/// The UI speaks Matrix3D/Vector3D. This adapter is the single boundary where the two meet, so
/// that "the domain logic has no WPF dependency" is a structural fact rather than a convention
/// someone has to remember.
///
/// Lives in the App project, not in Core, because Core is the side that must stay clean.
/// (MIGRATION_PLAN 2c places it in EchoEnvironment.Rendering/Math/WpfMath.cs; that project does
/// not exist until Phase 5, so it is here for now and moves with it.)
///
/// Deliberately NOT in a namespace named EchoEnvironment.Math: that would shadow System.Math in
/// every file importing it, and MainWindow uses Math.Abs extensively.
/// </summary>
public static class WpfMath
{
    /// <summary>
    /// Reproduces OrientationFilter's pre-extraction ToMatrix() element-for-element: the 3x3
    /// rotation goes into the top-left block and the fourth row is the identity's (0, 0, 0, 1).
    /// Note that WPF spells the fourth row's translation elements OffsetX/OffsetY/OffsetZ, not
    /// M41/M42/M43 - those three names do not exist on Matrix3D.
    /// </summary>
    public static Matrix3D ToMatrix3D(this in Mat3 m) =>
        new(m.M11, m.M12, m.M13, 0,
            m.M21, m.M22, m.M23, 0,
            m.M31, m.M32, m.M33, 0,
            0, 0, 0, 1);

    public static Vector3D ToVector3D(this in Vec3 v) => new(v.X, v.Y, v.Z);

    public static Vec3 ToVec3(this Vector3D v) => new(v.X, v.Y, v.Z);
}
