using EchoEnvironment.Core;
using Xunit;

namespace EchoEnvironment.Tests;

public class ImuAxisMapTests
{
    [Fact]
    public void LevelBoard_GravityOnChipZ_BecomesSensorLookAxis()
    {
        int[] raw = [1, 2, 3, 0, 0, 4466, 10, 20, 30];
        int[] mapped = new int[9];

        ImuAxisMap.ToSensorFrame(raw, mapped);

        Assert.Equal([-2, 1, 3, 0, 0, 4466, -20, 10, 30], mapped);
    }

    [Fact]
    public void LevelBoard_AfterRemap_SensorLooksStraightUp()
    {
        // Chip lying flat: gravity on +Z, looking north along chip +X.
        int[] raw = [0, 0, 0, 0, 0, 500, 500, 0, 0];
        int[] mapped = new int[9];
        ImuAxisMap.ToSensorFrame(raw, mapped);

        Mat3 m = new OrientationFilter().Update(mapped, 0.1);

        AssertMath.Close(1, m.M32, 1e-9, "flat board: sensor Z row points along world up (verified on hardware)");
    }

    [Fact]
    public void MeasuredTiltUpCapture_LooksAbout18DegreesOffVertical()
    {
        // Live COM14 capture, front tilted up ~20 degrees.
        int[] raw = [0, 0, 0, 1332, -142, 4187, -66, 20, -397];
        int[] mapped = new int[9];
        ImuAxisMap.ToSensorFrame(raw, mapped);

        Mat3 m = new OrientationFilter().Update(mapped, 0.1);
        double pitch = Math.Asin(Math.Clamp(-m.M32, -1, 1)) * 180.0 / Math.PI;

        Assert.InRange(pitch, -75, -70);
    }
}
