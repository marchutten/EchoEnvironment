using EchoEnvironment.Core;
using Xunit;

namespace EchoEnvironment.Tests;

/// <summary>
/// Pins the math of <see cref="SensorMountOrientation"/>: identity is a true no-op, each single-axis
/// rotation produces the expected row-vector matrix, and the yaw*pitch*roll composition applies in the
/// documented order. These tests pin the MATH, not a physical ground truth - which way a positive yaw
/// physically turns the sensor depends on the mount and is intentionally left to configuration.
/// </summary>
public class SensorMountOrientationTests
{
    private const double Tol = 1e-9;

    [Fact]
    public void Identity_IsFlaggedAsIdentity()
    {
        Assert.True(SensorMountOrientation.Identity.IsIdentity);
        Assert.False(new SensorMountOrientation(0, 0, 1).IsIdentity);
    }

    [Fact]
    public void Identity_ToMatrix_IsTheIdentityMatrix()
    {
        Mat3 m = SensorMountOrientation.Identity.ToMatrix();

        Assert.Equal(1, m.M11, Tol);
        Assert.Equal(1, m.M22, Tol);
        Assert.Equal(1, m.M33, Tol);
        Assert.Equal(0, m.M12, Tol);
        Assert.Equal(0, m.M13, Tol);
        Assert.Equal(0, m.M21, Tol);
        Assert.Equal(0, m.M23, Tol);
        Assert.Equal(0, m.M31, Tol);
        Assert.Equal(0, m.M32, Tol);
    }

    [Fact]
    public void Yaw90_IsARotationAboutUp_AboutTheExpectedSign()
    {
        // Row-vector R_y(90): cos=0, sin=1 -> M13=-1, M31=+1, M22 stays 1.
        Mat3 m = new SensorMountOrientation(90, 0, 0).ToMatrix();

        Assert.Equal(0, m.M11, Tol);
        Assert.Equal(-1, m.M13, Tol);
        Assert.Equal(1, m.M22, Tol);
        Assert.Equal(1, m.M31, Tol);
        Assert.Equal(0, m.M33, Tol);
    }

    [Fact]
    public void Pitch90_IsARotationAboutEast()
    {
        // Row-vector R_x(90): M22=cos=0, M23=sin=1, M32=-sin=-1, M11 stays 1.
        Mat3 m = new SensorMountOrientation(0, 90, 0).ToMatrix();

        Assert.Equal(1, m.M11, Tol);
        Assert.Equal(0, m.M22, Tol);
        Assert.Equal(1, m.M23, Tol);
        Assert.Equal(-1, m.M32, Tol);
    }

    [Fact]
    public void Roll90_IsARotationAboutLookAxis()
    {
        // Row-vector R_z(90): M11=cos=0, M12=sin=1, M21=-sin=-1, M33 stays 1.
        Mat3 m = new SensorMountOrientation(0, 0, 90).ToMatrix();

        Assert.Equal(0, m.M11, Tol);
        Assert.Equal(1, m.M12, Tol);
        Assert.Equal(-1, m.M21, Tol);
        Assert.Equal(1, m.M33, Tol);
    }

    [Fact]
    public void Multiply_IsRowVectorComposition()
    {
        // (A * B) must satisfy v * (A*B) == (v*A)*B for the row-vector convention.
        Mat3 a = new SensorMountOrientation(37, 0, 0).ToMatrix();
        Mat3 b = new SensorMountOrientation(0, 53, 0).ToMatrix();
        Mat3 ab = a * b;

        double[] v = [0.3, -0.6, 0.9];

        double[] viaProduct = Apply(ab, v);
        double[] sequential = Apply(b, Apply(a, v));

        Assert.Equal(sequential[0], viaProduct[0], Tol);
        Assert.Equal(sequential[1], viaProduct[1], Tol);
        Assert.Equal(sequential[2], viaProduct[2], Tol);
    }

    [Fact]
    public void MountThenImu_ComposesInOrder()
    {
        // The whole feature rests on p * (M_mount * M_imu) == (p * M_mount) * M_imu.
        Mat3 mount = new SensorMountOrientation(20, -15, 8).ToMatrix();
        Mat3 imu = new SensorMountOrientation(-40, 12, 33).ToMatrix(); // any rotation stands in for the IMU matrix

        Mat3 composed = mount * imu;
        double[] p = [1.0, 2.0, -3.0];

        double[] viaComposed = Apply(composed, p);
        double[] mountThenImu = Apply(imu, Apply(mount, p));

        Assert.Equal(mountThenImu[0], viaComposed[0], Tol);
        Assert.Equal(mountThenImu[1], viaComposed[1], Tol);
        Assert.Equal(mountThenImu[2], viaComposed[2], Tol);
    }

    [Fact]
    public void IdentityMount_ComposesToTheImuMatrixUnchanged()
    {
        Mat3 imu = new SensorMountOrientation(-40, 12, 33).ToMatrix();
        Mat3 composed = SensorMountOrientation.Identity.ToMatrix() * imu;

        Assert.Equal(imu.M11, composed.M11, Tol);
        Assert.Equal(imu.M22, composed.M22, Tol);
        Assert.Equal(imu.M33, composed.M33, Tol);
        Assert.Equal(imu.M13, composed.M13, Tol);
        Assert.Equal(imu.M31, composed.M31, Tol);
    }

    // Applies a row-vector Mat3 to a point: result_j = sum_i v_i * M_ij.
    private static double[] Apply(in Mat3 m, double[] v) =>
    [
        v[0] * m.M11 + v[1] * m.M21 + v[2] * m.M31,
        v[0] * m.M12 + v[1] * m.M22 + v[2] * m.M32,
        v[0] * m.M13 + v[1] * m.M23 + v[2] * m.M33,
    ];
}
