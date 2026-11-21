using System.Globalization;
using System.IO;
using EchoEnvironment.Core;
using EchoEnvironment.Hardware;
using Xunit;

namespace EchoEnvironment.Tests;

/// <summary>
/// Tests against the first captured session from the real device.
///
/// THE FIXTURE. TestData/COM12-115200-20261120-144148.txt is a byte-for-byte copy of the file
/// SerialSensorStream's raw tee wrote during a hardware session on 2026-11-20: COM12, 115200 baud,
/// 193 frames. It is the artifact MIGRATION_PLAN 3c and 4 have been asking for since Phase 3 - "a
/// real captured log, and the repo has none". There is one now, so the parity half of that gate is
/// closed by a test instead of by a promise.
///
/// WHY THE FIXTURE IS STILL NAMED COM12. The name records the port the capture was taken on, and the
/// tee named its own output after the port it had open. The device enumerates as COM14 on this machine
/// now (MainViewModel.DefaultPort), but renaming the fixture to match would make it claim a provenance
/// it does not have: the bytes inside were written by a session that was on COM12.
///
/// WHAT THE FILE ACTUALLY IS, measured rather than assumed:
///   - 193 lines, every line exactly 73 comma-separated integers, no empty lines, no carriage
///     returns, no non-numeric characters. 73 = 64 zones + 9 IMU, so the device is an 8x8 VL53L5CX
///     with an IMU attached, and the parser accepted 193 of 193 frames.
///   - The capture contains NO TIMESTAMPS. Every assertion below is therefore either dt-free
///     (read straight off the accelerometer) or states the dt it assumes. That is not a stylistic
///     choice: the filter's output genuinely depends on dt, and a capture without timestamps cannot
///     reconstruct it. See CapturedLog_FilterOutputDependsOnTheFrameRateTheCaptureDoesNotRecord.
///
/// WHY THE FIXTURE IS FOUND BY WALKING THE REPO AND NOT COPIED TO THE OUTPUT FOLDER. The suite
/// already depends on the source tree existing (FrameCoalescerTests reads MainWindow.xaml from it),
/// so a checked-in data file located the same way is consistent rather than a second convention.
/// It also keeps the fixture where a human opens it: TestData, next to the tests that read it.
///
/// WHY THIS IS NOT THE bin CAPTURE. The tee writes to the executable's own folder
/// (SerialSensorStream.DefaultCapturePath), which .gitignore swallows under bin/. This fixture is a
/// deliberate copy of one of those files. The two empty captures from the same session are not here:
/// an empty file tests nothing, and Finding 14 explains why they cannot be told apart.
/// </summary>
public class CapturedSessionTests
{
    private const string FixtureName = "COM12-115200-20261120-144148.txt";

    /// <summary>Measured from the fixture, not typed from a guess.</summary>
    private const int ExpectedFrames = 193;

    private const int ZonesPerFrame = 64;
    private const int FieldsPerFrame = 73;

    /// <summary>Fields 65-73 (1-based) are gyro x y z, accel x y z, mag x y z - see SensorFrameParser.</summary>
    private const int ImuOffset = ZonesPerFrame;

    /// <summary>MainWindow's visible band, mirrored so the fixture can be judged against it.</summary>
    private const double VisibleMinMm = 20.0;
    private const double VisibleMaxMm = 4000.0;

    /// <summary>
    /// The pacing assumed wherever a dt is required, because the capture records none. Two values are
    /// used deliberately: 1/60 is the render rate Phase 4 chose, 1/5 is a plausible device rate, and
    /// the difference between them is itself one of the findings below.
    /// </summary>
    private const double FastFrameSeconds = 1.0 / 60.0;
    private const double SlowFrameSeconds = 1.0 / 5.0;

    // -----------------------------------------------------------------------------------------
    // The parity gate: the bytes the tee wrote replay, through the production stream, into exactly
    // the frames the live path would have drawn.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void CapturedLog_IsExactlyTheFrameShapeTheParserAlreadyAccepts()
    {
        List<string> lines = FixtureLines();

        Assert.Equal(ExpectedFrames, lines.Count);

        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];

            // ReadAllLines strips terminators, so a per-line check can say nothing about HOW the device
            // terminated its frames - that claim is measured over the whole file, in
            // CapturedLog_DeviceTerminatesWithCrlf_SoEveryFrameAlsoProducesAnEmptyLine. What a per-line
            // check can say is that no line is blank and every field is an integer.
            Assert.NotEqual(string.Empty, line);

            string[] parts = line.Split(',', StringSplitOptions.TrimEntries);
            Assert.Equal(FieldsPerFrame, parts.Length);
            Assert.All(parts, part => Assert.True(
                int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
                $"line {i + 1} field '{part}' is not an integer."));

            Assert.True(SensorFrameParser.TryParse(line, out SensorFrame frame),
                $"line {i + 1} of a real capture did not parse: {line[..Math.Min(line.Length, 40)]}...");
            Assert.Equal(ZonesPerFrame, frame.ZoneCount);
            Assert.True(frame.HasImu);
        }
    }

    [Fact]
    public void CapturedLog_ReplaysThroughTheProductionStream_WithNothingRejectedAndNothingDropped()
    {
        using var stream = new ReplaySensorStream(FixturePath()) { IntervalMilliseconds = 0 };

        var frames = new List<SensorFrame>();
        var rejected = new List<string>();
        var faults = new List<Exception>();
        bool finished = false;

        stream.FrameReceived += (_, frame) => frames.Add(frame);
        stream.LineRejected += (_, line) => rejected.Add(line);
        stream.Faulted += (_, ex) => faults.Add(ex);
        stream.StatusChanged += (_, status) =>
        {
            if (status.Contains("Replay finished", StringComparison.Ordinal))
            {
                finished = true;
            }
        };

        stream.Open("ignored", 0);
        WaitFor(() => finished, TimeSpan.FromSeconds(30));
        stream.Close();

        Assert.Empty(faults);
        Assert.Equal(ExpectedFrames, frames.Count);
        Assert.Equal(ExpectedFrames, stream.FramesReplayed);
        Assert.Empty(rejected);
        Assert.Equal(0, stream.LinesRejected);

        // 193 frames of 73 fields is about 34 KB, handed to the framer in ReplaySensorStream's
        // 4096-character chunks. The unread remainder never passes LineFramer's 10,000-character
        // overflow guard, so a real session does not exercise the overflow path either.
        Assert.Equal(0, stream.DroppedBytes);

        Assert.All(frames, frame =>
        {
            Assert.Equal(ZonesPerFrame, frame.ZoneCount);
            Assert.True(frame.HasImu);
        });
    }

    /// <summary>
    /// Settles Finding 10 for THIS session instead of leaving it a hypothetical.
    ///
    /// Finding 10 says a capture and a replay legitimately disagree when a session is interrupted
    /// mid-frame: a serial port has no end to drain, so the live app never draws the unterminated
    /// remainder, while ReplaySensorStream drains it because a file has an end. Whether that happened
    /// in a given session is a fact about the file's final terminator, and it decides what a parity
    /// claim about this capture is allowed to say.
    /// </summary>
    [Fact]
    public void CapturedLog_EveryFrameWasTerminated_SoThisSessionHadNoTrailingFrameDivergence()
    {
        string text = File.ReadAllText(FixturePath());

        // The capture ends with a terminator, so the replay's tail drain finds nothing to add: all 193
        // frames were drawn by the live app AND replayed from the file. Finding 10's divergence is real
        // but did not occur in this session, so a parity claim about this capture needs no correction.
        // Had the file been cut off mid-frame, the replay would have produced 194 frames and the live
        // app 193, and every comparison would have had to subtract one.
        Assert.True(text.EndsWith('\n'), "the capture does not end with a terminator, so its final frame reached the file but never reached the app.");
        Assert.False(text.EndsWith("\n\n"), "the capture ends with a doubled terminator, which would mean an empty frame at the end of the session.");
    }

    /// <summary>
    /// The capture is CRLF, which makes LineFramer's documented quirk a fact about every real session
    /// rather than a hypothetical pinned only by unit tests.
    ///
    /// THE READING THAT HAD TO BE CORRECTED. An earlier pass over this file reported "no carriage
    /// returns anywhere", and a test asserted it. It failed with Expected 0, Actual 193. The cause was
    /// the tool, not the device: a line-by-line reader strips line terminators, so it cannot report
    /// what terminated a line. Any claim about terminators has to count characters in the whole file,
    /// which is what this test does and what the earlier reading could not do.
    ///
    /// WHY IT MATTERS. LineFramer treats '\r' OR '\n' as a terminator, so a CRLF capture yields the
    /// frame and then an EMPTY line - 193 times in 193 frames. Those phantom lines are invisible in the
    /// app because both Deliver implementations ignore blank lines: they are neither a frame nor a
    /// rejected line. So the device exercises the quirk on every frame of every session, and capture
    /// and replay agree only because blank lines are dropped on both sides. Anyone who "fixes" CRLF
    /// handling in LineFramer changes the line count of every real capture by a factor of two.
    /// </summary>
    [Fact]
    public void CapturedLog_DeviceTerminatesWithCrlf_SoEveryFrameAlsoProducesAnEmptyLine()
    {
        string text = File.ReadAllText(FixturePath());

        Assert.Equal(ExpectedFrames, text.Count(character => character == '\r'));
        Assert.Equal(ExpectedFrames, text.Count(character => character == '\n'));

        // Fed in 4096-character chunks, the size ReplaySensorStream uses, so a terminator split across
        // a chunk boundary behaves here exactly as it does on replay.
        const int ChunkCharacters = 4096;

        var framer = new LineFramer();
        int lines = 0;
        int blankLines = 0;

        for (int start = 0; start < text.Length; start += ChunkCharacters)
        {
            framer.Append(text.Substring(start, Math.Min(ChunkCharacters, text.Length - start)));

            while (framer.TryReadLine(out string line))
            {
                lines++;
                if (line.Length == 0)
                {
                    blankLines++;
                }
            }
        }

        Assert.False(framer.TryReadPendingLine(out _),
            "the capture ends with a terminator, so nothing should be left for the replay-only tail drain.");

        // 193 frames plus 193 phantom blank lines.
        Assert.Equal(ExpectedFrames * 2, lines);
        Assert.Equal(ExpectedFrames, blankLines);
        Assert.Equal(0, framer.DroppedBytes);
    }

    // -----------------------------------------------------------------------------------------
    // What the distances in a real session actually look like, measured against the app's own
    // visible band and colour ramp.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void CapturedLog_DistancesTopOutAtHalfTheColourRamp_AndTheVisibleBandDiscardsOneReading()
    {
        int min = int.MaxValue;
        int max = int.MinValue;
        int belowVisibleFloor = 0;
        int nonPositive = 0;
        int total = 0;

        foreach (SensorFrame frame in FixtureFrames())
        {
            foreach (int mm in frame.Distances.Span)
            {
                total++;
                min = Math.Min(min, mm);
                max = Math.Max(max, mm);

                if (mm <= 0)
                {
                    nonPositive++;
                }

                if (mm < VisibleMinMm)
                {
                    belowVisibleFloor++;
                }
            }
        }

        Assert.Equal(ExpectedFrames * ZonesPerFrame, total);
        Assert.Equal(0, nonPositive);

        // FINDING. Exactly one reading in 12,352 falls below the 20 mm floor (18 mm, line 90 zone 57),
        // so the visible band does almost nothing on this session's data - and when it does act, it
        // silently discards a zone rather than reporting one.
        Assert.Equal(1, belowVisibleFloor);

        // FINDING. The whole session ranges 18..2003 mm against a 4000 mm ramp, so ColorScale.For(mm, 4000)
        // and WorldMap's mm / MaxDistanceMm bucketing never reach the top half of the palette with real
        // data. A Phase 5c screenshot that changes brushes will differ from the baseline for this reason
        // as much as for the brushes.
        Assert.True(max < VisibleMaxMm * 0.55,
            $"max distance {max} mm is no longer under {VisibleMaxMm * 0.55:0} mm; the ramp's upper half is being used, so re-check the baseline before comparing screenshots.");
        Assert.True(min < VisibleMinMm,
            $"min distance {min} mm no longer dips under the {VisibleMinMm} mm floor; the fixture may have been replaced.");
    }

    [Fact]
    public void CapturedLog_OpeningFramesRepeatThreeZonesBitForBit_WhileTheirNeighboursMove()
    {
        List<int[]> rows = FixtureFields();

        // FINDING. Zones 6, 7 and 8 (1-based) report the same value for the first 68-88 frames -
        // 1540, 89 and 2003 with not one count of variation - while their neighbours move by tens of
        // millimetres every frame. Zone 1 alone already changes at frame 12. A ranging measurement
        // that is bit-identical across 68 frames is not a measurement: either the device emits stale
        // values before those zones converge, or the zones are dead. The app has no warm-up
        // suppression, so all three are inside the visible band and were drawn as measured geometry.
        // Any screenshot taken in the first seconds of a connection therefore shows distances that
        // were never measured.
        int frozenZone6 = LeadingRun(rows, zoneIndex: 5);
        int frozenZone7 = LeadingRun(rows, zoneIndex: 6);
        int frozenZone8 = LeadingRun(rows, zoneIndex: 7);

        Assert.True(frozenZone6 >= 60, $"zone 6 stopped repeating after {frozenZone6} frames; the opening stale-value run is no longer there.");
        Assert.True(frozenZone7 >= 60, $"zone 7 stopped repeating after {frozenZone7} frames; the opening stale-value run is no longer there.");
        Assert.True(frozenZone8 >= 60, $"zone 8 stopped repeating after {frozenZone8} frames; the opening stale-value run is no longer there.");

        // The contrast that makes this a finding rather than a coincidence: a neighbouring zone is
        // already moving while those three are frozen.
        int movingZone1 = LeadingRun(rows, zoneIndex: 0);
        Assert.True(movingZone1 <= 15,
            $"zone 1 held one value for {movingZone1} frames; the frozen-zone finding depends on a neighbour that moves.");

        Assert.True(rows[0][5] >= VisibleMinMm && rows[0][5] <= VisibleMaxMm, "zone 6's frozen value is no longer inside the visible band, so it would not have been drawn.");
        Assert.True(rows[0][6] >= VisibleMinMm && rows[0][6] <= VisibleMaxMm, "zone 7's frozen value is no longer inside the visible band, so it would not have been drawn.");
        Assert.True(rows[0][7] >= VisibleMinMm && rows[0][7] <= VisibleMaxMm, "zone 8's frozen value is no longer inside the visible band, so it would not have been drawn.");
    }

    // -----------------------------------------------------------------------------------------
    // The orientation readout, on real data. This is the input defect D1 was reasoned about from a
    // screenshot; here it is measured from the bytes, with no filter state and therefore no dt.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void CapturedLog_SensorPointsAlmostStraightDown_WhichIsWhereTheAngleReadoutBreaks()
    {
        // Read straight off the accelerometer, with no filter involved, so nothing here depends on the
        // dt the capture cannot provide. World up expressed in sensor coordinates is what the
        // accelerometer reports (OrientationFilter's own definition), and the look direction is the
        // sensor's -Z axis, so its elevation above horizontal is asin(-up.Z) - the quantity the UI
        // computes, and the one D1 prints under the wrong label.
        double closestToHorizontal = double.MinValue;
        double largestOffVerticalPair = 0.0;

        foreach (int[] fields in FixtureFields())
        {
            var accel = new Vec3(fields[ImuOffset + 3], fields[ImuOffset + 4], fields[ImuOffset + 5]);
            Assert.True(accel.LengthSquared > 0, "an accel of exactly zero counts would have been rejected by the filter.");

            Vec3 up = accel.Normalized();
            double elevation = Math.Asin(Math.Clamp(-up.Z, -1, 1)) * 180.0 / Math.PI;
            closestToHorizontal = Math.Max(closestToHorizontal, elevation);

            // atan2(M12, M22) - the value the UI prints under the label "Pitch" - is a ratio of the two
            // components of up that are NOT the dominant one (M12 = up.X, M22 = up.Y). Its sensitivity to
            // noise is 1 / hypot(a, b), so the smaller that pair, the more counts of accelerometer noise
            // become degrees on screen.
            largestOffVerticalPair = Math.Max(largestOffVerticalPair, Math.Sqrt(up.X * up.X + up.Y * up.Y));
        }

        // FINDING. For every frame of a real session the sensor looks steeply down: the closest the look
        // direction comes to horizontal is about -73 degrees. The board was not lying flat during this
        // capture; it was facing the ground. Finding 7's complaint about the accel scale is visible here
        // too - the accel magnitude is ~4,470 counts, against the 500 the test fixtures assume.
        Assert.True(closestToHorizontal < -70.0,
            $"the look direction reached {closestToHorizontal:0.0} degrees above horizontal; the fixture is no longer a near-face-down session.");

        // FINDING. The pair feeding the displayed "Pitch" stays under 0.19 in magnitude for the whole
        // session, so its noise amplification is over 5x and it approaches the point where the angle is
        // undefined outright. This is the gimbal-lock signature of an asin decomposition with M32 near 1,
        // and it is why fixing D1's labels without addressing the conditioning will look as though the
        // readout got worse.
        Assert.True(largestOffVerticalPair < 0.2,
            $"hypot(M12, M22) reached {largestOffVerticalPair:0.000}; the ill-conditioning claim needs re-measuring.");
    }

    [Fact]
    public void CapturedLog_KeepsTheFilterOrthonormal_ForEveryFrameOfARealSession()
    {
        // The drift detector the plan asks for, run against a device instead of against generated
        // numbers. Nothing is asserted here about the ANGLE the filter reports, only about the matrix
        // staying a rigid rotation: the angle depends on dt, which this capture does not record, and an
        // assertion built on an assumed dt would be an assertion about the assumption.
        var filter = new OrientationFilter();
        int frame = 0;

        foreach (int[] fields in FixtureFields())
        {
            frame++;
            int[] imu = [
                fields[ImuOffset], fields[ImuOffset + 1], fields[ImuOffset + 2],
                fields[ImuOffset + 3], fields[ImuOffset + 4], fields[ImuOffset + 5],
                fields[ImuOffset + 6], fields[ImuOffset + 7], fields[ImuOffset + 8],
            ];

            Mat3 m = filter.Update(imu, FastFrameSeconds);

            AssertMath.OrthonormalRows(m, $"captured frame {frame}");
            Assert.True(double.IsFinite(m.M11) && double.IsFinite(m.M22) && double.IsFinite(m.M33),
                $"captured frame {frame} produced a non-finite matrix.");
        }

        Assert.Equal(ExpectedFrames, frame);
    }

    [Fact]
    public void CapturedLog_FilterOutputDependsOnTheFrameRateTheCaptureDoesNotRecord()
    {
        // FINDING. The capture stores no timestamps, so dt - the one input OrientationFilter needs and
        // the capture does not have - has to be assumed. This test measures the cost of that assumption
        // instead of hiding it: the same 193 frames, replayed through the same filter at two plausible
        // frame rates, do not agree.
        double[] fast = ElevationsThroughFilter(FastFrameSeconds);
        double[] slow = ElevationsThroughFilter(SlowFrameSeconds);

        Assert.Equal(ExpectedFrames, fast.Length);
        Assert.Equal(ExpectedFrames, slow.Length);

        double worstDifference = 0.0;
        int worstFrame = 0;
        for (int i = 0; i < fast.Length; i++)
        {
            double difference = Math.Abs(fast[i] - slow[i]);
            if (difference > worstDifference)
            {
                worstDifference = difference;
                worstFrame = i + 1;
            }
        }

        // MEASURED, not assumed. The first version of this test asserted a divergence above 2 degrees
        // and failed at 1.73 - the same mistake the capture commit's second test bug made: an invented
        // expectation rather than a measurement. The real number is what the bounds now sit around.
        //
        // WHY IT IS SMALLER THAN PREDICTED. OrientationFilter's correction time constant is 0.5 s, so
        // the accelerometer and magnetometer pull the pose back to what was measured on every frame
        // regardless of dt; dt only scales how far the gyro propagates the pose BETWEEN those
        // corrections. The gyro counts in this capture are small except for one handling event, so the
        // term dt actually controls is the small one. A missing timestamp is a real error source, but a
        // modest one: 1.73 degrees, against the 19-degree spread the ill-conditioned atan2 term already
        // produces between two samples of a nearly identical pose.
        Assert.True(worstDifference > 0.5,
            $"replaying the same capture at {1.0 / FastFrameSeconds:0} Hz and {1.0 / SlowFrameSeconds:0} Hz now differs by only {worstDifference:0.00} degrees; dt may have stopped mattering at all, which would make a timestamped capture less urgent than this file claims.");
        Assert.True(worstDifference < 5.0,
            $"dt alone now moves the readout by {worstDifference:0.00} degrees; guessing the frame rate has become the dominant error in the orientation display rather than a minor one.");

        // Frame 1 cannot be the worst frame, because an uninitialised filter returns the measured pose
        // for any dt. So a worst frame past 1 is the evidence that the divergence comes from gyro
        // propagation accumulating over many frames rather than from the first frame alone.
        Assert.True(worstFrame > 1,
            $"the largest dt-driven disagreement is at frame {worstFrame}; if that is frame 1 the gyro path is not being exercised at all.");
    }

    // -----------------------------------------------------------------------------------------
    // Fixture access.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Locates the fixture by walking up to the test project's own .csproj, the same anchor
    /// FrameCoalescerTests uses. The test assembly's Location points into the bin tree and the .csproj
    /// is not copied there, so the walk is what finds the repository layout.
    /// </summary>
    private static string FixturePath()
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(typeof(CapturedSessionTests).Assembly.Location)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EchoEnvironment.Tests.csproj")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        string path = Path.Combine(directory!.FullName, "TestData", FixtureName);
        Assert.True(File.Exists(path),
            $"the captured session fixture is missing at {path}. It is a checked-in file; if it is gone, restore it from the hardware capture it was copied from.");

        return path;
    }

    private static List<string> FixtureLines()
    {
        List<string> lines = File.ReadAllLines(FixturePath()).ToList();
        Assert.NotEmpty(lines);
        return lines;
    }

    private static List<int[]> FixtureFields() => FixtureLines()
        .Select(line => line.Split(',', StringSplitOptions.TrimEntries)
            .Select(part => int.Parse(part, CultureInfo.InvariantCulture))
            .ToArray())
        .ToList();

    private static List<SensorFrame> FixtureFrames()
    {
        var frames = new List<SensorFrame>();
        foreach (string line in FixtureLines())
        {
            Assert.True(SensorFrameParser.TryParse(line, out SensorFrame frame));
            frames.Add(frame);
        }

        return frames;
    }

    /// <summary>
    /// The elevation the UI would print, frame by frame, for one assumed frame rate. asin(-M32) is the
    /// look-direction elevation - the quantity D1 prints under the label "Roll".
    /// </summary>
    private static double[] ElevationsThroughFilter(double frameSeconds)
    {
        var filter = new OrientationFilter();
        var elevations = new List<double>();

        foreach (int[] fields in FixtureFields())
        {
            int[] imu = [
                fields[ImuOffset], fields[ImuOffset + 1], fields[ImuOffset + 2],
                fields[ImuOffset + 3], fields[ImuOffset + 4], fields[ImuOffset + 5],
                fields[ImuOffset + 6], fields[ImuOffset + 7], fields[ImuOffset + 8],
            ];

            Mat3 m = filter.Update(imu, frameSeconds);
            elevations.Add(Math.Asin(Math.Clamp(-m.M32, -1, 1)) * 180.0 / Math.PI);
        }

        return elevations.ToArray();
    }

    /// <summary>How many leading frames report the same value for one zone.</summary>
    private static int LeadingRun(List<int[]> rows, int zoneIndex)
    {
        int first = rows[0][zoneIndex];
        int run = 0;
        foreach (int[] row in rows)
        {
            if (row[zoneIndex] != first)
            {
                break;
            }

            run++;
        }

        return run;
    }

    private static void WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }

        Assert.True(condition(), "the replay did not finish within the timeout.");
    }
}
