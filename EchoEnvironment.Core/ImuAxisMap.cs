namespace EchoEnvironment.Core;

/// <summary>
/// Remaps raw IMU counts from the IMU chip frame into the DOF-sensor frame the
/// <see cref="OrientationFilter"/> expects (X right, Y up, looking along -Z).
///
/// Measured on the board (COM14 pose capture): chip +Z = up (level board reads ~1 g on accel Z),
/// chip +X = look direction (tilting the front up raises accel X), chip +Y = left (rolling right
/// raises accel Y). The sensor frame is X right, Y up, looking along -Z.
/// Mapping (right-handed): sensorX = -imuY, sensorY = imuZ, sensorZ = -imuX.
/// The same mapping is applied to gyro, accel and mag so the three stay consistent.
/// </summary>
public static class ImuAxisMap
{
    public static void ToSensorFrame(ReadOnlySpan<int> imu, Span<int> sensor)
    {
        for (int i = 0; i < 9; i += 3)
        {
            int x = imu[i];
            int y = imu[i + 1];
            int z = imu[i + 2];
            sensor[i] = -y;
            sensor[i + 1] = z;
            sensor[i + 2] = -x;
        }
    }
}
