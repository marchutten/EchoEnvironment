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

    // -----------------------------------------------------------------------------------------
    // The four legal frame shapes.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void SixteenZones_Parses16Distances_AndNoImu()
    {
        int[] values = Range16();

        bool ok = SensorFrameParser.TryParse(Line(values), out int[] distances, out int[]? imu);

        Assert.True(ok);
        Assert.Equal(values, distances);
        Assert.Null(imu);
    }

    [Fact]
    public void SixtyFourZones_Parses64Distances_AndNoImu()
    {
        int[] values = Enumerable.Range(100, 64).ToArray();

        bool ok = SensorFrameParser.TryParse(Line(values), out int[] distances, out int[]? imu);

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

        bool ok = SensorFrameParser.TryParse(Line(values), out int[] distances, out int[]? imu);

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

        bool ok = SensorFrameParser.TryParse(Line(values), out int[] distances, out int[]? imu);

        Assert.True(ok);
        Assert.Equal(values[..64], distances);
        Assert.NotNull(imu);
        Assert.Equal(9, imu.Length);
    }

    [Fact]
    public void ImuIsAlwaysTheTrailingValues_NotTheLeadingOnes()
    {
        int[] values = Enumerable.Range(100, 25).ToArray();

        Assert.True(SensorFrameParser.TryParse(Line(values), out int[] distances, out int[]? imu));
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

        bool ok = SensorFrameParser.TryParse(line, out int[] distances, out int[]? imu);

        Assert.True(ok);
        Assert.Equal(16, distances.Length);
        Assert.Null(imu);
    }

    [Fact]
    public void MixedSeparators_AreAccepted()
    {
        string line = "100,101;102 103\t104,105;106 107\t108,109;110 111\t112,113;114 115";

        Assert.True(SensorFrameParser.TryParse(line, out int[] distances, out int[]? imu));
        Assert.Equal(Range16(), distances);
        Assert.Null(imu);
    }

    [Fact]
    public void RepeatedSeparators_Collapse_AndDoNotInventZones()
    {
        // 16 real values with doubled and tripled separators. If empty entries were counted, the
        // frame would be rejected as an illegal count instead of accepted.
        string line = "100,,101;;;102   103\t\t104,105,106,107,108,109,110,111,112,113,114,,115";

        Assert.True(SensorFrameParser.TryParse(line, out int[] distances, out int[]? imu));
        Assert.Equal(Range16(), distances);
    }

    [Theory]
    [InlineData("  100,101,102,103,104,105,106,107,108,109,110,111,112,113,114,115  ")]
    [InlineData("100,101,102,103,104,105,106,107,108,109,110,111,112,113,114,115\r\n")]
    [InlineData("100,101,102,103,104,105,106,107,108,109,110,111,112,113,114,115\n")]
    [InlineData("\t100,101,102,103,104,105,106,107,108,109,110,111,112,113,114,115\t")]
    public void LeadingAndTrailingWhitespace_IsTrimmed(string line)
    {
        Assert.True(SensorFrameParser.TryParse(line, out int[] distances, out int[]? imu));
        Assert.Equal(16, distances.Length);
        Assert.Equal(115, distances[15]);
    }

    [Fact]
    public void WhitespaceAroundEachToken_IsTrimmedPerToken()
    {
        string line = " 100 , 101 , 102 , 103 , 104 , 105 , 106 , 107 , 108 , 109 , 110 , 111 , 112 , 113 , 114 , 115 ";

        Assert.True(SensorFrameParser.TryParse(line, out int[] distances, out int[]? imu));
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
        bool ok = SensorFrameParser.TryParse(line, out int[] distances, out int[]? imu);

        Assert.False(ok);
        Assert.Empty(distances);
        Assert.Null(imu);
    }

    [Fact]
    public void NullLine_IsRejectedWithoutThrowing()
    {
        // string.IsNullOrWhiteSpace(null) is true, so this is a rejection rather than a NullReferenceException.
        bool ok = SensorFrameParser.TryParse(null!, out int[] distances, out int[]? imu);

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
        bool ok = SensorFrameParser.TryParse(line, out int[] distances, out int[]? imu);

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

        Assert.True(SensorFrameParser.TryParse(line, out int[] distances, out int[]? imu));
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
        bool ok = SensorFrameParser.TryParse(LineOfCount(count), out int[] distances, out int[]? imu);

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
        Assert.False(SensorFrameParser.TryParse(line, out int[] distances, out int[]? imu));
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

        Assert.True(SensorFrameParser.TryParse(line, out int[] distances, out int[]? imu));
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

        Assert.True(SensorFrameParser.TryParse(line, out int[] distances, out int[]? imu));
        Assert.Equal(-5, distances[0]);
    }

    [Fact]
    public void LeadingPlusSignAndLeadingZeros_AreAccepted()
    {
        string line = "+100,0101,000000102,103,104,105,106,107,108,109,110,111,112,113,114,115";

        Assert.True(SensorFrameParser.TryParse(line, out int[] distances, out int[]? imu));
        Assert.Equal(100, distances[0]);
        Assert.Equal(101, distances[1]);
        Assert.Equal(102, distances[2]);
    }

    [Fact]
    public void ZeroAndIntMax_AreAccepted()
    {
        string line = Line(0, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113, 114, int.MaxValue);

        Assert.True(SensorFrameParser.TryParse(line, out int[] distances, out int[]? imu));
        Assert.Equal(0, distances[0]);
        Assert.Equal(int.MaxValue, distances[15]);
    }

    [Fact]
    public void IntMin_IsAccepted()
    {
        string line = Line(int.MinValue, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113, 114, 115);

        Assert.True(SensorFrameParser.TryParse(line, out int[] distances, out int[]? imu));
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
            Assert.True(SensorFrameParser.TryParse(Line(100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113, 114, 115),
                out int[] distances, out _));
            Assert.Equal(Range16(), distances);

            // A negative value must still parse under cultures with different number formatting.
            Assert.True(SensorFrameParser.TryParse(Line(-5, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113, 114, 115),
                out int[] negative, out _));
            Assert.Equal(-5, negative[0]);

            // A culture that uses space as a group separator must NOT reinterpret "1 000" as 1000:
            // space is a frame separator here, so it yields two zone values (1 and 0). 14 further
            // values make 16 tokens in total.
            Assert.True(SensorFrameParser.TryParse("1 000,101,102,103,104,105,106,107,108,109,110,111,112,113,114",
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
        Assert.True(SensorFrameParser.TryParse(LineOfCount(25), out int[] distances, out int[]? imu));
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
        Assert.True(SensorFrameParser.TryParse(LineOfCount(16), out int[] good, out int[]? goodImu));
        Assert.Equal(16, good.Length);

        // A bad call must reset the out params rather than leaving the previous frame in place.
        Assert.False(SensorFrameParser.TryParse("nonsense", out int[] bad, out int[]? badImu));
        Assert.Empty(bad);
        Assert.Null(badImu);
    }

    [Fact]
    public void AFrameWithImuThatIsRejected_LeavesNoPartialImuBehind()
    {
        // 25 tokens but the last one is garbage: neither distances nor imu may be partially populated.
        Assert.False(SensorFrameParser.TryParse(LineOfCount(24) + ",x", out int[] distances, out int[]? imu));
        Assert.Empty(distances);
        Assert.Null(imu);
    }
}
