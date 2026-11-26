namespace EchoEnvironment.Core;

/// <summary>
/// A fixed mounting orientation of the VL53L5CX DOF sensor, expressed as yaw / pitch / roll in
/// degrees. This is the static offset between how the DOF sensor is physically bolted down and the
/// reference frame the IMU-derived <see cref="OrientationFilter"/> already maps to world.
///
/// WHY A SEPARATE TYPE FROM THE IMU ORIENTATION. The IMU filter measures the live attitude of the
/// board; it cannot know how the DOF sensor's optical axis relates to that board if the two are
/// mounted at an angle to each other, or if the whole assembly is rotated on the vehicle. That
/// relationship is a fixed mechanical fact, not something to re-derive per frame, so it is a single
/// constant rotation configured once.
///
/// COMPOSITION (row-vector convention, v' = v * M). A point p in the DOF-sensor frame reaches the
/// world as <c>p * (M_mount * M_imu)</c>: the mount is applied first (DOF frame -> IMU reference
/// frame), then the IMU orientation (IMU reference frame -> world). The caller composes them; this
/// type only produces M_mount.
///
/// IDENTITY IS THE DEFAULT AND IT IS LOAD-BEARING. <see cref="Identity"/> reproduces today's
/// behavior exactly - a mount that is not configured changes nothing. Every non-identity value is a
/// deliberate opt-in.
///
/// ANGLE ORDER. <see cref="ToMatrix"/> composes as R_roll(X) * R_pitch(Y) * R_yaw(Z) in the row-vector
/// sense - a body-frame (intrinsic) XYZ sequence: roll about the sensor's own X first, then pitch about
/// its new Y, then yaw about its newest Z. Each angle is right-handed about its named body axis, matching
/// the standard positive-angle convention; the mapping is pinned by SensorMountOrientationTests.
/// </summary>
public readonly record struct SensorMountOrientation
{
    /// <summary>No mounting offset: the DOF sensor frame coincides with the IMU reference frame.</summary>
    public static readonly SensorMountOrientation Identity = new(0.0, 0.0, 0.0);

    public SensorMountOrientation(double yawDegrees, double pitchDegrees, double rollDegrees)
    {
        YawDegrees = yawDegrees;
        PitchDegrees = pitchDegrees;
        RollDegrees = rollDegrees;
    }

    /// <summary>Rotation about the body Z axis (up). Positive turns X toward Y by the right-hand rule.</summary>
    public double YawDegrees { get; }

    /// <summary>Rotation about the body Y axis. Positive turns Z toward X by the right-hand rule.</summary>
    public double PitchDegrees { get; }

    /// <summary>Rotation about the body X axis (forward). Positive turns Y toward Z by the right-hand rule.</summary>
    public double RollDegrees { get; }

    /// <summary>True when all three angles are zero, i.e. no offset at all.</summary>
    public bool IsIdentity => YawDegrees == 0.0 && PitchDegrees == 0.0 && RollDegrees == 0.0;

    /// <summary>
    /// Builds M_mount from yaw / pitch / roll. Returns <see cref="Mat3"/> identity for an identity
    /// mount, so a configured-but-zero mount composes to exactly the IMU matrix.
    /// </summary>
    public Mat3 ToMatrix()
    {
        Mat3 roll = RotationX(RollDegrees);
        Mat3 pitch = RotationY(PitchDegrees);
        Mat3 yaw = RotationZ(YawDegrees);

        // Body-frame (intrinsic) composition in the row-vector sense: roll about the sensor's own X
        // applied first, then pitch about its new Y, then yaw about its newest Z. Post-multiplying by
        // each elementary rotation applies it in the current body frame, so this is an intrinsic XYZ
        // sequence matching the standard positive-angle convention (roll=X, pitch=Y, yaw=Z).
        return roll * pitch * yaw;
    }

    // Elementary rotations in the row-vector convention (v' = v * R), i.e. the transpose of the usual
    // column-vector rotation matrices. Each is right-handed about its named world axis from WorldFrame.

    private static Mat3 RotationX(double degrees)
    {
        double c = System.Math.Cos(ToRadians(degrees));
        double s = System.Math.Sin(ToRadians(degrees));
        return new Mat3(
            1, 0, 0,
            0, c, s,
            0, -s, c);
    }

    private static Mat3 RotationY(double degrees)
    {
        double c = System.Math.Cos(ToRadians(degrees));
        double s = System.Math.Sin(ToRadians(degrees));
        return new Mat3(
            c, 0, -s,
            0, 1, 0,
            s, 0, c);
    }

    private static Mat3 RotationZ(double degrees)
    {
        double c = System.Math.Cos(ToRadians(degrees));
        double s = System.Math.Sin(ToRadians(degrees));
        return new Mat3(
            c, s, 0,
            -s, c, 0,
            0, 0, 1);
    }

    private static double ToRadians(double degrees) => degrees * System.Math.PI / 180.0;
}
