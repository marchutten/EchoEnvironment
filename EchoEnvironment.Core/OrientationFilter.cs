namespace EchoEnvironment.Core;

/// <summary>
/// Complementary filter for ITG3200 (gyro), BMA180 (accel) and HMC5883L (mag).
/// Tracks the world axes (east, up, north) as seen from the sensor frame. The gyro propagates them
/// between frames; accelerometer (gravity) and magnetometer (north) slowly pull them back.
///
/// PHASE 2 NOTE: this is the same class that used to live in the WPF App project using
/// Vector3D/Matrix3D. It now uses Core's own Vec3/Mat3 so that it compiles in an assembly with no
/// WPF reference at all. The arithmetic is unchanged, line for line. Convert the result to a
/// Matrix3D only at the UI boundary, via WpfMath.ToMatrix3D.
/// </summary>
public sealed class OrientationFilter
{
    private const double GyroLsbPerDegreePerSecond = 14.375;
    private const int CalibrationSamples = 50;
    private const double StationaryThresholdCounts = 60;
    private const double BiasAdaptationRate = 0.02;
    private const double CorrectionTimeConstantSeconds = 0.5;

    private Vec3 _up = WorldFrame.Up;
    private Vec3 _east = WorldFrame.East;
    private Vec3 _north = WorldFrame.North;
    private Vec3 _gyroBias;
    private Vec3 _gyroBiasSum;
    private int _calibrationCount;
    private bool _initialized;

    public Mat3 Current => Mat3.CreateOrientation(_east, _up, _north);

    /// <summary>
    /// Re-arms the filter for a new connection.
    ///
    /// FINDING pinned by Reset_DoesNotClearTheOrientationAxes_StalePoseIsStillReported: this does
    /// NOT clear _up/_east/_north, so Current keeps reporting the pose measured before the Reset
    /// until a valid frame arrives. MainWindow calls this on every reconnect. Preserved verbatim
    /// from the pre-extraction behaviour; changing it is a behaviour change, not a refactor.
    /// </summary>
    public void Reset()
    {
        _initialized = false;
        _calibrationCount = 0;
        _gyroBiasSum = default;
        _gyroBias = default;
    }

    /// <summary>
    /// imu: gyro x y z, accel x y z, mag x y z (raw counts). dt in seconds.
    /// Returns a matrix that rotates sensor-frame points into the world frame (X east, Y up, -Z north).
    /// </summary>
    public Mat3 Update(int[] imu, double dt)
    {
        var gyro = new Vec3(imu[0], imu[1], imu[2]);
        var accel = new Vec3(imu[3], imu[4], imu[5]);
        var mag = new Vec3(imu[6], imu[7], imu[8]);

        if (accel.LengthSquared < 1 || mag.LengthSquared < 1)
        {
            return Current;
        }

        Vec3 upMeasured = accel.Normalized();
        Vec3 eastMeasured = Vec3.Cross(mag, upMeasured);
        if (eastMeasured.LengthSquared < 1e-6)
        {
            return Current;
        }

        eastMeasured = eastMeasured.Normalized();
        Vec3 northMeasured = Vec3.Cross(upMeasured, eastMeasured);

        if (!_initialized)
        {
            _up = upMeasured;
            _north = northMeasured;
            _east = eastMeasured;
            _initialized = true;
            return Current;
        }

        dt = Math.Clamp(dt, 0, 0.5);

        Vec3 omega = default;
        if (_calibrationCount < CalibrationSamples)
        {
            _gyroBiasSum += gyro;
            _calibrationCount++;
            if (_calibrationCount == CalibrationSamples)
            {
                _gyroBias = _gyroBiasSum / CalibrationSamples;
            }
        }
        else
        {
            double toRadians = Math.PI / 180.0 / GyroLsbPerDegreePerSecond;
            Vec3 delta = gyro - _gyroBias;
            if (delta.Length < StationaryThresholdCounts)
            {
                // Board is at rest: track the bias slowly and ignore noise.
                _gyroBias += delta * BiasAdaptationRate;
            }
            else
            {
                omega = delta * toRadians;
            }
        }

        // A fixed world vector seen from a body rotating with omega changes as dw/dt = -omega x w.
        _up -= Vec3.Cross(omega, _up) * dt;
        _north -= Vec3.Cross(omega, _north) * dt;

        double alpha = 1 - Math.Exp(-dt / CorrectionTimeConstantSeconds);
        _up = (_up * (1 - alpha) + upMeasured * alpha).Normalized();
        _north = (_north * (1 - alpha) + northMeasured * alpha).Normalized();

        _east = Vec3.Cross(_north, _up).Normalized();
        _north = Vec3.Cross(_up, _east);
        return Current;
    }
}
