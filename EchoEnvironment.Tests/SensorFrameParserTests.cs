using System.Globalization;
using EchoEnvironment.Core;
using Xunit;

namespace EchoEnvironment.Tests;

/// <summary>
/// Characterization tests for <see cref="SensorFrameParser"/>.
///
/// These pinned CURRENT behaviour so that Phase 2 (moving the class into EchoEnvironment.Core)
/// could be proven to change nothing. They passed before the move and pass after it, unchanged -
/// which is the point. Where the current behaviour is a deliberate quirk or an unvalidated hole,
/// the test name and comment say so; a future reader must not "fix" it silently.
/// </summary>
public class SensorFrameParserTests
{
    private static string Line(params int[] values) => string.Join(",", values);

    private static string LineOfCount(int count) =>
        string.Join(",", Enumerable.Range(100, count).Select(v => v.ToString(CultureInfo.InvariantCulture)));

    private static int[] Range16() => Enumerable.Range(100, 16).ToArray();

    /// <summary>
    /// THE ADAPTER THAT REPLACED THE DELETED PRODUCTION OVERLOAD (§12.8 item 4).
    ///
    /// SensorFrameParser used to carry two TryParse signatures: the original `out int[], out int[]?`
    /// one, and the `out SensorFrame` one added in Phase 3 (§9.7). The array overload had ZERO
    /// production call sites - SerialSensorStream and ReplaySensorStream both use the frame overload,
    /// and MainWindow never names the parser at all - against 28 call sites in this file. Its own doc
    /// comment said "kept for one phase and deleted in Phase 5", and Phase 5 is where it was deleted.
    ///
    /// WHY THE ASSERTIONS BELOW KEEP THEIR ORIGINAL WORDING. Every characterization assertion here -
    /// the accepted shapes, the rejections, the out-param reset, Assert.Null(imu), Assert.Empty(distances) -
    /// is about the parsing body, TryParseValues, which neither moved nor changed. Rewriting 28
    /// assertions into frame-shaped form (frame.Distances.IsEmpty instead of Assert.Empty(distances))
    /// would have traded a set of passing, meaningful tests for a set of passing, restated tests, for no
    /// gain in coverage. This adapter reproduces the array/null shape the deleted overload produced, on
    /// top of the frame overload, using only facts the frame overload's own tests already pin:
    /// Distances and Imu are separate arrays, and a frame without IMU reports HasImu false with an empty
    /// Imu. On rejection the frame is default, so distances is empty and imu is null - which is what
    /// OnRejection_OutParamsAreReset_NotLeftStaleFromThePreviousFrame then checks.
    ///
    /// WHAT IS NO LONGER ASSERTED, STATED RATHER THAN HIDDEN: nothing here can any longer prove that
    /// the two overloads agreed, because only one exists. The shape table that comparison used to check
    /// is asserted directly instead by TheSingleOverload_AcceptsAndRejectsExactlyTheDocumentedShapes.
    /// </summary>
    private static bool TryParseLegacy(string line, out int[] distances, out int[]? imu)
    {
        bool ok = SensorFrameParser.TryParse(line, out SensorFrame frame);

        distances = frame.Distances.ToArray();
        imu = frame.HasImu ? frame.Imu.ToArray() : null;
        return ok;
    }

    // -----------------------------------------------------------------------------------------
    // The four legal frame shapes.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void SixteenZones_Parses16Distances_AndNoImu()
    {
        int[] values = Range16();

        bool ok = TryParseLegacy(Line(values), out int[] distances, out int[]? imu);

        Assert.True(ok);
        Assert.Equal(values, distances);
        Assert.Null(imu);
    }

    [Fact]
    public void SixtyFourZones_Parses64Distances_AndNoImu()
    {
        int[] values = Enumerable.Range(100, 64).ToArray();

        bool ok = TryParseLegacy(Line(values), out int[] distances, out int[]? imu);

        Assert.True(ok);
        Assert.Equal(values, distances);
        Assert.Null(imu);
    }

    /// <summary>
    /// QUIRK, deliberately pinned: a 25-value line is legal and means "16 zones + 9 IMU counts".
    /// 25 is NOT a zone count. Phase 2 must preserve this exactly; confirm with the firmware before
    /// ever changing it (migration plan risk register, entry 3).
    /// </summary>
    [Fact]
    public void TwentyFiveValues_IsLegal_AndMeans16ZonesPlusImu()
    {
        int[] values = Enumerable.Range(100, 25).ToArray();

        bool ok = TryParseLegacy(Line(values), out int[] distances, out int[]? imu);

        Assert.True(ok);
        Assert.Equal(16, distances.Length);
        Assert.NotNull(imu);
        Assert.Equal(9, imu.Length);
    }

    /// <summary>QUIRK, deliberately pinned: 73 values means "64 zones + 9 IMU counts".</summary>
    [Fact]
    public void SeventyThreeValues_IsLegal_AndMeans64ZonesPlusImu()
    {
        int[] values = Enumerable.Range(100, 73).ToArray();

        bool ok = TryParseLegacy(Line(values), out int[] distances, out int[]? imu);

        Assert.True(ok);
        Assert.Equal(values[..64], distances);
        Assert.NotNull(imu);
        Assert.Equal(9, imu.Length);
    }

    [Fact]
    public void ImuIsAlwaysTheTrailingValues_NotTheLeadingOnes()
    {
        int[] values = Enumerable.Range(100, 25).ToArray();

        Assert.True(TryParseLegacy(Line(values), out int[] distances, out int[]? imu));
        Assert.NotNull(imu);

        Assert.Equal(values[..16], distances);
        Assert.Equal(values[16..], imu);

        // Documented channel order (from the production comment):
        // ITG3200 gyro x y z, BMA180 accel x y z, HMC5883L mag x y z.
        Assert.Equal(values[16], imu[0]); // gyro x
        Assert.Equal(values[17], imu[1]); // gyro y
        Assert.Equal(values[18], imu[2]); // gyro z
        Assert.Equal(values[19], imu[3]); // accel x
        Assert.Equal(values[20], imu[4]); // accel y
        Assert.Equal(values[21], imu[5]); // accel z
        Assert.Equal(values[22], imu[6]); // mag x
        Assert.Equal(values[23], imu[7]); // mag y
        Assert.Equal(values[24], imu[8]); // mag z
    }

    // -----------------------------------------------------------------------------------------
    // Separators and whitespace.
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(",")]
    [InlineData(";")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void SingleSeparator_EachIsAccepted(string separator)
    {
        string line = string.Join(separator, Range16());

        bool ok = TryParseLegacy(line, out int[] distances, out int[]? imu);

        Assert.True(ok);
        Assert.Equal(16, distances.Length);
        Assert.Null(imu);
    }

    [Fact]
    public void MixedSeparators_AreAccepted()
    {
        string line = "100,101;102 103\t104,105;106 107\t108,109;110 111\t112,113;114 115";

        Assert.True(TryParseLegacy(line, out int[] distances, out int[]? imu));
        Assert.Equal(Range16(), distances);
        Assert.Null(imu);
    }

    [Fact]
    public void RepeatedSeparators_Collapse_AndDoNotInventZones()
    {
        // 16 real values with doubled and tripled separators. If empty entries were counted, the
        // frame would be rejected as an illegal count instead of accepted.
        string line = "100,,101;;;102   103\t\t104,105,106,107,108,109,110,111,112,113,114,,115";

        Assert.True(TryParseLegacy(line, out int[] distances, out int[]? imu));
        Assert.Equal(Range16(), distances);
    }

    [Theory]
    [InlineData("  100,101,102,103,104,105,106,107,108,109,110,111,112,113,114,115  ")]
    [InlineData("100,101,102,103,104,105,106,107,108,109,110,111,112,113,114,115\r\n")]
    [InlineData("100,101,102,103,104,105,106,107,108,109,110,111,112,113,114,115\n")]
    [InlineData("\t100,101,102,103,104,105,106,107,108,109,110,111,112,113,114,115\t")]
    public void LeadingAndTrailingWhitespace_IsTrimmed(string line)
    {
        Assert.True(TryParseLegacy(line, out int[] distances, out int[]? imu));
        Assert.Equal(16, distances.Length);
        Assert.Equal(115, distances[15]);
    }

    [Fact]
    public void WhitespaceAroundEachToken_IsTrimmedPerToken()
    {
        string line = " 100 , 101 , 102 , 103 , 104 , 105 , 106 , 107 , 108 , 109 , 110 , 111 , 112 , 113 , 114 , 115 ";

        Assert.True(TryParseLegacy(line, out int[] distances, out int[]? imu));
        Assert.Equal(Range16(), distances);
    }

    // -----------------------------------------------------------------------------------------
    // Rejections.
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    [InlineData(",,,")]
    [InlineData(";;;")]
    [InlineData("\t\t , , \t")]
    public void EmptyOrSeparatorOnlyLine_IsRejected(string line)
    {
        bool ok = TryParseLegacy(line, out int[] distances, out int[]? imu);

        Assert.False(ok);
        Assert.Empty(distances);
        Assert.Null(imu);
    }

    [Fact]
    public void NullLine_IsRejectedWithoutThrowing()
    {
        // string.IsNullOrWhiteSpace(null) is true, so this is a rejection rather than a NullReferenceException.
        bool ok = TryParseLegacy(null!, out int[] distances, out int[]? imu);

        Assert.False(ok);
        Assert.Empty(distances);
        Assert.Null(imu);
    }

    [Theory]
    [InlineData("100,x,102,103,104,105,106,107,108,109,110,111,112,113,114,115")]
    [InlineData("100,101,102,103,104,105,106,107,108,109,110,111,112,113,114,1o5")]
    [InlineData("100,101,102,103,104,105,106,107,108,109,110,111,112,113,114,115,116,117,118,119,120,121,122,123,x")]
    public void AnyUnparseableToken_RejectsTheWholeFrame_AndLeavesDistancesEmpty(string line)
    {
        bool ok = TryParseLegacy(line, out int[] distances, out int[]? imu);

        Assert.False(ok);
        Assert.Empty(distances);
        Assert.Null(imu);
    }

    [Fact]
    public void ASpaceInsideANumber_SplitsItIntoTwoTokens_AndCorruptsTheFrame()
    {
        // CHARACTERIZATION, NOT APPROVAL. "1 15" becomes the tokens "1" and "15". The line still has
        // exactly 16 tokens, so it is ACCEPTED with two wrong values, and the trailing zone count is
        // silently shifted. A malformed line can therefore pass validation and reach UpdateFrame.
        // This is the hole that a stricter framing layer (Phase 3 LineFramer) should close.
        string line = "100,101,102,103,104,105,106,107,108,109,110,111,112,113,1 15";

        Assert.True(TryParseLegacy(line, out int[] distances, out int[]? imu));
        Assert.Equal(16, distances.Length);
        Assert.Equal(Enumerable.Range(100, 14).ToArray(), distances[..14]);
        Assert.Equal(1, distances[14]);  // was meant to be 114
        Assert.Equal(15, distances[15]); // was meant to be 115
    }

    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(17)]
    [InlineData(24)]
    [InlineData(26)]
    [InlineData(63)]
    [InlineData(65)]
    [InlineData(72)]
    [InlineData(74)]
    [InlineData(100)]
    public void IllegalValueCounts_AreRejected(int count)
    {
        bool ok = TryParseLegacy(LineOfCount(count), out int[] distances, out int[]? imu);

        Assert.False(ok);
        Assert.Empty(distances);
        Assert.Null(imu);
    }

    [Theory]
    [InlineData("100.5,101,102,103,104,105,106,107,108,109,110,111,112,113,114,115")]
    [InlineData("1e2,101,102,103,104,105,106,107,108,109,110,111,112,113,114,115")]
    [InlineData("0x64,101,102,103,104,105,106,107,108,109,110,111,112,113,114,115")]
    [InlineData("2147483648,101,102,103,104,105,106,107,108,109,110,111,112,113,114,115")]
    [InlineData("-2147483649,101,102,103,104,105,106,107,108,109,110,111,112,113,114,115")]
    public void NonIntegerTokens_AreRejected(string line)
    {
        // NumberStyles.Integer: no decimal point, no exponent, no group separator, no hex prefix,
        // and int range is enforced.
        Assert.False(TryParseLegacy(line, out int[] distances, out int[]? imu));
        Assert.Empty(distances);
    }

    [Fact]
    public void AGroupSeparatorWrittenWithCommas_IsNotRecognized_AsOneNumber()
    {
        // CHARACTERIZATION: "1,000" is NOT the number 1000 here. ',' is a frame separator, so the
        // line is 16 ordinary zone values (1 and 0 among them) and parses successfully. A device
        // that ever formats its output with thousands separators would be accepted, silently, as a
        // valid frame. Worth knowing before the firmware contract is documented in Phase 3.
        string line = "1,000,101,102,103,104,105,106,107,108,109,110,111,112,113,114";

        Assert.True(TryParseLegacy(line, out int[] distances, out int[]? imu));
        Assert.Equal(16, distances.Length);
        Assert.Equal(1, distances[0]);
        Assert.Equal(0, distances[1]);
    }

    // -----------------------------------------------------------------------------------------
    // Accepted value shapes.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void NegativeMillimetres_AreAccepted()
    {
        // CHARACTERIZATION, NOT APPROVAL. The migration plan flags this as "verify this is intended".
        // Today a negative distance parses successfully and then flows into UpdateFrame, where
        // `mm < MinDistanceMm` hides the zone rather than rejecting the frame. If negative distances
        // are a firmware error, the fix belongs in the parser and this is the test that must change.
        string line = Line(-5, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113, 114, 115);

        Assert.True(TryParseLegacy(line, out int[] distances, out int[]? imu));
        Assert.Equal(-5, distances[0]);
    }

    [Fact]
    public void LeadingPlusSignAndLeadingZeros_AreAccepted()
    {
        string line = "+100,0101,000000102,103,104,105,106,107,108,109,110,111,112,113,114,115";

        Assert.True(TryParseLegacy(line, out int[] distances, out int[]? imu));
        Assert.Equal(100, distances[0]);
        Assert.Equal(101, distances[1]);
        Assert.Equal(102, distances[2]);
    }

    [Fact]
    public void ZeroAndIntMax_AreAccepted()
    {
        string line = Line(0, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113, 114, int.MaxValue);

        Assert.True(TryParseLegacy(line, out int[] distances, out int[]? imu));
        Assert.Equal(0, distances[0]);
        Assert.Equal(int.MaxValue, distances[15]);
    }

    [Fact]
    public void IntMin_IsAccepted()
    {
        string line = Line(int.MinValue, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113, 114, 115);

        Assert.True(TryParseLegacy(line, out int[] distances, out int[]? imu));
        Assert.Equal(int.MinValue, distances[0]);
    }

    // -----------------------------------------------------------------------------------------
    // Culture independence.
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("de-DE")] // decimal separator is ','
    [InlineData("fr-FR")] // group separator is a narrow no-break space
    [InlineData("en-US")]
    [InlineData("cs-CZ")]
    [InlineData("vi-VN")] // decimal separator is '.'
    public void ParsingIsInvariantCulture_RegardlessOfThreadCulture(string cultureName)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(cultureName);

            // A comma-separated line must parse identically in every culture, including cultures
            // where ',' is the decimal separator.
            Assert.True(TryParseLegacy(Line(100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113, 114, 115),
                out int[] distances, out _));
            Assert.Equal(Range16(), distances);

            // A negative value must still parse under cultures with different number formatting.
            Assert.True(TryParseLegacy(Line(-5, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113, 114, 115),
                out int[] negative, out _));
            Assert.Equal(-5, negative[0]);

            // A culture that uses space as a group separator must NOT reinterpret "1 000" as 1000:
            // space is a frame separator here, so it yields two zone values (1 and 0). 14 further
            // values make 16 tokens in total.
            Assert.True(TryParseLegacy("1 000,101,102,103,104,105,106,107,108,109,110,111,112,113,114",
                out int[] spaced, out _));
            Assert.Equal(16, spaced.Length);
            Assert.Equal(1, spaced[0]);
            Assert.Equal(0, spaced[1]);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // -----------------------------------------------------------------------------------------
    // Structural guarantees Phase 2 relies on.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void DistancesAndImuAreSeparateArrays_NotViewsOverOneBuffer()
    {
        Assert.True(TryParseLegacy(LineOfCount(25), out int[] distances, out int[]? imu));
        Assert.NotNull(imu);

        // Mutating one must not affect the other. If Phase 2 switches to ReadOnlyMemory over a shared
        // buffer, this is the test that documents the change in ownership semantics.
        distances[0] = 999;
        Assert.NotEqual(999, imu[0]);

        imu[0] = 888;
        Assert.NotEqual(888, distances[0]);
    }

    [Fact]
    public void OnRejection_OutParamsAreReset_NotLeftStaleFromThePreviousFrame()
    {
        Assert.True(TryParseLegacy(LineOfCount(16), out int[] good, out int[]? goodImu));
        Assert.Equal(16, good.Length);

        // A bad call must reset the out params rather than leaving the previous frame in place.
        Assert.False(TryParseLegacy("nonsense", out int[] bad, out int[]? badImu));
        Assert.Empty(bad);
        Assert.Null(badImu);
    }

    [Fact]
    public void AFrameWithImuThatIsRejected_LeavesNoPartialImuBehind()
    {
        // 25 tokens but the last one is garbage: neither distances nor imu may be partially populated.
        Assert.False(TryParseLegacy(LineOfCount(24) + ",x", out int[] distances, out int[]? imu));
        Assert.Empty(distances);
        Assert.Null(imu);
    }

    // -----------------------------------------------------------------------------------------
    // The SensorFrame overload added in Phase 3 (§9.7, deferred from Phase 2 step 2d).
    //
    // These exist because ISensorStream.FrameReceived is the first consumer of the frame model, and
    // a type nothing tests is a type nothing understands. They used to assert that this overload
    // agreed with the out int[] overload it was derived from, shape by shape. That comparison became
    // void when Phase 5 deleted the array overload (§12.8 item 4) - there is now one signature, so
    // there is nothing left for two signatures to disagree about. What replaces it is the shape table
    // asserted directly.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void TheSingleOverload_AcceptsAndRejectsExactlyTheDocumentedShapes()
    {
        // Every shape the parser is documented to accept, and every shape it is documented to reject,
        // asserted against the one overload that exists - including the two quirks (25 and 73 values)
        // and the "1 15" hole that ASpaceInsideANumber_SplitsItIntoTwoTokens_AndCorruptsTheFrame
        // characterizes. A rejected line must leave a default frame, which is what
        // SensorFrameOverload_OnRejection_FrameIsDefault_NotPartiallyPopulated checks in detail.
        (string Line, bool Accepted, int Zones, bool HasImu)[] cases =
        [
            (LineOfCount(16), true, 16, false),
            (LineOfCount(64), true, 64, false),
            (LineOfCount(25), true, 16, true),
            (LineOfCount(73), true, 64, true),
            ("", false, 0, false),
            (" ", false, 0, false),
            (",,,", false, 0, false),
            ("nonsense", false, 0, false),
            (LineOfCount(17), false, 0, false),
            (LineOfCount(24) + ",x", false, 0, false),
            ("100,101,102,103,104,105,106,107,108,109,110,111,112,113,1 15", true, 16, false),
        ];

        foreach ((string line, bool accepted, int zones, bool hasImu) in cases)
        {
            bool ok = SensorFrameParser.TryParse(line, out SensorFrame frame);

            Assert.Equal(accepted, ok);
            Assert.Equal(zones, frame.ZoneCount);
            Assert.Equal(zones, frame.Distances.Length);
            Assert.Equal(hasImu, frame.HasImu);
            Assert.Equal(hasImu ? 9 : 0, frame.Imu.Length);
        }
    }

    [Fact]
    public void SensorFrameOverload_OnRejection_FrameIsDefault_NotPartiallyPopulated()
    {
        Assert.True(SensorFrameParser.TryParse(LineOfCount(25), out SensorFrame good));

        // 25 tokens but the last is garbage: nothing may survive from the previous frame.
        Assert.False(SensorFrameParser.TryParse(LineOfCount(24) + ",x", out SensorFrame bad));
        Assert.True(bad.Distances.IsEmpty);
        Assert.True(bad.Imu.IsEmpty);
        Assert.False(bad.HasImu);
        Assert.Equal(0, bad.ZoneCount);
        Assert.Equal(default, bad.Timestamp);
    }

    [Fact]
    public void SensorFrameOverload_ZoneCountIsTheZoneCount_NotTheTokenCount()
    {
        // 25 tokens is a 16-zone frame with IMU, not a 25-zone frame. ZoneCount must be 16, and
        // Distances must hold 16 values, or MainWindow's `distances.Length == 64 ? 8 : 4` grid
        // selection would pick a size that does not exist.
        Assert.True(SensorFrameParser.TryParse(LineOfCount(25), out SensorFrame frame));
        Assert.Equal(16, frame.ZoneCount);
        Assert.Equal(16, frame.Distances.Length);
        Assert.Equal(9, frame.Imu.Length);
        Assert.True(frame.HasImu);

        Assert.True(SensorFrameParser.TryParse(LineOfCount(73), out SensorFrame wide));
        Assert.Equal(64, wide.ZoneCount);
        Assert.Equal(64, wide.Distances.Length);
        Assert.Equal(9, wide.Imu.Length);
    }

    [Fact]
    public void SensorFrameOverload_WithoutImu_ImuIsEmptyArray_NotDefaultMemory()
    {
        // HasImu false must still leave Imu safe to read. A default(ReadOnlyMemory<int>) and
        // Array.Empty<int>() both have Length 0, so the distinction matters only for the
        // IsDefault check below - and the point of the test is that consumers never have to know.
        Assert.True(SensorFrameParser.TryParse(LineOfCount(16), out SensorFrame frame));
        Assert.False(frame.HasImu);
        Assert.Equal(0, frame.Imu.Length);
        Assert.True(frame.Imu.IsEmpty);
    }

    [Fact]
    public void SensorFrameOverload_DistancesAndImuRemainSeparateArrays()
    {
        // The same ownership guarantee the array overload pins, restated for the frame model: the
        // two are NOT views over one buffer, so writing through Distances cannot corrupt Imu.
        Assert.True(SensorFrameParser.TryParse(LineOfCount(25), out SensorFrame frame));

        int[] distances = frame.Distances.ToArray();
        int[] imu = frame.Imu.ToArray();
        distances[0] = 999;
        Assert.NotEqual(999, imu[0]);

        imu[0] = 888;
        Assert.NotEqual(888, frame.Distances.Span[0]);
    }

    [Fact]
    public void SensorFrameOverload_TimestampIsStamped_AndCanBeReplacedByWith()
    {
        DateTimeOffset before = DateTimeOffset.UtcNow;
        Assert.True(SensorFrameParser.TryParse(LineOfCount(16), out SensorFrame frame));
        DateTimeOffset after = DateTimeOffset.UtcNow;

        Assert.True(frame.Timestamp >= before && frame.Timestamp <= after,
            $"parse timestamp {frame.Timestamp:O} is outside [{before:O}, {after:O}].");

        // A replay source replaces the timestamp rather than re-parsing. This is the mechanism
        // Phase 4's coalescing stage and Phase 6's replay rely on.
        var replayed = frame with { Timestamp = new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero) };
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero), replayed.Timestamp);
        Assert.Equal(frame.Distances.Length, replayed.Distances.Length);
    }

    [Fact]
    public void SensorFrameEquality_ComparesArrayReferences_NotContents()
    {
        // DOCUMENTS A TRAP, not an approved behaviour. This is a record struct, so the compiler
        // generated value equality - but ReadOnlyMemory<T>.Equals compares the backing array
        // reference and span bounds, not the values. Two frames holding identical numbers backed by
        // different arrays are NOT equal. Nothing in the codebase compares frames today; if a future
        // consumer needs content equality, it must add an explicit Equals override rather than
        // assuming the generated one does it. If this test ever starts PASSING, something changed the
        // frame's backing storage and this comment is now wrong.
        Assert.True(SensorFrameParser.TryParse(LineOfCount(16), out SensorFrame a));
        Assert.True(SensorFrameParser.TryParse(LineOfCount(16), out SensorFrame b));

        Assert.Equal(a.Distances.ToArray(), b.Distances.ToArray());
        Assert.NotEqual(a, b);
    }
}
