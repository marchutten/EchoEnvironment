using EchoEnvironment.Core;
using Xunit;

namespace EchoEnvironment.Tests;

/// <summary>
/// Pins the matrix-element -> reported-angle mapping used by MainWindow.SetOrientation, so that
/// defect D1 (the pitch/roll label swap) is fixed as a deliberate, auditable change in Phase 6a
/// instead of being discovered by eye.
///
/// MainWindow.SetOrientation today computes:
///     heading = atan2(-M31, M33)
///     pitch   = asin(clamp(-M32))
///     roll    = atan2(M12, M22)
/// and prints them as:
///     "Pitch: {roll}"   "Roll: {pitch}"
///
/// FINDING (and a correction to the migration plan's own description of D1): the three
/// expressions are each physically correct, and the variable NAMES are correct — only the two
/// labels are swapped.
///
///   asin(-M32)      = elevation of the look direction above horizontal.
///                     Column 3 of the matrix is world south in sensor coordinates, so M32 is the
///                     sensor-Z component of south... see the derivation below; the sign works out
///                     to look . up, which is sin(elevation). This is a genuine pitch.
///   atan2(M12, M22) = angle of world up projected into the sensor's own XY plane, measured from
///                     sensor Y toward sensor X. That is image roll: rotation of the sensor about
///                     its own optical (-Z) axis. This is a genuine roll.
///                     NOTE: the migration plan calls this "rotation about the up axis". That is
///                     wrong — see RollAboutOpticalAxis_DoesNotChangeElevation_AndIsNotRotationAboutUp.
///   atan2(-M31, M33) = compass heading of the look direction, 0 = north, 90 = east. Correct.
///
/// So the fix is a two-token change in the format string, and these tests are what make it safe.
///
/// PHASE 2 NOTE: these expressions now read Core's Mat3 directly, which is what they should always
/// have done - the angles are a property of the orientation, not of WPF's matrix type.
/// </summary>
public class OrientationAngleMappingTests
{
    private const double Tolerance = 1e-5;

    private sealed record Pose(Vec3 X, Vec3 Y, Vec3 Z)
    {
        public Vec3 UpInSensor => new(X.Y, Y.Y, Z.Y);
        public Vec3 NorthInSensor => new(-X.Z, -Y.Z, -Z.Z);
    }

    /// <summary>Level, looking north.</summary>
    private static readonly Pose LevelNorth = new(new(1, 0, 0), new(0, 1, 0), new(0, 0, 1));

    /// <summary>Level, looking east.</summary>
    private static readonly Pose LevelEast = new(new(0, 0, 1), new(0, 1, 0), new(-1, 0, 0));

    /// <summary>Level, looking south.</summary>
    private static readonly Pose LevelSouth = new(new(-1, 0, 0), new(0, 1, 0), new(0, 0, -1));

    /// <summary>Level and looking straight down (sensor -Z points at the ground).</summary>
    private static readonly Pose LookingDown = new(new(1, 0, 0), new(0, 0, -1), new(0, 1, 0));

    /// <summary>
    /// Level and looking north, but rolled 30 degrees about its own optical axis.
    /// Right-handed: X x Y = (cos30, sin30, 0) x (-sin30, cos30, 0) = (0, 0, 1) = Z.
    /// </summary>
    private static readonly Pose Rolled30 = new(
        new(Math.Cos(30 * Math.PI / 180), Math.Sin(30 * Math.PI / 180), 0),
        new(-Math.Sin(30 * Math.PI / 180), Math.Cos(30 * Math.PI / 180), 0),
        new(0, 0, 1));

    /// <summary>
    /// Pitched up 36.87 degrees (3-4-5 triangle), no roll. Look direction = -Z = (0, 0.6, -0.8):
    /// above horizontal and still pointing north.
    /// </summary>
    private static readonly Pose PitchedUp345 = new(
        new(1, 0, 0), new(0, 0.8, 0.6), new(0, -0.6, 0.8));

    private static Mat3 MatrixFor(Pose pose)
    {
        // Counts are scaled well above what a real BMA180/HMC5883L produces so that rounding a
        // non-integer component (only Rolled30 has one) contributes at most ~5e-8 relative error,
        // far below Tolerance. The filter's own arithmetic is exact.
        const int Scale = 10_000_000;

        int[] imu =
        [
            0, 0, 0,
            (int)Math.Round(pose.UpInSensor.X * Scale), (int)Math.Round(pose.UpInSensor.Y * Scale), (int)Math.Round(pose.UpInSensor.Z * Scale),
            (int)Math.Round(pose.NorthInSensor.X * Scale), (int)Math.Round(pose.NorthInSensor.Y * Scale), (int)Math.Round(pose.NorthInSensor.Z * Scale),
        ];

        return new OrientationFilter().Update(imu, 0.1);
    }

    // The three expressions, copied verbatim from MainWindow.SetOrientation.
    private static double Heading(in Mat3 m) => Math.Atan2(-m.M31, m.M33) * 180.0 / Math.PI;
    private static double PitchVariable(in Mat3 m) => Math.Asin(Math.Clamp(-m.M32, -1, 1)) * 180.0 / Math.PI;
    private static double RollVariable(in Mat3 m) => Math.Atan2(m.M12, m.M22) * 180.0 / Math.PI;

    /// <summary>
    /// The `pitch` variable really is the elevation of the look direction, for poses whose true
    /// elevation is known independently of the filter.
    /// </summary>
    [Theory]
    [InlineData(nameof(LookingDown), -90.0)]
    [InlineData(nameof(LevelSouth), 0.0)]
    [InlineData(nameof(LevelEast), 0.0)]
    [InlineData(nameof(LevelNorth), 0.0)]
    [InlineData(nameof(Rolled30), 0.0)]
    [InlineData(nameof(PitchedUp345), 36.8698976)]
    public void PitchVariable_IsTheLookDirectionElevation(string poseName, double expectedElevation)
    {
        Mat3 m = MatrixFor(PoseByName(poseName));

        AssertMath.Close(expectedElevation, PitchVariable(m), Tolerance, $"{poseName}: asin(-M32)");

        // Independent confirmation straight from the matrix: transform the sensor's look axis and
        // measure its world elevation. If the two disagree, the matrix layout is wrong, not the label.
        Vec3 look = AssertMath.Transform(m, WorldFrame.SensorLookAxis);
        double directElevation = Math.Asin(Math.Clamp(look.Y / look.Length, -1, 1)) * 180.0 / Math.PI;
        AssertMath.Close(directElevation, PitchVariable(m), Tolerance, $"{poseName}: asin(-M32) vs direct look elevation");
    }

    /// <summary>
    /// The `roll` variable really is rotation of the sensor about its own optical axis.
    /// Rolled30 is the discriminating pose: it has roll 30 and elevation 0, while PitchedUp345 has
    /// roll 0 and elevation 36.87. If the two expressions were measuring the same thing, these
    /// tests could not both pass.
    /// </summary>
    [Theory]
    [InlineData(nameof(LevelNorth), 0.0)]
    [InlineData(nameof(LevelEast), 0.0)]
    [InlineData(nameof(PitchedUp345), 0.0)]
    [InlineData(nameof(Rolled30), 30.0)]
    public void RollVariable_IsRotationAboutTheOpticalAxis(string poseName, double expectedRoll)
    {
        Mat3 m = MatrixFor(PoseByName(poseName));

        AssertMath.Close(expectedRoll, RollVariable(m), Tolerance, $"{poseName}: atan2(M12, M22)");
    }

    [Fact]
    public void RollAboutOpticalAxis_DoesNotChangeElevation_AndIsNotRotationAboutUp()
    {
        // Corrects the migration plan's description of atan2(M12, M22) as "rotation about the up
        // axis". A board rolled about its own optical axis while level is NOT rotated about the
        // world up axis, yet the roll expression reads 30 degrees. Conversely, a board whose
        // heading changes (a genuine rotation about the up axis) reads roll 0.
        Mat3 rolled = MatrixFor(Rolled30);
        Mat3 level = MatrixFor(LevelNorth);

        // Compared against the unrolled board, so what is being proved is that the ROLL reading
        // changed while elevation and heading did not.
        AssertMath.Close(0.0, RollVariable(level), Tolerance, "level board: roll");
        AssertMath.Close(0.0, PitchVariable(level), Tolerance, "level board: elevation");
        Assert.True(Math.Abs(RollVariable(rolled) - RollVariable(level)) > 29.0,
            $"rolling the board must change the roll reading; got {RollVariable(rolled):R} vs {RollVariable(level):R}.");

        AssertMath.Close(30.0, RollVariable(rolled), Tolerance, "rolled board: roll");
        AssertMath.Close(0.0, PitchVariable(rolled), Tolerance, "rolled board: elevation must stay 0");
        AssertMath.Close(0.0, Heading(rolled), Tolerance, "rolled board: heading must stay north");

        // Turning the board from north to east IS a rotation about the world up axis, and the roll
        // expression correctly reports 0 for it.
        Mat3 east = MatrixFor(LevelEast);
        AssertMath.Close(0.0, RollVariable(east), Tolerance, "board facing east: roll must be 0");
        AssertMath.Close(90.0, Heading(east), Tolerance, "board facing east: heading");
    }

    [Theory]
    [InlineData(nameof(LevelNorth), 0.0)]
    [InlineData(nameof(LevelEast), 90.0)]
    [InlineData(nameof(LevelSouth), 180.0)]
    public void HeadingVariable_IsCompassHeadingOfTheLookDirection(string poseName, double expectedHeading)
    {
        Mat3 m = MatrixFor(PoseByName(poseName));

        double heading = Heading(m);
        if (heading < 0)
        {
            heading += 360;
        }

        AssertMath.Close(expectedHeading, heading, Tolerance, $"{poseName}: heading");
    }

    /// <summary>
    /// CHARACTERIZATION OF DEFECT D1. Asserts the CURRENT (wrong) pairing of labels to values,
    /// exactly as MainWindow formats it, so that Phase 6a's fix is a visible red/green change
    /// instead of a silent edit. When the labels are corrected, this test must be updated to expect
    /// the corrected pairing — that update is the audit trail.
    ///
    /// Asserted numerically rather than by comparing formatted text: atan2 can return -0.0, which
    /// renders as "-0.0" and would make a string comparison fail for a reason unrelated to the
    /// defect. The pairing of label to VALUE is what matters.
    /// </summary>
    [Fact]
    public void DefectD1_CurrentFormatStringPrintsRollUnderThePitchLabel()
    {
        Mat3 rolled = MatrixFor(Rolled30);     // physically: roll 30, elevation 0
        Mat3 pitched = MatrixFor(PitchedUp345); // physically: roll 0, elevation 36.87

        // MainWindow's format string, reproduced verbatim:
        //   "Pitch: {roll}" then "Roll: {pitch}"
        // So the number printed under "Pitch" is RollVariable() and the number under "Roll" is
        // PitchVariable().
        AssertMath.Close(30.0, RollVariable(rolled), Tolerance, "value printed under the Pitch label for a rolled board");
        AssertMath.Close(0.0, PitchVariable(rolled), Tolerance, "value printed under the Roll label for a rolled board");

        AssertMath.Close(0.0, RollVariable(pitched), Tolerance, "value printed under the Pitch label for a pitched board");
        AssertMath.Close(Math.Asin(0.6) * 180.0 / Math.PI, PitchVariable(pitched), Tolerance, "value printed under the Roll label for a pitched board");

        // The correct pairing is provably different from the printed one, so Phase 6a cannot be a no-op:
        // the number that SHOULD appear under "Pitch" is the one currently printed under "Roll".
        Assert.True(Math.Abs(RollVariable(rolled) - PitchVariable(rolled)) > 1.0,
            "the two expressions must measure different quantities or the label swap would be harmless");
        Assert.True(Math.Abs(RollVariable(pitched) - PitchVariable(pitched)) > 1.0,
            "the two expressions must measure different quantities or the label swap would be harmless");
    }

    /// <summary>
    /// The corrected mapping, written out now so Phase 6a has a target to implement against.
    /// Currently unenforceable against MainWindow (it lives in the UI layer, which the tests cannot
    /// instantiate); this documents the intended end state.
    /// </summary>
    [Fact]
    public void CorrectedMapping_PitchIsElevationAndRollIsOpticalAxisRotation()
    {
        Mat3 rolled = MatrixFor(Rolled30);
        Mat3 pitched = MatrixFor(PitchedUp345);

        // After Phase 6a: Pitch == asin(-M32), Roll == atan2(M12, M22).
        AssertMath.Close(0.0, PitchVariable(rolled), Tolerance, "corrected Pitch for a rolled board");
        AssertMath.Close(30.0, RollVariable(rolled), Tolerance, "corrected Roll for a rolled board");

        AssertMath.Close(Math.Asin(0.6) * 180.0 / Math.PI, PitchVariable(pitched), Tolerance, "corrected Pitch for a pitched board");
        AssertMath.Close(0.0, RollVariable(pitched), Tolerance, "corrected Roll for a pitched board");
    }

    private static Pose PoseByName(string name) => name switch
    {
        "LevelNorth" => LevelNorth,
        "LevelEast" => LevelEast,
        "LevelSouth" => LevelSouth,
        "LookingDown" => LookingDown,
        "Rolled30" => Rolled30,
        "PitchedUp345" => PitchedUp345,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown pose."),
    };
}
