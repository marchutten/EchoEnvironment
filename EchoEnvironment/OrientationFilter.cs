using System.Windows.Media.Media3D;

namespace EchoEnvironment;

/// <summary>
/// Complementary filter for ITG3200 (gyro), BMA180 (accel) and HMC5883L (mag).
/// Tracks the world axes (east, up, north) as seen from the sensor frame. The gyro propagates them
/// between frames; accelerometer (gravity) and magnetometer (north) slowly pull them back.
/// </summary>
internal sealed class OrientationFilter
{
    private const double GyroLsbPerDegreePerSecond = 14.375;
    private const int CalibrationSamples = 50;
    private const double StationaryThresholdCounts = 60;
    private const double BiasAdaptationRate = 0.02;
    private const double CorrectionTimeConstantSeconds = 0.5;

    private Vector3D _up = new(0, 1, 0);
    private Vector3D _east = new(1, 0, 0);
    private Vector3D _north = new(0, 0, -1);
    private Vector3D _gyroBias;
    private Vector3D _gyroBiasSum;
    private int _calibrationCount;
    private bool _initialized;

    public Matrix3D Current => ToMatrix();

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
    public Matrix3D Update(int[] imu, double dt)
    {
        var gyro = new Vector3D(imu[0], imu[1], imu[2]);
        var accel = new Vector3D(imu[3], imu[4], imu[5]);
        var mag = new Vector3D(imu[6], imu[7], imu[8]);

        if (accel.LengthSquared < 1 || mag.LengthSquared < 1)
        {
            return ToMatrix();
        }

        Vector3D upMeasured = accel;
        upMeasured.Normalize();
        Vector3D eastMeasured = Vector3D.CrossProduct(mag, upMeasured);
        if (eastMeasured.LengthSquared < 1e-6)
        {
            return ToMatrix();
        }

        eastMeasured.Normalize();
        Vector3D northMeasured = Vector3D.CrossProduct(upMeasured, eastMeasured);

        if (!_initialized)
        {
            _up = upMeasured;
            _north = northMeasured;
            _east = eastMeasured;
            _initialized = true;
            return ToMatrix();
        }

        dt = Math.Clamp(dt, 0, 0.5);

        Vector3D omega = default;
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
            Vector3D delta = gyro - _gyroBias;
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
        _up -= Vector3D.CrossProduct(omega, _up) * dt;
        _north -= Vector3D.CrossProduct(omega, _north) * dt;

        double alpha = 1 - Math.Exp(-dt / CorrectionTimeConstantSeconds);
        _up = Normalized(_up * (1 - alpha) + upMeasured * alpha);
        _north = Normalized(_north * (1 - alpha) + northMeasured * alpha);

        _east = Normalized(Vector3D.CrossProduct(_north, _up));
        _north = Vector3D.CrossProduct(_up, _east);
        return ToMatrix();
    }

    private static Vector3D Normalized(Vector3D v)
    {
        v.Normalize();
        return v;
    }

    private Matrix3D ToMatrix()
    {
        // Row vectors: v' = v * M, so row j holds where sensor axis j ends up in (east, up, -north).
        return new Matrix3D(
            _east.X, _up.X, -_north.X, 0,
            _east.Y, _up.Y, -_north.Y, 0,
            _east.Z, _up.Z, -_north.Z, 0,
            0, 0, 0, 1);
    }
}
