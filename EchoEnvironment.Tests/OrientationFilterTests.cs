using EchoEnvironment.Core;
using Xunit;

namespace EchoEnvironment.Tests;

/// <summary>
/// Characterization + correctness tests for <see cref="OrientationFilter"/>.
///
/// HOW THE EXPECTED VALUES ARE DERIVED (this is deliberately NOT a restatement of the production
/// formula, which would make the tests circular):
///
///   A pose is declared as the three SENSOR axes expressed in WORLD coordinates:
///       X = where the sensor's X axis points in the world
///       Y = where the sensor's Y axis points in the world
///       Z = where the sensor's Z axis points in the world   (the sensor looks along -Z)
///
///   World frame (WorldFrame in Core): X = east, Y = up, -Z = north.
///   So east = (1,0,0), up = (0,1,0), north = (0,0,-1).
///
///   The filter's inputs are physical measurements, so they are derived from the pose. A world
///   vector v appears in sensor coordinates with component v_j = (sensor axis j) . v:
///       accel = up in sensor coords    = (X.Y, Y.Y, Z.Y)
///       mag   = north in sensor coords = (-X.Z, -Y.Z, -Z.Z)
///   (The filter takes east from Cross(mag, up), which is consistent because north x up = east.)
///
///   The matrix the filter must return: the codebase uses row vectors throughout, v' = v * M, so
///   x' = M11*x + M21*y + M31*z. Therefore row j of M must be sensor axis j in world coordinates:
///       M = | X.X X.Y X.Z |
///           | Y.X Y.Y Y.Z |
///           | Z.X Z.Y Z.Z |
///   which is exactly what Mat3.CreateOrientation builds from (_east, _up, -_north): its COLUMNS
///   are (_east, _up, -_north) expressed in sensor coordinates, so its ROWS are the sensor axes in
///   world coordinates. Getting this relationship wrong is the highest-risk item in the Mat3 port,
///   and it is pinned element-by-element in MatrixLayout_... below.
///
/// Each pose carries a CountScale chosen so its accel/mag counts are EXACT integers, not rounded
/// approximations. Without that, a pose is recovered only to count-rounding error - about 2e-4 for
/// a 500-count vector holding thirds - and a 1e-9 tolerance asserts nothing. The one exception
/// (30 degrees, whose cos/sin are irrational) scales its counts up instead and states its looser
/// tolerance explicitly.
/// </summary>
public class OrientationFilterTests
{
    private const double IdealAccelCounts = 500.0; // 1 g
    private const double PoseTolerance = 1e-9;

    /// <summary>
    /// GeneralPose's components are thirds. At 500 counts, 1/3 of that is 166.666, which rounds to
    /// 167 - a 0.2% error, so the pose could only be recovered to ~2e-4 and a 1e-9 tolerance would
    /// be meaningless. A scale divisible by 3 makes the counts exact: 1500 gives accel
    /// (500, 1000, 500) and mag (-1000, -500, -1000).
    /// </summary>
    private const double GeneralPoseCountScale = 1500.0;

    /// <summary>
    /// Declared here rather than as Mat3.Identity in Core: nothing in production needs an identity
    /// matrix, and Core should not carry API only the tests call.
    /// </summary>
    private static readonly Mat3 Identity = new(1, 0, 0, 0, 1, 0, 0, 0, 1);

    /// <summary>
    /// The accel/mag magnitude at which this pose's sensor-frame counts are exact integers, so the
    /// pose is recovered to PoseTolerance rather than to count-rounding error.
    /// </summary>
    private sealed record Pose(Vec3 X, Vec3 Y, Vec3 Z, double CountScale = IdealAccelCounts)
    {
        /// <summary>World up, expressed in sensor coordinates — what the accelerometer reads.</summary>
        public Vec3 UpInSensor => new(X.Y, Y.Y, Z.Y);

        /// <summary>World north, expressed in sensor coordinates — what the magnetometer reads.</summary>
        public Vec3 NorthInSensor => new(-X.Z, -Y.Z, -Z.Z);

        /// <summary>The matrix the filter must return for this pose (see class header).</summary>
        public Mat3 ExpectedMatrix =>
            new(X.X, X.Y, X.Z,
                Y.X, Y.Y, Y.Z,
                Z.X, Z.Y, Z.Z);
    }

    // -----------------------------------------------------------------------------------------
    // Poses. Each is orthonormal and right-handed (X x Y = Z), verified by hand.
    // -----------------------------------------------------------------------------------------

    /// <summary>Board flat and level, sensor X east, sensor Y up, looking north.</summary>
    private static readonly Pose FlatFacingNorth = new(
        new(1, 0, 0), new(0, 1, 0), new(0, 0, 1));

    /// <summary>Board flat and level, looking east. Sensor X then points south.</summary>
    private static readonly Pose FlatFacingEast = new(
        new(0, 0, 1), new(0, 1, 0), new(-1, 0, 0));

    /// <summary>
    /// Tilted up 36.87 degrees (3-4-5 triangle, so every count is an exact integer).
    /// Look direction = -Z = (0, 0.6, -0.8): 36.87 degrees above horizontal, still pointing north
    /// (a negative world-Z component is north in this world frame).
    /// </summary>
    private static readonly Pose TiltedUp345 = new(
        new(1, 0, 0), new(0, 0.8, 0.6), new(0, -0.6, 0.8));

    /// <summary>Tilted up 30 degrees. Counts must be rounded, so this pose uses a looser tolerance.</summary>
    private static readonly Pose TiltedUp30 = new(
        new(1, 0, 0), new(0, Math.Cos(30 * Math.PI / 180), Math.Sin(30 * Math.PI / 180)),
        new(0, -Math.Sin(30 * Math.PI / 180), Math.Cos(30 * Math.PI / 180)));

    /// <summary>Board upside down, still looking north. Sensor Y points at the ground.</summary>
    private static readonly Pose InvertedFacingNorth = new(
        new(-1, 0, 0), new(0, -1, 0), new(0, 0, 1));

    /// <summary>
    /// A fully general pose with no zero or repeated elements — the layout-pinning pose.
    /// Exact orthonormal right-handed triple: X=(2/3,1/3,2/3), Y=(-2/3,2/3,1/3), Z=(-1/3,-2/3,2/3).
    /// Every component is a third, so it needs a count scale divisible by 3; see
    /// GeneralPoseCountScale.
    /// </summary>
    private static readonly Pose GeneralPose = new(
        new(2.0 / 3, 1.0 / 3, 2.0 / 3), new(-2.0 / 3, 2.0 / 3, 1.0 / 3), new(-1.0 / 3, -2.0 / 3, 2.0 / 3),
        GeneralPoseCountScale);

    private static Vec3 Counts(double x, double y, double z) => new(x, y, z);

    private static int[] Frame(Vec3 gyro, Vec3 accel, Vec3 mag) =>
        [(int)Math.Round(gyro.X), (int)Math.Round(gyro.Y), (int)Math.Round(gyro.Z),
         (int)Math.Round(accel.X), (int)Math.Round(accel.Y), (int)Math.Round(accel.Z),
         (int)Math.Round(mag.X), (int)Math.Round(mag.Y), (int)Math.Round(mag.Z)];

    /// <summary>
    /// The raw counts a device would report for this pose. Scales default to the pose's own
    /// CountScale, which is chosen so those counts are exact integers - see the class header.
    /// </summary>
    private static int[] PoseFrame(Pose pose, Vec3 gyro,
        double? accelScale = null, double? magScale = null) =>
        Frame(gyro, pose.UpInSensor * (accelScale ?? pose.CountScale),
                   pose.NorthInSensor * (magScale ?? pose.CountScale));

    /// <summary>
    /// A filter that has completed its calibration window: call 1 initializes (and does NOT
    /// accumulate), calls 2..51 accumulate 50 samples. Bias ends up equal to biasGyro.
    /// </summary>
    private static OrientationFilter Calibrated(Pose pose, Vec3 biasGyro)
    {
        var filter = new OrientationFilter();
        filter.Update(PoseFrame(pose, biasGyro), 0.1);
        for (int call = 2; call <= 51; call++)
        {
            filter.Update(PoseFrame(pose, biasGyro), 0.1);
        }

        return filter;
    }

    private static OrientationFilter Calibrated(Pose pose) => Calibrated(pose, Counts(0, 0, 0));

    private static Pose PoseByName(string name) => name switch
    {
        "FlatFacingNorth" => FlatFacingNorth,
        "FlatFacingEast" => FlatFacingEast,
        "TiltedUp345" => TiltedUp345,
        "InvertedFacingNorth" => InvertedFacingNorth,
        "GeneralPose" => GeneralPose,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown pose."),
    };

    // -----------------------------------------------------------------------------------------
    // Initial state and the initialization path.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void BeforeAnyUpdate_CurrentIsIdentity()
    {
        var filter = new OrientationFilter();

        AssertMath.Matrix(Identity, filter.Current, AssertMath.Exact, "initial Current");
    }

    /// <summary>
    /// The first Update seeds the axes straight from the measurements and returns immediately:
    /// no gyro propagation and no accel/mag blending is applied to the initializing frame, and no
    /// calibration sample is consumed.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.001)]
    [InlineData(0.5)]
    [InlineData(1000.0)]
    public void FirstUpdate_ReturnsTheMeasuredPose_NoCorrectionApplied(double dt)
    {
        var filter = new OrientationFilter();

        Mat3 m = filter.Update(PoseFrame(FlatFacingEast, Counts(0, 0, 0)), dt);

        AssertMath.Matrix(FlatFacingEast.ExpectedMatrix, m, PoseTolerance, "first Update");
    }

    [Theory]
    [InlineData(nameof(FlatFacingNorth))]
    [InlineData(nameof(FlatFacingEast))]
    [InlineData(nameof(TiltedUp345))]
    [InlineData(nameof(InvertedFacingNorth))]
    [InlineData(nameof(GeneralPose))]
    public void KnownPose_FirstUpdateRotatesSensorAxesIntoTheDocumentedWorldFrame(string poseName)
    {
        Pose pose = PoseByName(poseName);
        var filter = new OrientationFilter();

        Mat3 m = filter.Update(PoseFrame(pose, Counts(0, 0, 0)), 0.1);

        AssertMath.Matrix(pose.ExpectedMatrix, m, PoseTolerance, $"{poseName} matrix");

        // The behavioural half of the same claim: transforming a sensor-frame basis vector must
        // land on the world direction that sensor axis physically points at.
        Vec3 sensorX = AssertMath.Transform(m, new Vec3(1, 0, 0));
        Vec3 sensorY = AssertMath.Transform(m, new Vec3(0, 1, 0));
        Vec3 sensorZ = AssertMath.Transform(m, new Vec3(0, 0, 1));

        AssertMath.Close(pose.X.X, sensorX.X, PoseTolerance, $"{poseName}: Transform(sensor X).X");
        AssertMath.Close(pose.X.Y, sensorX.Y, PoseTolerance, $"{poseName}: Transform(sensor X).Y");
        AssertMath.Close(pose.X.Z, sensorX.Z, PoseTolerance, $"{poseName}: Transform(sensor X).Z");

        AssertMath.Close(pose.Y.X, sensorY.X, PoseTolerance, $"{poseName}: Transform(sensor Y).X");
        AssertMath.Close(pose.Y.Y, sensorY.Y, PoseTolerance, $"{poseName}: Transform(sensor Y).Y");
        AssertMath.Close(pose.Y.Z, sensorY.Z, PoseTolerance, $"{poseName}: Transform(sensor Y).Z");

        AssertMath.Close(pose.Z.X, sensorZ.X, PoseTolerance, $"{poseName}: Transform(sensor Z).X");
        AssertMath.Close(pose.Z.Y, sensorZ.Y, PoseTolerance, $"{poseName}: Transform(sensor Z).Y");
        AssertMath.Close(pose.Z.Z, sensorZ.Z, PoseTolerance, $"{poseName}: Transform(sensor Z).Z");

        // The look direction is the sensor's -Z axis; it must be a unit vector pointing where the
        // board physically looks.
        Vec3 look = AssertMath.Transform(m, WorldFrame.SensorLookAxis);
        AssertMath.Close(-pose.Z.X, look.X, PoseTolerance, $"{poseName}: look.X");
        AssertMath.Close(-pose.Z.Y, look.Y, PoseTolerance, $"{poseName}: look.Y");
        AssertMath.Close(-pose.Z.Z, look.Z, PoseTolerance, $"{poseName}: look.Z");
        AssertMath.Close(1, look.Length, PoseTolerance, $"{poseName}: |look|");

        AssertMath.OrthonormalRows(m, poseName);
    }

    [Fact]
    public void TiltedUp30Degrees_ReportsTheTiltOfTheLookDirection()
    {
        // cos 30 is irrational, so the integer counts are rounded here. Counts are scaled far above
        // what a real BMA180 produces so the rounding error (0.5 counts out of 10^7) stays well
        // inside the tolerance below; the filter's own math is exact.
        var filter = new OrientationFilter();
        Mat3 m = filter.Update(PoseFrame(TiltedUp30, Counts(0, 0, 0), 10_000_000, 10_000_000), 0.1);

        AssertMath.Matrix(TiltedUp30.ExpectedMatrix, m, 1e-7, "30 degree pose");

        // Look-direction elevation above horizontal = asin(-M32). This is the quantity the UI
        // prints; see OrientationAngleMappingTests for the label defect (D1).
        double elevation = Math.Asin(Math.Clamp(-m.M32, -1, 1)) * 180.0 / Math.PI;
        AssertMath.Close(30.0, elevation, 1e-5, "look-direction elevation");
    }

    [Fact]
    public void TiltedUp345_IsRecognizedAs36Point87Degrees()
    {
        var filter = new OrientationFilter();
        Mat3 m = filter.Update(PoseFrame(TiltedUp345, Counts(0, 0, 0)), 0.1);

        double elevation = Math.Asin(Math.Clamp(-m.M32, -1, 1)) * 180.0 / Math.PI;
        AssertMath.Close(Math.Asin(0.6) * 180.0 / Math.PI, elevation, 1e-6, "look-direction elevation");
    }

    [Fact]
    public void FlatBoard_ReportsZeroTiltZeroRollAndZeroHeading()
    {
        var filter = new OrientationFilter();
        Mat3 m = filter.Update(PoseFrame(FlatFacingNorth, Counts(0, 0, 0)), 0.1);

        AssertMath.Close(0, Math.Asin(Math.Clamp(-m.M32, -1, 1)) * 180.0 / Math.PI, PoseTolerance, "elevation");
        AssertMath.Close(0, Math.Atan2(m.M12, m.M22) * 180.0 / Math.PI, PoseTolerance, "roll about the optical axis");
        AssertMath.Close(0, Math.Atan2(-m.M31, m.M33) * 180.0 / Math.PI, PoseTolerance, "heading (north)");
    }

    [Fact]
    public void FlatBoardFacingEast_ReportsHeading90()
    {
        var filter = new OrientationFilter();
        Mat3 m = filter.Update(PoseFrame(FlatFacingEast, Counts(0, 0, 0)), 0.1);

        double heading = Math.Atan2(-m.M31, m.M33) * 180.0 / Math.PI;
        if (heading < 0)
        {
            heading += 360;
        }

        AssertMath.Close(90.0, heading, PoseTolerance, "heading (east)");
    }

    [Fact]
    public void InvertedBoard_IsDistinguishedFromFlatBoard()
    {
        Mat3 flat = new OrientationFilter().Update(PoseFrame(FlatFacingNorth, Counts(0, 0, 0)), 0.1);
        Mat3 inverted = new OrientationFilter().Update(PoseFrame(InvertedFacingNorth, Counts(0, 0, 0)), 0.1);

        // M22 is the world-up component of sensor Y: it flips sign when the board is inverted.
        AssertMath.Close(1, flat.M22, PoseTolerance, "flat M22");
        AssertMath.Close(-1, inverted.M22, PoseTolerance, "inverted M22");
        AssertMath.OrthonormalRows(inverted, "inverted");
    }

    /// <summary>
    /// Pins the exact element layout of Mat3.CreateOrientation(): rows hold the sensor axes, built
    /// from (_east, _up, -_north), and the north column is NEGATED. A transposition or a dropped
    /// negation fails here. GeneralPose is used because none of its nine elements is zero or
    /// repeated, so a permutation cannot slip through.
    ///
    /// The fourth row (M14/M24/M34 and WPF's OffsetX/OffsetY/OffsetZ) is not asserted here because
    /// Mat3 deliberately models rotation only and has no fourth row. That row is the adapter's
    /// responsibility and is pinned in WpfMathTests instead.
    /// </summary>
    [Fact]
    public void MatrixLayout_NorthColumnIsNegated_AndRowsAreSensorAxes()
    {
        var filter = new OrientationFilter();
        Mat3 m = filter.Update(PoseFrame(GeneralPose, Counts(0, 0, 0)), 0.1);

        // Row 1 = sensor X axis in world = (2/3, 1/3, 2/3)
        AssertMath.Close(2.0 / 3, m.M11, 1e-9, "M11");
        AssertMath.Close(1.0 / 3, m.M12, 1e-9, "M12");
        AssertMath.Close(2.0 / 3, m.M13, 1e-9, "M13");

        // Row 2 = sensor Y axis in world = (-2/3, 2/3, 1/3)
        AssertMath.Close(-2.0 / 3, m.M21, 1e-9, "M21");
        AssertMath.Close(2.0 / 3, m.M22, 1e-9, "M22");
        AssertMath.Close(1.0 / 3, m.M23, 1e-9, "M23");

        // Row 3 = sensor Z axis in world = (-1/3, -2/3, 2/3)
        AssertMath.Close(-1.0 / 3, m.M31, 1e-9, "M31");
        AssertMath.Close(-2.0 / 3, m.M32, 1e-9, "M32");
        AssertMath.Close(2.0 / 3, m.M33, 1e-9, "M33");

        // A rotation, not a reflection: determinant +1, not -1. A dropped negation on the north
        // column would make this -1.
        AssertMath.Close(1, AssertMath.Determinant(m), 1e-9, "determinant (rotation, not reflection)");
    }

    [Fact]
    public void Current_AlwaysMatchesTheLastUpdate()
    {
        var filter = new OrientationFilter();
        Mat3 m = filter.Update(PoseFrame(FlatFacingEast, Counts(0, 0, 0)), 0.1);

        AssertMath.Matrix(m, filter.Current, AssertMath.Exact, "Current after Update");
    }

    // -----------------------------------------------------------------------------------------
    // Degenerate input guards.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void ZeroAccel_ReturnsCurrentMatrixUnchanged()
    {
        var filter = new OrientationFilter();
        Mat3 established = filter.Update(PoseFrame(FlatFacingEast, Counts(0, 0, 0)), 0.1);

        // With integer counts, LengthSquared < 1 can only mean all three components are zero, so
        // this guard is really an all-zeros check (see the next test).
        Mat3 after = filter.Update(Frame(Counts(500, 500, 500), Counts(0, 0, 0), Counts(0, 500, 0)), 0.1);

        AssertMath.Matrix(established, after, AssertMath.Exact, "zero accel");
    }

    [Fact]
    public void ZeroMag_ReturnsCurrentMatrixUnchanged()
    {
        var filter = new OrientationFilter();
        Mat3 established = filter.Update(PoseFrame(FlatFacingEast, Counts(0, 0, 0)), 0.1);

        Mat3 after = filter.Update(Frame(Counts(500, 500, 500), Counts(0, 500, 0), Counts(0, 0, 0)), 0.1);

        AssertMath.Matrix(established, after, AssertMath.Exact, "zero mag");
    }

    [Fact]
    public void SmallestPossibleNonZeroAccel_PassesTheGuard_BecauseCountsAreIntegers()
    {
        // accel = (1,0,0) has LengthSquared == 1, which is NOT < 1, so the guard does not fire.
        // Sensor X is then aligned with world up, so row 1 of the matrix is (0,1,0).
        var filter = new OrientationFilter();
        Mat3 m = filter.Update(Frame(Counts(0, 0, 0), Counts(1, 0, 0), Counts(0, 0, -1)), 0.1);

        AssertMath.Close(0, m.M11, 1e-9, "M11");
        AssertMath.Close(1, m.M12, 1e-9, "M12: sensor X points where the accelerometer says up is");
        AssertMath.Close(0, m.M13, 1e-9, "M13");
        AssertMath.Close(0, m.M22, 1e-9, "M22: sensor Y is not the up axis here");
        AssertMath.OrthonormalRows(m, "unit-count accel");
    }

    [Fact]
    public void MagParallelToAccel_IsRejected_CrossProductIsDegenerate()
    {
        var filter = new OrientationFilter();
        Mat3 established = filter.Update(PoseFrame(FlatFacingNorth, Counts(0, 0, 0)), 0.1);

        // mag parallel to accel => Cross(mag, up) == 0 => eastMeasured.LengthSquared == 0 < 1e-6.
        Mat3 after = filter.Update(Frame(Counts(0, 0, 0), Counts(0, 500, 0), Counts(0, 250, 0)), 0.1);

        AssertMath.Matrix(established, after, AssertMath.Exact, "collinear mag/accel");
    }

    [Fact]
    public void AntiParallelMagIsAlsoRejected()
    {
        var filter = new OrientationFilter();
        Mat3 established = filter.Update(PoseFrame(FlatFacingNorth, Counts(0, 0, 0)), 0.1);

        Mat3 after = filter.Update(Frame(Counts(0, 0, 0), Counts(0, 500, 0), Counts(0, -400, 0)), 0.1);

        AssertMath.Matrix(established, after, AssertMath.Exact, "anti-parallel mag/accel");
    }

    [Fact]
    public void Update_ThrowsOnShortImuArray_NoValidationExists()
    {
        // CHARACTERIZATION, NOT APPROVAL. The parser guarantees exactly 9 IMU values, so the filter
        // validates nothing. If a future ISensorStream (Phase 3) can deliver a truncated frame, this
        // is an IndexOutOfRangeException on the UI thread.
        var filter = new OrientationFilter();

        Assert.Throws<IndexOutOfRangeException>(() => filter.Update([1, 2, 3, 4, 5, 6, 7, 8], 0.1));
    }

    // -----------------------------------------------------------------------------------------
    // Reset semantics.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Reset_ThenFirstUpdate_RebuildsFromMeasurements_NotFromPreviousState()
    {
        var filter = new OrientationFilter();
        filter.Update(PoseFrame(FlatFacingEast, Counts(0, 0, 0)), 0.1);

        filter.Reset();
        Mat3 afterReset = filter.Update(PoseFrame(InvertedFacingNorth, Counts(0, 0, 0)), 0.1);

        AssertMath.Matrix(InvertedFacingNorth.ExpectedMatrix, afterReset, PoseTolerance, "after Reset");
    }

    [Fact]
    public void Reset_DoesNotClearTheOrientationAxes_StalePoseIsStillReported()
    {
        // FINDING. Reset() clears _initialized, _calibrationCount and the gyro bias, but NOT
        // _up/_east/_north. So between Reset() and the first successful Update, Current still
        // reports the pose measured before the Reset. In MainWindow this happens on every
        // reconnect: _imuFilter.Reset() runs, and the viewport keeps the previous orientation
        // until a valid frame arrives.
        var filter = new OrientationFilter();
        Mat3 beforeReset = filter.Update(PoseFrame(FlatFacingEast, Counts(0, 0, 0)), 0.1);

        filter.Reset();

        AssertMath.Matrix(beforeReset, filter.Current, AssertMath.Exact, "Current immediately after Reset");

        // And a rejected (degenerate) frame right after Reset leaves that stale pose in place.
        Mat3 afterBadFrame = filter.Update(Frame(Counts(0, 0, 0), Counts(0, 0, 0), Counts(0, 0, 0)), 0.1);
        AssertMath.Matrix(beforeReset, afterBadFrame, AssertMath.Exact, "stale pose survives a rejected frame");
    }

    // -----------------------------------------------------------------------------------------
    // dt handling.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void DtIsClampedToHalfSecond_LargeDtBehavesExactlyLikeHalfSecond()
    {
        Vec3 moving = Counts(900, -700, 1100);

        var clampProbe = Calibrated(FlatFacingNorth);
        var reference = Calibrated(FlatFacingNorth);

        Mat3 clampM = default, refM = default;
        for (int i = 0; i < 20; i++)
        {
            // Identical call sequences; only one dt differs, and it is above the clamp.
            refM = reference.Update(PoseFrame(FlatFacingNorth, moving), 0.5);
            clampM = clampProbe.Update(PoseFrame(FlatFacingNorth, moving), i == 10 ? 10.0 : 0.5);
        }

        AssertMath.Matrix(refM, clampM, 1e-9, "dt=10 must behave exactly like dt=0.5");
    }

    [Fact]
    public void DtJustAboveTheClamp_IsTreatedAsTheClampValue_0Point6BehavesExactlyLike0Point5()
    {
        Vec3 moving = Counts(900, -700, 1100);

        var clamped = Calibrated(FlatFacingNorth);
        var reference = Calibrated(FlatFacingNorth);

        Mat3 clampedM = default, refM = default;
        for (int i = 0; i < 20; i++)
        {
            refM = reference.Update(PoseFrame(FlatFacingNorth, moving), 0.5);
            clampedM = clamped.Update(PoseFrame(FlatFacingNorth, moving), i == 10 ? 0.6 : 0.5);
        }

        AssertMath.Matrix(refM, clampedM, 1e-9, "dt=0.6 must behave exactly like dt=0.5");
    }

    [Fact]
    public void DtBelowTheClamp_DoesProduceADifferentResult_TheClampIsNotSmallerThanHalfASecond()
    {
        // 0.4 rather than 0.49, deliberately. The gyro-propagation term is proportional to
        // dt * exp(-2*dt), whose maximum is exactly at dt = 0.5, so a value just below the clamp
        // differs from it by only ~5e-6 and the assertion would be dominated by floating-point
        // noise instead of by the clamp.
        Vec3 moving = Counts(900, -700, 1100);

        var atClamp = Calibrated(FlatFacingNorth);
        var belowClamp = Calibrated(FlatFacingNorth);

        Mat3 at = atClamp.Update(PoseFrame(FlatFacingNorth, moving), 0.5);
        Mat3 below = belowClamp.Update(PoseFrame(FlatFacingNorth, moving), 0.4);

        double difference = Math.Abs(at.M12 - below.M12);
        Assert.True(difference > 1e-4,
            $"dt=0.5 and dt=0.4 should differ; M12 difference was {difference:R}.");
    }

    [Fact]
    public void NegativeDtIsClampedToZero_NoCorrectionAndNoPropagation()
    {
        var filter = new OrientationFilter();
        Mat3 pose = filter.Update(PoseFrame(FlatFacingNorth, Counts(0, 0, 0)), 0.1);

        // dt <= 0 => alpha = 1 - exp(0) = 0, and the propagation term is multiplied by 0.
        Mat3 after = filter.Update(PoseFrame(FlatFacingEast, Counts(0, 0, 0)), -5.0);

        AssertMath.Matrix(pose, after, AssertMath.Exact, "dt=-5 must be a no-op");
    }

    [Fact]
    public void ZeroDtIsACompleteNoOp_AxesAreNotEvenPulledTowardTheMeasurements()
    {
        var filter = new OrientationFilter();
        Mat3 pose = filter.Update(PoseFrame(FlatFacingNorth, Counts(0, 0, 0)), 0.1);

        Mat3 after = filter.Update(PoseFrame(InvertedFacingNorth, Counts(0, 0, 0)), 0.0);

        AssertMath.Matrix(pose, after, AssertMath.Exact, "dt=0 must be a no-op");
    }

    // -----------------------------------------------------------------------------------------
    // Gyro bias calibration window.
    //
    // FINDING: the plan says "exactly CalibrationSamples (50) samples ... the 50th sets _gyroBias".
    // That is the 50th *accumulating* sample, which is the 51st *call* to Update — the very first
    // call returns through the initialization path and never accumulates. Pinned precisely below.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void CalibrationWindow_Is50AccumulatingSamples_Not50Calls()
    {
        Vec3 g = Counts(120, -80, 200);
        Vec3 probe = g + Counts(500, 0, 0); // delta 500 > threshold 60 => must rotate if calibrated

        var filter = new OrientationFilter();
        filter.Update(PoseFrame(FlatFacingNorth, g), 0.1); // call 1: init only, no accumulation

        // Calls 2..51
        // always zero, so with a constant pose the matrix must not move — including call 51, where
        // the probe gyro is fed but still only accumulated. If the window were counted in *calls*
        // it would have ended at call 50 and this assertion would fail at call 51.
        for (int call = 2; call <= 51; call++)
        {
            Mat3 m = filter.Update(PoseFrame(FlatFacingNorth, call == 51 ? probe : g), 0.1);
            AssertMath.Matrix(FlatFacingNorth.ExpectedMatrix, m, 1e-9, $"call {call} must not rotate (still calibrating)");
        }

        // Call 52 is the first call outside the calibration branch. The bias is now established, so
        // the same probe gyro produces real rotation.
        Mat3 moved = filter.Update(PoseFrame(FlatFacingNorth, probe), 0.1);
        Assert.True(Math.Abs(moved.M32 - FlatFacingNorth.ExpectedMatrix.M32) > 1e-6,
            "call 52 must apply gyro rotation; the calibration window did not end after 50 accumulating samples.");
    }

    [Fact]
    public void GyroIsCompletelyIgnoredDuringCalibration_RegardlessOfMagnitude()
    {
        var filter = new OrientationFilter();
        filter.Update(PoseFrame(FlatFacingNorth, Counts(0, 0, 0)), 0.1);

        Vec3 violent = Counts(30000, -30000, 30000);

        for (int call = 2; call <= 51; call++)
        {
            Mat3 pose = filter.Update(PoseFrame(FlatFacingNorth, violent), 0.1);
            AssertMath.Matrix(FlatFacingNorth.ExpectedMatrix, pose, 1e-9, $"call {call} during calibration");
        }
    }

    [Fact]
    public void CalibrationBiasIsTheMeanOfTheWindow_AndGyroAtThatBiasProducesNoRotation()
    {
        Vec3 g = Counts(120, -80, 200);
        var filter = Calibrated(FlatFacingNorth, g);

        // gyro == bias => delta == 0 => below the stationary threshold => no rotation at all.
        // This only holds if the bias really was established as the mean of the window.
        Mat3 atBias = filter.Update(PoseFrame(FlatFacingNorth, g), 0.1);
        AssertMath.Matrix(FlatFacingNorth.ExpectedMatrix, atBias, 1e-9, "gyro exactly at bias must produce no rotation");

        // A delta of 50 counts (< 60) is also treated as rest.
        Mat3 underThreshold = filter.Update(PoseFrame(FlatFacingNorth, g + Counts(50, 0, 0)), 0.1);
        AssertMath.Matrix(FlatFacingNorth.ExpectedMatrix, underThreshold, 1e-9, "delta 50 counts must be treated as rest");
    }

    [Fact]
    public void StationaryThresholdIsStrictlyLessThan60Counts_59IsRestAnd60IsMotion()
    {
        var rest = Calibrated(FlatFacingNorth);
        var motion = Calibrated(FlatFacingNorth);

        Mat3 restResult = rest.Update(PoseFrame(FlatFacingNorth, Counts(59, 0, 0)), 0.1);
        Mat3 motionResult = motion.Update(PoseFrame(FlatFacingNorth, Counts(60, 0, 0)), 0.1);

        AssertMath.Matrix(FlatFacingNorth.ExpectedMatrix, restResult, 1e-9, "delta 59 counts = rest");
        Assert.True(Math.Abs(motionResult.M32 - FlatFacingNorth.ExpectedMatrix.M32) > 1e-9,
            "delta 60 counts should have rotated the estimate (the comparison is '<', not '<='), but the matrix did not move.");
    }

    [Fact]
    public void StationaryBoardWithConstantPose_IsPerfectlyStable_MatrixIsFrozen()
    {
        // Once the axes already agree with the measurements and omega is zero, the blend is an
        // identity operation. Any drift here would show up as the board slowly rotating on a bench.
        var filter = Calibrated(TiltedUp345);

        for (int i = 0; i < 200; i++)
        {
            Mat3 m = filter.Update(PoseFrame(TiltedUp345, Counts(0, 0, 0)), 0.1);
            AssertMath.Matrix(TiltedUp345.ExpectedMatrix, m, 1e-9, $"stationary iteration {i}");
        }
    }

    [Fact]
    public void ConstantRotation_ShearsTheEstimateAwayFromTheMeasuredPose()
    {
        // Same pose inputs, but a gyro that is clearly moving. The gyro path and the accel/mag path
        // disagree by design; this pins that they really are separate contributions.
        var still = Calibrated(FlatFacingNorth);
        var spinning = Calibrated(FlatFacingNorth);

        Vec3 spin = Counts(0, 0, 1000); // rotation about the world vertical axis
        Mat3 stillM = default, spinningM = default;
        for (int i = 0; i < 20; i++)
        {
            stillM = still.Update(PoseFrame(FlatFacingNorth, Counts(0, 0, 0)), 0.1);
            spinningM = spinning.Update(PoseFrame(FlatFacingNorth, spin), 0.1);
        }

        AssertMath.Matrix(FlatFacingNorth.ExpectedMatrix, stillM, 1e-9, "still board");
        AssertMath.OrthonormalRows(spinningM, "spinning board");

        double difference = Math.Abs(spinningM.M11 - stillM.M11) + Math.Abs(spinningM.M12 - stillM.M12)
                          + Math.Abs(spinningM.M21 - stillM.M21) + Math.Abs(spinningM.M22 - stillM.M22);
        Assert.True(difference > 1e-3, $"expected the spinning estimate to diverge, got total difference {difference:R}.");
    }

    [Fact]
    public void CorrectionTimeConstantBlendsAbout5PercentPerFrameAt100ms()
    {
        // alpha = 1 - exp(-dt / 2.0). At dt = 0.1 that is 0.04877. With omega == 0 and a pose that
        // differs from the current estimate by exactly one blend, the result must sit at that
        // fraction. Proves CorrectionTimeConstantSeconds == 2.0 is the value actually in force.
        var filter = new OrientationFilter();
        Mat3 flat = filter.Update(PoseFrame(FlatFacingNorth, Counts(0, 0, 0)), 0.1); // initialized, no accumulation

        // Call 2 is still inside the calibration branch, so omega == 0 and only the blend acts.
        Mat3 blended = filter.Update(PoseFrame(TiltedUp345, Counts(0, 0, 0)), 0.1);

        double alpha = 1 - Math.Exp(-0.1 / 2.0);

        // _up starts
        // Explicit Vec3 types are required: a target-typed `new(...)` has no type of its own, so it
        // cannot be the left operand of an operator.
        Vec3 expectedUp = new Vec3(0, 1, 0) * (1 - alpha) + new Vec3(0, 0.8, -0.6) * alpha;
        expectedUp = expectedUp.Normalized();

        // M32 is the sensor-Z component of world up (row 3, column 2).
        AssertMath.Close(expectedUp.Z, blended.M32, 1e-9, "M32 after one blend at dt=0.1");
        AssertMath.Close(1, flat.M22, PoseTolerance, "M22 before the blend");
    }

    // -----------------------------------------------------------------------------------------
    // Orthonormality as a property test — the drift detector.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Orthonormality_HoldsOverLongRandomInputSequences()
    {
        // Seeded so a failure is reproducible. 12 sequences x 400 frames.
        var details = new List<string>();
        int failures = 0;

        for (int sequence = 0; sequence < 12; sequence++)
        {
            var rng = new Random(0xEC40 + sequence);
            var filter = new OrientationFilter();

            for (int frame = 0; frame < 400; frame++)
            {
                int[] imu = RandomImu(rng);
                double dt = frame == 0 ? 0.1 : rng.NextDouble() * 0.4;

                Mat3 m = filter.Update(imu, dt);

                try
                {
                    AssertMath.OrthonormalRows(m, $"sequence {sequence} frame {frame}");
                }
                catch (Exception ex)
                {
                    failures++;
                    if (details.Count < 8)
                    {
                        details.Add(ex.Message);
                    }
                }
            }
        }

        if (failures > 0)
        {
            Assert.Fail($"{failures} orthonormality violations. First few:{Environment.NewLine}" +
                        string.Join(Environment.NewLine, details));
        }
    }

    [Fact]
    public void Orthonormality_HoldsWhenInputsArePathologicalButNotRejected()
    {
        // Near-collinear mag/accel (legal but ill-conditioned), unit-length counts, and accel along
        // the body diagonal. These are the inputs most likely to expose a normalization blow-up.
        var filter = new OrientationFilter();

        int[][] cases =
        [
            [0, 0, 0, 0, 1, 0, 0, 1, 1],   // mag almost parallel to accel
            [0, 0, 0, 1, 0, 0, 0, 0, 1],   // unit counts, mag perpendicular to accel
            [0, 0, 0, 0, 1, 0, 0, -1, 1],  // mag almost anti-parallel
            [1, 1, 1, 1, 1, 1, 1, 1, -1],  // accel along the body diagonal
            [0, 0, 0, 0, -1, 0, 0, 0, -1], // inverted, unit counts
        ];

        for (int round = 0; round < 3; round++)
        {
            foreach (int[] imu in cases)
            {
                for (int i = 0; i < 40; i++)
                {
                    Mat3 m = filter.Update(imu, 0.1);
                    AssertMath.OrthonormalRows(m, $"case [{string.Join(",", imu)}] round {round} iteration {i}");
                }
            }
        }
    }

    private static int[] RandomImu(Random rng)
    {
        Vec3 accel = RandomVector(rng, 500);
        if (accel.LengthSquared < 1)
        {
            accel = Counts(0, 500, 0);
        }

        Vec3 mag = RandomVector(rng, 450);
        bool degenerate = mag.LengthSquared < 1
            || Math.Abs(AssertMath.Dot(mag, accel)) / (mag.Length * accel.Length) > 0.999;

        if (degenerate)
        {
            // Keep the frame out of the guards so the test exercises the filter, not its guards.
            mag = Counts(-accel.Y * 2, accel.X * 2, 100);
            if (mag.LengthSquared < 1)
            {
                mag = Counts(0, 0, 450);
            }
        }

        return Frame(RandomVector(rng, 1500), accel, mag);
    }

    private static Vec3 RandomVector(Random rng, int scale)
    {
        int Next() => rng.Next(-scale, scale);
        return new Vec3(Next(), Next(), Next());
    }
}
