using EchoEnvironment.Core;
using Xunit;

namespace EchoEnvironment.Tests;

public class ImuAxisMapTests
{
    [Fact]
    public void LevelBoard_GravityOnChipZ_BecomesSensorUp()
    {
        int[] raw = [1, 2, 3, 0, 0, 4466, 10, 20, 30];
        int[] mapped = new int[9];

        ImuAxisMap.ToSensorFrame(raw, mapped);

        Assert.Equal([-2, 3, -1, 0, 4466, 0, -20, 30, -10], mapped);
    }

    [Fact]
    public void LevelBoard_AfterRemap_FilterReportsLevelPose()
    {
        // Chip lying flat: gravity on +Z, looking north along chip +X.
        int[] raw = [0, 0, 0, 0, 0, 500, 500, 0, 0];
        int[] mapped = new int[9];
        ImuAxisMap.ToSensorFrame(raw, mapped);

        Mat3 m = new OrientationFilter().Update(mapped, 0.1);

        AssertMath.Close(1, m.M22, 1e-9, "sensor Y must be world up for a level board");
    }

    [Fact]
    public void MeasuredTiltUpCapture_ReportsAPositivePitchOfAbout18Degrees()
    {
        // Live COM14 capture, front tilted up ~20 degrees.
        int[] raw = [0, 0, 0, 1332, -142, 4187, -66, 20, -397];
        int[] mapped = new int[9];
        ImuAxisMap.ToSensorFrame(raw, mapped);

        Mat3 m = new OrientationFilter().Update(mapped, 0.1);
        double pitch = Math.Asin(Math.Clamp(-m.M32, -1, 1)) * 180.0 / Math.PI;

        Assert.InRange(pitch, 15, 20);
    }
}
