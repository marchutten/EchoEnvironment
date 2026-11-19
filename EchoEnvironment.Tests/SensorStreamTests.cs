using System.IO;
using EchoEnvironment.Core;
using EchoEnvironment.Hardware;
using Xunit;

namespace EchoEnvironment.Tests;

/// <summary>
/// Tests for the Phase 3 transport seam (<see cref="ISensorStream"/>) and its implementations.
///
/// WHAT THIS CAN AND CANNOT PROVE. There is no serial port in a test process, so
/// <see cref="SerialSensorStream"/>'s Open/DataReceived path is not exercised here — that half of
/// Phase 3's gate ("app connects to real hardware and behaves identically") is inherently a hardware
/// session. What IS tested is the seam's contract: the members every implementation must honour, the
/// framing and parsing each one runs, and the simulator's IMU channel producing a pose the Phase 1
/// known-pose tests already know the answer for.
///
/// ⚠️ The parity half of the gate is still OPEN: <see cref="ReplaySensorStream"/> is tested against a
/// synthetic log written below, not against a capture from the real device, because the repo has no
/// capture (plan §3c, §4). Passing these tests proves the class replays a log correctly. It does not
/// prove the log matches the device.
/// </summary>
public class SensorStreamTests
{
    private static string FrameLine(int count) =>
        string.Join(",", Enumerable.Range(100, count));

    // -----------------------------------------------------------------------------------------
    // The seam contract, asserted against every implementation rather than against each one
    // separately, so a new implementation cannot quietly omit a member.
    // -----------------------------------------------------------------------------------------

    public static TheoryData<ISensorStream> EveryStream() => new()
    {
        new SerialSensorStream(),
        new SimulatedSensorStream(),
        new ReplaySensorStream("unused.log"),
    };

    [Theory]
    [MemberData(nameof(EveryStream))]
    public void EveryStream_HasNoPortOpenBeforeOpen(ISensorStream stream)
    {
        Assert.False(stream.IsOpen);
        Assert.NotNull(stream.ListPorts());
        stream.Dispose();
        Assert.False(stream.IsOpen);
    }

    [Theory]
    [MemberData(nameof(EveryStream))]
    public void EveryStream_CloseWithoutOpen_IsSafeAndIdempotent(ISensorStream stream)
    {
        stream.Close();
        stream.Close();
        stream.Dispose();
        stream.Dispose();
        Assert.False(stream.IsOpen);
    }

    /// <summary>
    /// Only the two implementations that can be opened without hardware. SerialSensorStream is
    /// deliberately excluded: opening a real port in a test is not a seam test, and on this machine
    /// COM12 is the development sensor — a test that opens it would interfere with a hardware
    /// session. That half of Phase 3's gate belongs to the hardware session, not to the suite.
    /// </summary>
    public static TheoryData<ISensorStream> OpenableStreams() => new()
    {
        new SimulatedSensorStream { IntervalMilliseconds = 1000 },
        new ReplaySensorStream("unused.log"),
    };

    [Theory]
    [MemberData(nameof(OpenableStreams))]
    public void OpenTwice_IsRejected(ISensorStream stream)
    {
        stream.Open("SIM", 115200);
        Assert.True(stream.IsOpen);

        Assert.Throws<InvalidOperationException>(() => stream.Open("SIM", 115200));

        stream.Dispose();
    }

    [Theory]
    [MemberData(nameof(EveryStream))]
    public void EveryStream_OpenAfterDispose_IsRejected(ISensorStream stream)
    {
        stream.Dispose();
        Assert.Throws<ObjectDisposedException>(() => stream.Open("SIM", 115200));
    }

    // -----------------------------------------------------------------------------------------
    // SerialSensorStream: the parts that do not need a port.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void SerialSensorStream_ListPorts_ReturnsDescriptors_AndNeverNull()
    {
        using var stream = new SerialSensorStream();

        IReadOnlyList<PortDescriptor> ports = stream.ListPorts();
        Assert.NotNull(ports);

        // The App's port picker depends on Name being non-empty; a descriptor with a blank name
        // would render as an empty combo-box entry.
        Assert.All(ports, port => Assert.False(string.IsNullOrWhiteSpace(port.Name)));
    }

    [Fact]
    public void SerialSensorStream_OpenOnAnInvalidPortName_Throws_AndLeavesNothingOpen()
    {
        using var stream = new SerialSensorStream();

        // The App catches exactly this class of failure and shows ex.Message. The seam must keep
        // throwing it rather than swallowing it, or Connect_Click's catch block becomes dead code.
        Exception ex = Record.Exception(() => stream.Open("NOT-A-PORT", 115200));
        Assert.NotNull(ex);
        Assert.False(stream.IsOpen);
    }

    // WHAT IS NOT TESTED HERE, and why: SerialSensorStream.Open's success path — the status message
    // it raises, the DataReceived subscription, the framing of real port text — cannot be tested
    // without opening a real port. On this machine COM12 is the development sensor, so a test that
    // opened the first available port would take the device away from a hardware session. That half
    // of Phase 3's gate ("app connects to real hardware and behaves identically") is discharged by
    // the hardware session itself, not by the suite. Pretending otherwise would be a green test
    // standing in for a check that was never performed.

    // -----------------------------------------------------------------------------------------
    // ReplaySensorStream
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void ReplaySensorStream_ReplaysEveryFrameInOrder_AndReportsNoneRejected()
    {
        string path = WriteLog(
            FrameLine(16),
            FrameLine(64),
            FrameLine(25),
            FrameLine(73));

        using var stream = new ReplaySensorStream(path) { IntervalMilliseconds = 0 };
        var frames = new List<SensorFrame>();
        var rejected = new List<string>();
        stream.FrameReceived += (_, frame) => frames.Add(frame);
        stream.LineRejected += (_, line) => rejected.Add(line);

        stream.Open("ignored", 0);
        WaitFor(() => stream.FramesReplayed >= 4, TimeSpan.FromSeconds(10));
        stream.Close();

        Assert.Equal(4, frames.Count);
        Assert.Empty(rejected);
        Assert.Equal(4, stream.FramesReplayed);
        Assert.Equal(0, stream.LinesRejected);

        Assert.Equal(16, frames[0].ZoneCount);
        Assert.False(frames[0].HasImu);
        Assert.Equal(64, frames[1].ZoneCount);
        Assert.False(frames[1].HasImu);
        Assert.Equal(16, frames[2].ZoneCount);
        Assert.True(frames[2].HasImu);
        Assert.Equal(64, frames[3].ZoneCount);
        Assert.True(frames[3].HasImu);

        // Order, not just count: a replay that reorders frames would silently corrupt the map view.
        Assert.Equal(100, frames[0].Distances.Span[0]);
        Assert.Equal(100, frames[1].Distances.Span[0]);
    }

    [Fact]
    public void ReplaySensorStream_RejectsGarbageLines_AndKeepsGoing()
    {
        string path = WriteLog(
            FrameLine(16),
            "this is not a frame",
            FrameLine(16));

        using var stream = new ReplaySensorStream(path) { IntervalMilliseconds = 0 };
        var frames = new List<SensorFrame>();
        var rejected = new List<string>();
        stream.FrameReceived += (_, frame) => frames.Add(frame);
        stream.LineRejected += (_, line) => rejected.Add(line);

        stream.Open("ignored", 0);
        WaitFor(() => stream.FramesReplayed >= 2, TimeSpan.FromSeconds(10));
        stream.Close();

        Assert.Equal(2, frames.Count);
        Assert.Equal(["this is not a frame"], rejected);
    }

    [Fact]
    public void ReplaySensorStream_LastFrameWithoutTrailingNewline_IsNotDropped()
    {
        // The replay-only drain. A log editor that strips the final newline must not cost a frame.
        // SerialSensorStream deliberately has no equivalent: a port has no end to drain at.
        string path = WriteLogRaw(FrameLine(16) + "\n" + FrameLine(16));

        using var stream = new ReplaySensorStream(path) { IntervalMilliseconds = 0 };
        int frames = 0;
        stream.FrameReceived += (_, _) => frames++;

        stream.Open("ignored", 0);
        WaitFor(() => frames >= 2, TimeSpan.FromSeconds(10));
        stream.Close();

        Assert.Equal(2, frames);
    }

    [Fact]
    public void ReplaySensorStream_CrlfLog_ReplaysTheSameFramesASingleNewlineLog()
    {
        // LineFramer turns "\r\n" into a line plus an empty line. The empty line is filtered as
        // framing noise, so a CRLF capture and an LF capture must produce identical frames. This is
        // what makes a Windows-captured log usable for replay.
        string lf = WriteLog(FrameLine(16), FrameLine(64));
        string crlf = WriteLogRaw(FrameLine(16) + "\r\n" + FrameLine(64) + "\r\n");

        var lfFrames = ReplayToFrames(lf);
        var crlfFrames = ReplayToFrames(crlf);

        Assert.Equal(lfFrames.Count, crlfFrames.Count);
        for (int i = 0; i < lfFrames.Count; i++)
        {
            Assert.Equal(lfFrames[i].Distances.ToArray(), crlfFrames[i].Distances.ToArray());
        }
    }

    [Fact]
    public void ReplaySensorStream_MissingFile_RaisesFaultedAndNotAnUnobservedTaskException()
    {
        using var stream = new ReplaySensorStream(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".log"));
        var faults = new List<Exception>();
        stream.Faulted += (_, ex) => faults.Add(ex);

        stream.Open("ignored", 0);
        WaitFor(() => faults.Count > 0, TimeSpan.FromSeconds(10));
        stream.Close();

        Assert.NotEmpty(faults);
        Assert.All(faults, fault => Assert.IsAssignableFrom<IOException>(fault));
    }

    [Fact]
    public void ReplaySensorStream_EmptyPath_IsRejectedAtConstruction()
    {
        Assert.Throws<ArgumentException>(() => new ReplaySensorStream("   "));
    }

    // -----------------------------------------------------------------------------------------
    // SimulatedSensorStream — the offline-development half of the seam.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void SimulatedSensorStream_ProducesFramesWithTheConfiguredZoneCount()
    {
        using var stream = new SimulatedSensorStream { ZoneCount = 64, IntervalMilliseconds = 5 };
        var frames = new List<SensorFrame>();
        stream.FrameReceived += (_, frame) => frames.Add(frame);

        stream.Open("SIM", 0);
        WaitFor(() => frames.Count >= 5, TimeSpan.FromSeconds(10));
        stream.Close();

        Assert.NotEmpty(frames);
        Assert.All(frames, frame =>
        {
            Assert.Equal(64, frame.ZoneCount);
            Assert.Equal(64, frame.Distances.Length);
            Assert.True(frame.HasImu);
            Assert.Equal(9, frame.Imu.Length);
        });
    }

    [Fact]
    public void SimulatedSensorStream_EveryZoneStaysInsideTheVisibleBand()
    {
        // The App hides a zone outside 20..4000 mm. A simulator whose wave left that band would draw
        // nothing and look broken while being technically correct.
        using var stream = new SimulatedSensorStream { ZoneCount = 16, IntervalMilliseconds = 5 };
        var frames = new List<SensorFrame>();
        stream.FrameReceived += (_, frame) => frames.Add(frame);

        stream.Open("SIM", 0);
        WaitFor(() => frames.Count >= 5, TimeSpan.FromSeconds(10));
        stream.Close();

        Assert.NotEmpty(frames);
        Assert.All(frames, frame => Assert.All(frame.Distances.ToArray(), mm => Assert.InRange(mm, 20, 4000)));
    }

    [Fact]
    public void SimulatedSensorStream_InvalidZoneCount_RaisesFaultedInsteadOfProducingABrokenFrame()
    {
        using var stream = new SimulatedSensorStream { ZoneCount = 25, IntervalMilliseconds = 5 };
        var faults = new List<Exception>();
        var frames = new List<SensorFrame>();
        stream.Faulted += (_, ex) => faults.Add(ex);
        stream.FrameReceived += (_, frame) => frames.Add(frame);

        stream.Open("SIM", 0);
        WaitFor(() => faults.Count > 0, TimeSpan.FromSeconds(10));
        stream.Close();

        Assert.NotEmpty(faults);
        Assert.Empty(frames);
    }

    /// <summary>
    /// The claim that makes SimulatedSensorStream worth building: its IMU channel drives
    /// OrientationFilter to a pose the Phase 1 known-pose tests already pin, so the app can be
    /// developed and demoed with no sensor and the orientation shown is a pose the suite has already
    /// verified numerically.
    /// </summary>
    [Theory]
    [InlineData(SimulationPose.FlatFacingNorth, 1, 0, 0, 0, 1, 0, 0, 0, 1)]
    [InlineData(SimulationPose.FlatFacingEast, 0, 0, 1, 0, 1, 0, -1, 0, 0)]
    [InlineData(SimulationPose.InvertedFacingNorth, -1, 0, 0, 0, -1, 0, 0, 0, 1)]
    public void SimulatedSensorStream_ImuChannelDrivesTheFilterToTheDeclaredPose(
        SimulationPose pose,
        double m11, double m12, double m13,
        double m21, double m22, double m23,
        double m31, double m32, double m33)
    {
        using var stream = new SimulatedSensorStream
        {
            Pose = pose,
            ZoneCount = 16,
            IntervalMilliseconds = 2,
        };

        var filter = new OrientationFilter();
        var expected = new Mat3(m11, m12, m13, m21, m22, m23, m31, m32, m33);

        int frames = 0;
        Mat3 last = default;
        stream.FrameReceived += (_, frame) =>
        {
            // dt = 0 keeps the filter on its measured-pose path: the first Update seeds the axes and
            // returns, so the pose the simulator declares must be the pose the filter reports, with
            // no correction blending to explain a mismatch away.
            last = filter.Update(frame.Imu.ToArray(), 0);
            frames++;
        };

        stream.Open("SIM", 0);
        WaitFor(() => frames >= 1, TimeSpan.FromSeconds(10));
        stream.Close();

        Assert.True(frames >= 1, "simulator produced no frames");
        AssertMath.Matrix(expected, last, AssertMath.Geometry, $"{pose} pose");
        AssertMath.OrthonormalRows(last, pose.ToString());
    }

    [Fact]
    public void SimulatedSensorStream_ListPortsOffersOneNamedPortForThePicker()
    {
        using var stream = new SimulatedSensorStream();

        IReadOnlyList<PortDescriptor> ports = stream.ListPorts();
        Assert.Single(ports);
        Assert.Equal("SIM", ports[0].Name);
    }

    // -----------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------

    private static List<SensorFrame> ReplayToFrames(string path)
    {
        using var stream = new ReplaySensorStream(path) { IntervalMilliseconds = 0 };
        var frames = new List<SensorFrame>();
        stream.FrameReceived += (_, frame) => frames.Add(frame);

        stream.Open("ignored", 0);
        WaitFor(() => stream.FramesReplayed >= 2, TimeSpan.FromSeconds(10));
        stream.Close();

        return frames;
    }

    /// <summary>Writes lines each terminated by a single '\n'.</summary>
    private static string WriteLog(params string[] lines) =>
        WriteLogRaw(string.Join("\n", lines) + "\n");

    /// <summary>Writes text exactly as given, so terminator behaviour can be controlled.</summary>
    private static string WriteLogRaw(string text)
    {
        string path = Path.Combine(Path.GetTempPath(), "echo-replay-" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(path, text);
        return path;
    }

    private static void WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            Thread.Sleep(10);
        }
    }
}
