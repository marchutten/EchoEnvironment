using System.Globalization;

namespace EchoEnvironment.Core;

/// <summary>
/// Parses a line of separated distance values (mm) into a frame. Accepts 16 (4x4) or 64 (8x8) values.
///
/// PHASE 2 NOTE: moved verbatim from the WPF App project. The logic is byte-for-byte identical,
/// including the quirks that the characterization tests pin as deliberate rather than as bugs:
///   - 25 and 73 values are LEGAL inputs meaning "16/64 zones + 9 IMU values".
///   - negative millimetres are accepted.
///   - space is a separator, so "1 15" silently becomes two zones rather than one malformed value.
/// Changing any of those is a behaviour change to be negotiated with the firmware, not a refactor.
///
/// PHASE 3 NOTE (SensorFrame added here per 9.7, deferred from Phase 2 step 2d): the frame-returning
/// overload was added because ISensorStream.FrameReceived is the first consumer that needs a single
/// value type. The original out int[] overload was KEPT for that transition, as Phase 2d required, and
/// was deleted in Phase 5 (§12.8 item 4) once nothing in the app still called it.
/// </summary>
public static class SensorFrameParser
{
    private static readonly char[] Separators = [',', ';', ' ', '\t'];

    /// <summary>
    /// Parses a line into a SensorFrame. The timestamp is stamped at parse time; a replay source
    /// replaces it with a `with` expression rather than going through the parser.
    /// On failure, frame is default: Distances and Imu are empty and HasImu is false.
    ///
    /// THIS IS NOW THE ONLY TryParse SIGNATURE. The original `out int[], out int[]?` overload that
    /// Phase 2 step 2d required to be kept during the transition was deleted here, in Phase 5, exactly
    /// as its own comment promised (§12.8 item 4). It had zero production call sites —
    /// SerialSensorStream.Port_DataReceived and ReplaySensorStream.Deliver both already used this
    /// overload, and MainWindow never names the parser at all — against 28 call sites in
    /// SensorFrameParserTests, which now go through a private test-side adapter rather than through a
    /// production signature that nothing in the app called.
    /// </summary>
    public static bool TryParse(string line, out SensorFrame frame)
    {
        frame = default;
        if (!TryParseValues(line, out int[] distances, out int[]? imu))
        {
            return false;
        }

        frame = new SensorFrame(
            distances,
            distances.Length,
            imu ?? Array.Empty<int>(),
            imu is not null,
            DateTimeOffset.UtcNow);
        return true;
    }

    /// <summary>
    /// The parsing body. It used to be shared by two public overloads so the hot serial path never paid
    /// for a second copy of a frame; the array overload was deleted in Phase 5 (§12.8 item 4), so this is
    /// now the single body behind the single signature. Distances and Imu are separate arrays, not slices
    /// of one buffer — pinned by DistancesAndImuAreSeparateArrays_NotViewsOverOneBuffer.
    /// </summary>
    private static bool TryParseValues(string line, out int[] distances, out int[]? imu)
    {
        distances = [];
        imu = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        string[] parts = line.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int zoneCount = parts.Length switch
        {
            16 or 25 => 16,
            64 or 73 => 64,
            _ => 0
        };
        if (zoneCount == 0)
        {
            return false;
        }

        int[] values = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i]))
            {
                return false;
            }
        }

        distances = values[..zoneCount];
        if (parts.Length > zoneCount)
        {
            // Order: ITG3200 gyro x y z, BMA180 accel x y z, HMC5883L mag x y z.
            imu = values[zoneCount..];
        }

        return true;
    }
}
