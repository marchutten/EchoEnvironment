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
/// </summary>
public static class SensorFrameParser
{
    private static readonly char[] Separators = [',', ';', ' ', '\t'];

    public static bool TryParse(string line, out int[] distances, out int[]? imu)
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
