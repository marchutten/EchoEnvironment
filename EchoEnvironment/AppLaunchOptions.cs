using System.IO;
using EchoEnvironment.Hardware;

namespace EchoEnvironment;

/// <summary>
/// Which transport the app was told to use. Decided once, at startup, by <see cref="AppLaunchOptions"/>.
/// </summary>
public enum SensorSourceKind
{
    /// <summary>A real serial port, chosen in the toolbar. The default, and the only source with a port and a baud rate.</summary>
    Serial,

    /// <summary><see cref="SimulatedSensorStream"/>: synthetic frames, no hardware.</summary>
    Simulated,

    /// <summary><see cref="ReplaySensorStream"/>: a captured log replayed through the production framing and parsing.</summary>
    Replay,
}

/// <summary>
/// The app's chosen sensor source, as a factory rather than as an instance.
///
/// WHY A FACTORY AND NOT THE STREAM ITSELF. MainWindow's connection lifecycle is create-on-connect,
/// dispose-on-disconnect: Connect_Click builds the stream and Disconnect calls Dispose on it. A single
/// injected instance would be disposed by the first Disconnect, so the second Connect would meet an
/// ObjectDisposedException. Handing MainWindow a factory keeps that lifecycle exactly as it is today -
/// each connection gets a live stream and disposes it - while still letting the composition root decide
/// WHICH stream. For a replay that means a second Connect replays the log again from the start, which is
/// what a viewer would expect from a button labelled Connect.
///
/// WHY THE KIND IS CARRIED AND NOT JUST INFERRED FROM THE STREAM. The toolbar's Capture checkbox only
/// means something for a live serial session: a replay source has nothing to capture, and a simulator
/// generates its frames rather than receiving text. MainWindow needs to say that before a stream exists,
/// so it can disable the control instead of accepting a request it cannot honour.
/// </summary>
public sealed record SensorSource(SensorSourceKind Kind, string? ReplayPath)
{
    public static SensorSource Serial() => new(SensorSourceKind.Serial, null);

    public static SensorSource Simulated() => new(SensorSourceKind.Simulated, null);

    public static SensorSource Replay(string replayPath) => new(SensorSourceKind.Replay, replayPath);

    /// <summary>Raw capture is a property of the serial transport, so it is available for exactly one source.</summary>
    public bool CaptureSupported => Kind == SensorSourceKind.Serial;

    /// <summary>A fresh stream for one connection. The caller owns it and must dispose it.</summary>
    public ISensorStream CreateStream() => Kind switch
    {
        SensorSourceKind.Serial => new SerialSensorStream(),
        SensorSourceKind.Simulated => new SimulatedSensorStream(),
        SensorSourceKind.Replay => new ReplaySensorStream(ReplayPath!),
        _ => throw new InvalidOperationException($"Unknown sensor source: {Kind}."),
    };
}

/// <summary>
/// The composition root's argument parser (MIGRATION_PLAN 6c).
///
/// WHY THIS IS ITS OWN TYPE. §5 lists "App runs with --sim and --replay &lt;file&gt; - no hardware
/// required" as only half satisfied: SimulatedSensorStream and ReplaySensorStream have existed since
/// Phase 3, but nothing in the App could select either one, so the offline workflow the whole Phase 3
/// extraction was for could not actually be used. The blocker was never the streams - it was a decision
/// with no home. Putting that decision in a pure function over string[] makes it the easiest thing in
/// the App to test, and it is what lets 6c land without the 6b view-model refactor it was originally
/// written alongside.
///
/// WHY THE PARSER REJECTS RATHER THAN IGNORES. An unrecognised argument today would be silently ignored
/// and the app would open a serial port while the user believed they were replaying a log. A wrong
/// sensor source is not a cosmetic mistake: a replay session produces no capture file and a serial
/// session needs hardware that may not be attached. Rejecting at startup, with the usage line, is the
/// cheaper failure.
/// </summary>
public static class AppLaunchOptions
{
    /// <summary>Printed on every rejection, because every rejection is a mistyped command line.</summary>
    public const string Usage =
        "Usage: EchoEnvironment [--sim] [--replay <file>]   (no arguments: connect to a serial port)";

    /// <summary>
    /// Turns the command line into one <see cref="SensorSource"/>, or into a message that says what was
    /// wrong with it. On failure, <c>source</c> is null and <c>error</c> is non-null; on success the
    /// reverse. The out-params are always assigned rather than left stale, the same discipline
    /// SensorFrameParser pins for its own parser.
    /// </summary>
    public static bool TryParse(string[]? args, out SensorSource? source, out string? error)
    {
        source = null;
        error = null;

        SensorSourceKind? chosen = null;
        string? replayPath = null;

        // A null array is what a caller with no arguments at all produces; treating it as "no arguments"
        // rather than as a fault keeps the default path - the serial one - the least surprising.
        string[] commandLine = args ?? [];

        for (int i = 0; i < commandLine.Length; i++)
        {
            string argument = commandLine[i];

            switch (argument.ToLowerInvariant())
            {
                case "--sim":
                    if (chosen is not null)
                    {
                        error = $"Only one sensor source can be chosen; --sim conflicts with {KindName(chosen.Value)}. {Usage}";
                        return false;
                    }

                    chosen = SensorSourceKind.Simulated;
                    break;

                case "--replay":
                    if (chosen is not null)
                    {
                        // .Value rather than a null-forgiving `!`: `chosen` is a nullable VALUE type, and
                        // the compiler will not narrow it through `is not null` inside an interpolated
                        // string. .Value states the same fact the enclosing if already proved, and unlike
                        // `!` it would throw rather than pass a null if that proof ever stopped holding.
                        error = $"Only one sensor source can be chosen; --replay conflicts with {KindName(chosen.Value)}. {Usage}";
                        return false;
                    }

                    if (i + 1 >= commandLine.Length)
                    {
                        error = $"--replay needs a captured log path. {Usage}";
                        return false;
                    }

                    replayPath = commandLine[++i];

                    if (string.IsNullOrWhiteSpace(replayPath))
                    {
                        error = $"--replay needs a captured log path, not an empty one. {Usage}";
                        return false;
                    }

                    // Checked here rather than left to the replay pump. ReplaySensorStream reports an
                    // unreadable file through Faulted, which lands in the status line after the window is
                    // already open and after the user has pressed Connect; a mistyped path is knowable at
                    // startup, and knowing it at startup costs the user no session.
                    if (!File.Exists(replayPath))
                    {
                        error = $"--replay: no such file: {replayPath}";
                        return false;
                    }

                    chosen = SensorSourceKind.Replay;
                    break;

                default:
                    error = $"Unrecognised argument: {argument}. {Usage}";
                    return false;
            }
        }

        source = chosen switch
        {
            SensorSourceKind.Simulated => SensorSource.Simulated(),
            SensorSourceKind.Replay => SensorSource.Replay(replayPath!),
            _ => SensorSource.Serial(),
        };

        return true;
    }

    private static string KindName(SensorSourceKind kind) => kind switch
    {
        SensorSourceKind.Simulated => "--sim",
        SensorSourceKind.Replay => "--replay",
        _ => "the serial source",
    };
}
