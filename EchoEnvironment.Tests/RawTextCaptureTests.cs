using System.IO;
using EchoEnvironment.Core;
using EchoEnvironment.Hardware;
using Xunit;

namespace EchoEnvironment.Tests;

/// <summary>
/// Tests for the raw capture (RawTextCapture) and the transport's half of it.
///
/// WHY THIS EXISTS. MIGRATION_PLAN 3c and 4 have carried "ReplaySensorStream needs a real captured log,
/// and the repo has none" since Phase 3, through six sections. Verified at cb3fbf2, that was not an
/// undone task but an impossible one: the solution contained no writer of any kind, so even a live
/// hardware session could not save what it received. These tests cover the code that makes it possible.
///
/// WHAT THESE CAN AND CANNOT PROVE. The end-to-end parity claim is "the frames the app drew from the
/// device are the frames the same bytes produce on replay." The last mile needs the device, and a test
/// process has no port — the same limitation SensorStreamTests records. What IS proven here is the link
/// that was missing: bytes arrive, land in a file unchanged, and produce the same frames when replayed
/// as they produced when framed live. That is the half that was structurally impossible, and it is the
/// half a capture can fail.
/// </summary>
public class RawTextCaptureTests
{
    private static string FrameLine(int count) =>
        string.Join(",", Enumerable.Range(100, count));

    // -----------------------------------------------------------------------------------------
    // The capture writes text, not lines. Everything else follows from that.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void RawTextCapture_WritesExactlyTheCharactersGiven_AndAddsNothing()
    {
        string path = CapturePath();
        using (var capture = new RawTextCapture(path))
        {
            Assert.True(capture.Write("100,200,300"));
            Assert.True(capture.Write("400,500"));

            // 11 + 7. The first version of this assertion said 14, and the failure was the test being
            // wrong rather than the capture: CharactersWritten reported 18, which is the character count
            // of the two chunks and of the file the assertion below reads back.
            Assert.Equal(18, capture.CharactersWritten);
        }

        // "100,200,300400,500" is deliberately not a valid frame. A capture that inserted a newline
        // between chunks would look tidier and would be a lie: the port did not send one.
        Assert.Equal("100,200,300400,500", File.ReadAllText(path));
    }

    [Fact]
    public void RawTextCapture_PreservesCarriageReturnsAsReceived()
    {
        string path = CapturePath();
        using (var capture = new RawTextCapture(path))
        {
            capture.Write(FrameLine(16) + "\r\n");
            capture.Write(FrameLine(16) + "\r\n");
        }

        // LineFramer treats '\r' and '\n' as two terminators, so a CRLF capture replays as a line plus
        // an empty line. Normalising terminators on the way to disk would replay a stream the port never
        // produced - the opposite of what a capture is for. The bytes must be the bytes.
        string text = File.ReadAllText(path);
        Assert.Equal(2, text.Split("\r\n").Length - 1);
        Assert.DoesNotContain("\n\n", text);
    }

    [Fact]
    public void RawTextCapture_KeepsTheEmptyLineNoiseACrlfCaptureProduces()
    {
        string path = CapturePath();
        using (var capture = new RawTextCapture(path))
        {
            capture.Write(FrameLine(16) + "\r\n");
        }

        var framer = new LineFramer();
        framer.Append(File.ReadAllText(path));
        Assert.True(framer.TryReadLine(out string first));
        Assert.Equal(FrameLine(16), first);
        Assert.True(framer.TryReadLine(out string second));
        Assert.Equal(string.Empty, second);
    }

    [Fact]
    public void RawTextCapture_EmptyChunk_IsAccepted_AndCountedAsNothing()
    {
        string path = CapturePath();
        using var capture = new RawTextCapture(path);

        Assert.True(capture.Write(string.Empty));
        Assert.Equal(0, capture.CharactersWritten);
        Assert.False(capture.Failed);
    }

    [Fact]
    public void RawTextCapture_CreatesTheDirectoryItNeeds()
    {
        // A capture path with a folder that does not exist yet is the normal case for this class, not a
        // failure. DefaultCapturePath produces exactly that on a machine that has never captured.
        // Two levels of folder that do not exist yet. This is the normal case for DefaultCapturePath on a
        // machine that has never captured, not a failure.
        string path = Path.Combine(
            Path.GetTempPath(),
            "echo-capture-" + Guid.NewGuid().ToString("N"),
            "nested",
            "deeper",
            "capture.txt");
        using (var capture = new RawTextCapture(path))
        {
            capture.Write(FrameLine(16));
        }

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void RawTextCapture_SecondCaptureOnTheSamePath_AppendsInsteadOfOverwriting()
    {
        string path = CapturePath();
        using (var first = new RawTextCapture(path))
        {
            first.Write(FrameLine(16) + "\n");
        }

        using (var second = new RawTextCapture(path))
        {
            second.Write(FrameLine(16) + "\n");
        }

        // Reconnecting to the same device and capturing to the same name must not silently discard the
        // earlier session. Whether that is desirable is a separate question from whether it is lossy.
        string[] lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.Equal(lines[0], lines[1]);
    }

    [Fact]
    public void RawTextCapture_InvalidPath_IsRejectedAtConstruction()
    {
        Assert.Throws<ArgumentException>(() => new RawTextCapture("   "));
    }

    [Fact]
    public void RawTextCapture_ExistingDirectoryAsAFilePath_IsRejectedAtConstruction()
    {
        // Path.GetTempPath() is a directory that certainly exists, which is what makes this a real probe.
        // The first version of this test pointed at the tests' own scratch folder, which had not been
        // created yet, so the constructor simply made a FILE there and the test asserted that a rejection
        // had occurred when nothing had been rejected at all - a green-looking test that had measured
        // nothing, and which also poisoned the folder name for every other test in the class.
        //
        // Failing at construction, at the caller's request, is what lets the app report "capture failed"
        // and keep the connection: the alternative is a failure surfacing later, inside the serial receive
        // handler, mid-session.
        Exception ex = Record.Exception(() => new RawTextCapture(Path.GetTempPath()));
        Assert.NotNull(ex);
        Assert.IsAssignableFrom<IOException>(ex);
    }

    [Fact]
    public void RawTextCapture_WriteAfterDispose_ReturnsFalse_AndNeverThrows()
    {
        string path = CapturePath();
        var capture = new RawTextCapture(path);
        capture.Write(FrameLine(16));
        capture.Dispose();

        // The closed writer would throw ObjectDisposedException. A diagnostic that can throw inside the
        // receive handler is worse than no diagnostic, so the class absorbs it and reports false.
        Assert.False(capture.Write(FrameLine(16)));
    }

    [Fact]
    public void RawTextCapture_DisposeIsIdempotent_AndReleasesTheFile()
    {
        string path = CapturePath();
        var capture = new RawTextCapture(path);
        capture.Write(FrameLine(16));
        capture.Dispose();
        capture.Dispose();
        capture.Dispose();

        // Exclusive open succeeding is the proof the handle is gone. A capture still open after
        // Disconnect would block the very replay a hardware session is about to run.
        using (var probe = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.True(probe.Length > 0);
        }
    }

    [Fact]
    public void RawTextCapture_AutoFlushMeansACrashedSessionStillLeavesAUsableLog()
    {
        // No Dispose at all. This is the scenario a capture exists for: the app died mid-session and
        // the log is the only evidence. Without AutoFlush the text would still be in a buffer.
        string path = CapturePath();
        var capture = new RawTextCapture(path);
        capture.Write(FrameLine(64) + "\n");

        using (var probe = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            using var reader = new StreamReader(probe);
            Assert.Equal(FrameLine(64), reader.ReadLine());
        }
    }

    // -----------------------------------------------------------------------------------------
    // The transport half: the capture is opt-in, and with no capture started the transport must
    // behave exactly as it did before this commit.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void SerialSensorStream_WithoutACapture_ReportsNothingCaptured()
    {
        using var stream = new SerialSensorStream();

        Assert.Null(stream.CaptureFilePath);
        Assert.Equal(0, stream.CaptureCharacters);
        Assert.Null(stream.CaptureFailure);
    }

    [Fact]
    public void SerialSensorStream_StartCaptureOnAnUnusablePath_Throws_AndLeavesNoCaptureActive()
    {
        using var stream = new SerialSensorStream();

        Exception ex = Record.Exception(() => stream.StartCapture(Path.GetTempPath()));
        Assert.NotNull(ex);
        Assert.Null(stream.CaptureFilePath);
        Assert.Equal(0, stream.CaptureCharacters);
    }

    [Fact]
    public void SerialSensorStream_StopCaptureWithoutACapture_IsSafeAndIdempotent()
    {
        using var stream = new SerialSensorStream();

        stream.StopCapture();
        stream.StopCapture();
        stream.Close();
        stream.Close();

        Assert.Null(stream.CaptureFilePath);
    }

    [Fact]
    public void SerialSensorStream_DisposeClosesAnActiveCapture()
    {
        string path = CapturePath();
        var stream = new SerialSensorStream();
        stream.StartCapture(path);
        Assert.Equal(path, stream.CaptureFilePath);

        // Close() is reached through Dispose, and MainWindow's Disconnect goes through Dispose too, so
        // this is the path a real session actually takes on the way out.
        stream.Dispose();

        Assert.Null(stream.CaptureFilePath);
        using (var probe = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(0, probe.Length);
        }
    }

    [Fact]
    public void SerialSensorStream_StartCaptureTwice_ClosesTheFirstFile_AndTracksTheSecond()
    {
        string first = CapturePath();
        string second = CapturePath();
        using var stream = new SerialSensorStream();

        stream.StartCapture(first);
        stream.StartCapture(second);

        Assert.Equal(second, stream.CaptureFilePath);

        // Switching targets must not leave the first file open by accident. Exclusive open is the test.
        using (var probe = File.Open(first, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(0, probe.Length);
        }
    }

    [Fact]
    public void SerialSensorStream_StartCaptureAfterDispose_IsRejected()
    {
        var stream = new SerialSensorStream();
        stream.Dispose();

        Assert.Throws<ObjectDisposedException>(() => stream.StartCapture(CapturePath()));
    }

    [Fact]
    public void DefaultCapturePath_NamesTheConditionsOfTheCapture()
    {
        string path = SerialSensorStream.DefaultCapturePath("COM12", 115200);

        Assert.Contains("COM12", path);
        Assert.Contains("115200", path);
        Assert.EndsWith(".txt", path, StringComparison.Ordinal);
        Assert.Contains("captures", Path.GetDirectoryName(path) ?? string.Empty);

        // A log whose baud rate is unknown cannot be compared against a run at a different one, so the
        // conditions travel in the name rather than in someone's memory of the session.
        Assert.Contains(DateTime.Now.ToString("yyyyMMdd"), path);
    }

    [Fact]
    public void DefaultCapturePath_SanitisesCharactersThatAreNotValidInAFileName()
    {
        string path = SerialSensorStream.DefaultCapturePath("COM 12/USB", 9600);
        string file = Path.GetFileName(path);

        Assert.DoesNotContain("/", file);
        Assert.DoesNotContain(" ", file);
        Assert.Contains("COM-12-USB", file);
    }

    [Fact]
    public void DefaultCapturePath_RejectsAnEmptyPortName()
    {
        Assert.Throws<ArgumentException>(() => SerialSensorStream.DefaultCapturePath("  ", 115200));
    }

    // -----------------------------------------------------------------------------------------
    // THE POINT OF THE COMMIT. The capture is only worth having if the file it writes reproduces the
    // stream the port delivered. This is the parity check MIGRATION_PLAN 3c and 4 wanted, minus the
    // device: bytes arrive in port-shaped chunks, land in a file, and replay to the same frames.
    // -----------------------------------------------------------------------------------------

    [Theory]
    // A whole frame in one chunk, a frame split across chunks, CRLF terminators, and garbage between
    // frames - the shapes a real port actually delivers. "noTrailingNewline" is deliberately absent from
    // this list: it is the one shape where capture and replay legitimately disagree, and it is pinned by
    // CapturedText_TrailingFrameWithoutNewline_ExistsOnReplayButNeverReachedTheLiveApp instead.
    [InlineData("whole")]
    [InlineData("split")]
    [InlineData("crlf")]
    [InlineData("garbageInMiddle")]
    public void CapturedText_ReplaysToTheSameFramesTheLiveFramerProduced(string shape)
    {
        string[] chunks = PortShapedChunks(shape);

        // What the app would have drawn: the same framer and parser SerialSensorStream runs, fed the
        // same chunks in the same order.
        var live = FrameThroughLivePath(chunks);

        // What the capture writes: those chunks, concatenated, unchanged.
        string path = CapturePath();
        using (var capture = new RawTextCapture(path))
        {
            foreach (string chunk in chunks)
            {
                capture.Write(chunk);
            }
        }

        Assert.Equal(string.Concat(chunks), File.ReadAllText(path));

        // What a later session would draw from the file.
        var replayed = ReplayToFrames(path);

        Assert.Equal(live.Frames.Count, replayed.Frames.Count);
        for (int i = 0; i < live.Frames.Count; i++)
        {
            Assert.Equal(live.Frames[i].ZoneCount, replayed.Frames[i].ZoneCount);
            Assert.Equal(live.Frames[i].Distances.ToArray(), replayed.Frames[i].Distances.ToArray());
            Assert.Equal(live.Frames[i].HasImu, replayed.Frames[i].HasImu);
        }

        // Rejected lines must match too, or a capture that silently dropped a malformed line would look
        // clean while hiding exactly the framing difference a parity check exists to find.
        Assert.Equal(live.Rejected, replayed.Rejected);
    }

    [Fact]
    public void CapturedText_RoundTripsAFrameCountTheAppWouldHaveDrawn()
    {
        // 64 zones plus 9 IMU values - the shape the device in MIGRATION_PLAN 12.10 actually sends.
        string line = string.Join(",", Enumerable.Range(100, 64)) + ",11,42,-11,-126,-225,4438,-20,-30,-40";
        string path = CapturePath();

        using (var capture = new RawTextCapture(path))
        {
            capture.Write(line + "\r\n");
        }

        var frames = ReplayToFrames(path).Frames;
        Assert.Single(frames);
        Assert.Equal(64, frames[0].ZoneCount);
        Assert.True(frames[0].HasImu);
        Assert.Equal(9, frames[0].Imu.Length);
        Assert.Equal(4438, frames[0].Imu.ToArray()[5]);
    }

    [Fact]
    public void CapturedText_TrailingFrameWithoutNewline_ExistsOnReplayButNeverReachedTheLiveApp()
    {
        // The one shape where a capture and a live session legitimately disagree, and the reason is
        // documented on LineFramer: a serial port has no end, so SerialSensorStream never drains the
        // unterminated remainder. A log file does have an end, so ReplaySensorStream drains it and
        // delivers a frame the app never saw.
        //
        // Not a bug to fix in a capture commit - a property of the parity check itself. A capture
        // compared against a replay must account for a final frame only the replay produces, or every
        // session interrupted mid-frame reports a phantom difference at exactly the moment a capture is
        // most likely to be read.
        string[] chunks = PortShapedChunks("noTrailingNewline");
        var live = FrameThroughLivePath(chunks);

        string path = CapturePath();
        using (var capture = new RawTextCapture(path))
        {
            foreach (string chunk in chunks)
            {
                capture.Write(chunk);
            }
        }

        var replayed = ReplayToFrames(path);

        Assert.Single(live.Frames);
        Assert.Equal(2, replayed.Frames.Count);
        Assert.Equal(live.Frames[0].Distances.ToArray(), replayed.Frames[0].Distances.ToArray());
    }

    // -----------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Chunk boundaries a serial port really produces. Feeding one whole line at a time would test the
    /// happy path only; a frame split across a read is where framing bugs live.
    /// </summary>
    private static string[] PortShapedChunks(string shape)
    {
        string a = FrameLine(64);
        string b = FrameLine(16);

        return shape switch
        {
            "whole" => [a + "\n", b + "\n"],
            "split" => [a[..30], a[30..] + "\n" + b + "\n"],
            "crlf" => [a + "\r\n", b + "\r\n"],
            "noTrailingNewline" => [a + "\n", b],
            "garbageInMiddle" => [a + "\n", "not a frame at all\n", b + "\n"],
            _ => throw new ArgumentException($"unknown capture shape: {shape}", nameof(shape)),
        };
    }

    /// <summary>
    /// Reproduces SerialSensorStream.Port_DataReceived's framing and parsing over the given chunks,
    /// including the TrimOverflow position and the blank-line filter. Copied deliberately: the point of
    /// the comparison is that the capture path and this path agree, and a shared helper would hide a
    /// divergence instead of reporting it.
    /// </summary>
    private static (List<SensorFrame> Frames, List<string> Rejected) FrameThroughLivePath(string[] chunks)
    {
        var framer = new LineFramer();
        var frames = new List<SensorFrame>();
        var rejected = new List<string>();

        foreach (string chunk in chunks)
        {
            framer.Append(chunk);

            while (framer.TryReadLine(out string line))
            {
                if (SensorFrameParser.TryParse(line, out SensorFrame frame))
                {
                    frames.Add(frame);
                }
                else if (!string.IsNullOrWhiteSpace(line))
                {
                    rejected.Add(line);
                }
            }

            framer.TrimOverflow();
        }

        return (frames, rejected);
    }
    /// <summary>
    /// Replays a captured file through ReplaySensorStream, collecting frames and rejected lines.
    /// Written locally rather than shared with SensorStreamTests: the parity comparison above is between
    /// the live framing path (copied deliberately) and the replay path, and a helper both sides call
    /// would hide a divergence instead of reporting it.
    /// </summary>
    private static (List<SensorFrame> Frames, List<string> Rejected) ReplayToFrames(string path)
    {
        using var stream = new ReplaySensorStream(path) { IntervalMilliseconds = 0 };
        var frames = new List<SensorFrame>();
        var rejected = new List<string>();
        stream.FrameReceived += (_, frame) => frames.Add(frame);
        stream.LineRejected += (_, line) => rejected.Add(line);

        bool finished = false;
        stream.StatusChanged += (_, status) =>
        {
            if (status.Contains("Replay finished", StringComparison.Ordinal))
            {
                finished = true;
            }
        };

        // Waiting on the completion message rather than on a frame count: the shapes under test have
        // different expected counts, and baking a count into the helper would let the helper decide the
        // answer before the test measures it.
        stream.Open("ignored", 0);
        WaitFor(() => finished, TimeSpan.FromSeconds(10));
        stream.Close();

        return (frames, rejected);
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

    /// <summary>
    /// A GUID-unique file inside a GUID-unique folder. The folder is part of the scheme, not decoration:
    /// a single scratch folder shared by the whole class let one probe test leave a FILE named like the
    /// folder, and every later test then failed in Directory.CreateDirectory for a reason that had
    /// nothing to do with the behaviour under test. Private roots per test also exercise RawTextCapture's
    /// directory creation on every run, which is the behaviour DefaultCapturePath actually depends on.
    /// </summary>
    private static string CapturePath() =>
        Path.Combine(Path.GetTempPath(), "echo-capture-" + Guid.NewGuid().ToString("N"), "capture.txt");
}
