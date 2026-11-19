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
/// value type. The original out int[] overload is KEPT, as Phase 2d requires, so the App and the
/// existing parser tests compile unchanged during the transition. Delete it in Phase 5.
/// </summary>
public static class SensorFrameParser
{
    private static readonly char[] Separators = [',', ';', ' ', '\t'];

    /// <summary>
    /// Original signature, kept for one phase and deleted in Phase 5. Behaviour is identical to the
    /// pre-Phase-3 implementation, including the out-param reset on rejection pinned by
    /// OnRejection_OutParamsAreReset_NotLeftStaleFromThePreviousFrame.
    /// </summary>
    public static bool TryParse(string line, out int[] distances, out int[]? imu)
        => TryParseValues(line, out distances, out imu);

    /// <summary>
    /// Parses a line into a SensorFrame. The timestamp is stamped at parse time; a replay source
    /// replaces it with a `with` expression rather than going through the parser.
    /// On failure, frame is default: Distances and Imu are empty and HasImu is false.
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
    /// The parsing body, shared by both public overloads so the hot serial path never pays for a
    /// second copy of a frame. Distances and Imu are separate arrays, not slices of one buffer —
    /// pinned by DistancesAndImuAreSeparateArrays_NotViewsOverOneBuffer.
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
