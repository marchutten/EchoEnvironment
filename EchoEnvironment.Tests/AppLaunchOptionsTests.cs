using System.IO;
using EchoEnvironment.Hardware;
using Xunit;

namespace EchoEnvironment.Tests;

/// <summary>
/// Tests for the composition root's argument parser (MIGRATION_PLAN 6c).
///
/// WHY THIS PARSER IS ITS OWN TYPE, AND SO ITS OWN TEST. §5 has carried "App runs with --sim and
/// --replay &lt;file&gt; - no hardware required" as only half satisfied since Phase 3: SimulatedSensorStream
/// and ReplaySensorStream both existed, and MainWindow constructed SerialSensorStream directly, so neither
/// was reachable. The missing piece was a decision, and a decision written inside a Window is neither
/// testable nor visible. As a pure function over string[] it is the most testable thing in the App.
///
/// WHAT THE TESTS ARE REALLY FOR. Not "does --sim parse" - that is obvious. The failure this parser exists
/// to prevent is the SILENT one: an unrecognised or mistyped argument being ignored, the window opening
/// anyway, and a user who typed --replay watching a serial port that has no device attached. Every
/// rejection test below is a test that the parser refuses rather than guesses.
///
/// WHY NO STA THREAD IS NEEDED HERE. CreateStream() constructs SerialSensorStream, SimulatedSensorStream and
/// ReplaySensorStream. None is a FrameworkElement - SerialPort is not UI-affine - so unlike
/// ZoneRendererTests, ScenePrimitivesTests and OrbitCameraControllerTests this file needs no apartment. If a
/// test here ever fails with "The calling thread must be STA", a stream implementation has acquired a UI
/// dependency, which the dependency graph forbids for exactly this assembly.
///
/// WHY CONSTRUCTING THE STREAMS IS SAFE IN A TEST. Constructing is not opening. SerialSensorStream's IsOpen
/// is `_port is not null` and no port is created by its constructor; SimulatedSensorStream starts its timer
/// in Open, not in the constructor; ReplaySensorStream only validates the path in the constructor and pumps
/// in Open. Nothing here touches a port, a timer or a file.
/// </summary>
public class AppLaunchOptionsTests
{
    /// <summary>
    /// A real file, because --replay's path is checked for existence at startup and a test of that check
    /// needs a path that exists. Written into the test assembly's own directory and deleted afterwards;
    /// deliberately NOT a path that merely looks plausible, because then the test would be asserting that
    /// the parser rejects a file it never actually looked for.
    /// </summary>
    private static string ExistingReplayFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"echo-launch-test-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "100,101,102,103,104,105,106,107,108,109,110,111,112,113,114,115\n");
        return path;
    }

    private static SensorSource Parse(params string[] args)
    {
        Assert.True(AppLaunchOptions.TryParse(args, out SensorSource? source, out string? error),
            $"parsing [{string.Join(" ", args)}] was rejected: {error}");

        Assert.NotNull(source);
        Assert.Null(error);

        return source!;
    }

    private static string Rejected(params string[] args)
    {
        Assert.False(AppLaunchOptions.TryParse(args, out SensorSource? source, out string? error),
            $"parsing [{string.Join(" ", args)}] was accepted when it should have been rejected");

        // The out-params are always assigned rather than left stale, the same discipline
        // OnRejection_OutParamsAreReset_NotLeftStaleFromThePreviousFrame pins for SensorFrameParser. A
        // caller that checked only the return value would otherwise find a half-built source behind it.
        Assert.Null(source);
        Assert.NotNull(error);

        return error!;
    }

    // -----------------------------------------------------------------------------------------
    // The default path: no arguments means the serial source, exactly as before 6c.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// No arguments means the serial source - the pre-6c behaviour, asserted twice because the two "no
    /// arguments" shapes are different things. An empty array is what a shell with no arguments produces;
    /// a null array is what a caller that never looked at the command line produces. Both must land on the
    /// serial source, and neither may be treated as a fault, because the serial path is the one every
    /// existing user is on.
    /// </summary>
    [Fact]
    public void AnEmptyCommandLineMeansTheSerialSource()
    {
        SensorSource source = Parse([]);

        Assert.Equal(SensorSourceKind.Serial, source.Kind);
        Assert.Null(source.ReplayPath);
        Assert.True(source.CaptureSupported);
        Assert.IsType<SerialSensorStream>(source.CreateStream());
    }

    [Fact]
    public void ANullCommandLineMeansTheSerialSource_RatherThanAFault()
    {
        Assert.True(AppLaunchOptions.TryParse(null, out SensorSource? source, out string? error),
            $"a null command line was rejected: {error}");

        Assert.NotNull(source);
        Assert.Null(error);

        Assert.Equal(SensorSourceKind.Serial, source!.Kind);
        Assert.True(source.CaptureSupported);
    }

    // -----------------------------------------------------------------------------------------
    // The two offline sources §5 has been waiting for since Phase 3.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void SimSelectsTheSimulatedStream_AndCaptureIsNotOfferedForIt()
    {
        SensorSource source = Parse("--sim");

        Assert.Equal(SensorSourceKind.Simulated, source.Kind);
        Assert.IsType<SimulatedSensorStream>(source.CreateStream());

        // The simulator generates frames rather than receiving text, so there is nothing to tee to a file.
        Assert.False(source.CaptureSupported);
    }

    [Fact]
    public void ReplaySelectsTheReplayStream_AndCarriesThePathThrough()
    {
        string path = ExistingReplayFile();
        try
        {
            SensorSource source = Parse("--replay", path);

            Assert.Equal(SensorSourceKind.Replay, source.Kind);
            Assert.Equal(path, source.ReplayPath);
            Assert.False(source.CaptureSupported);

            var stream = Assert.IsType<ReplaySensorStream>(source.CreateStream());

            // The path survived the round trip into the stream that has to read it. Asserted through the
            // stream's own port descriptor, because ReplaySensorStream reports the log as its "port" and
            // that is the only way to ask the constructed object what it was given.
            Assert.Equal(path, Assert.Single(stream.ListPorts()).Name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A replay source is asked for a path, and a path that does not exist is knowable at startup.
    ///
    /// WHY THIS IS REJECTED HERE RATHER THAN LEFT TO THE STREAM. ReplaySensorStream reports an unreadable
    /// file through Faulted, which surfaces in the status line after the window is open and after the user
    /// has pressed Connect. Catching it at startup costs the user no session and no confusion.
    /// </summary>
    [Fact]
    public void ReplayWithAMissingFileIsRejectedAtStartup_NotAfterConnect()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"echo-launch-missing-{Guid.NewGuid():N}.txt");
        Assert.False(File.Exists(missing));

        string error = Rejected("--replay", missing);

        Assert.Contains(missing, error, StringComparison.Ordinal);
        Assert.Contains("no such file", error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Three ways to ask for a replay without giving a readable path: no argument at all, an empty one, and
    /// a whitespace one. All three are rejected.
    ///
    /// The arguments are scalars rather than an array literal because InlineData binds its arguments to a
    /// `params` parameter individually; `new string[] { ... }` is not a valid attribute argument for a
    /// non-matching parameter type.
    /// </summary>
    [Theory]
    [InlineData("--replay")]
    [InlineData("--replay", "")]
    [InlineData("--replay", "   ")]
    public void ReplayWithoutAPathIsRejected(params string[] args)
    {
        string error = Rejected(args);

        Assert.Contains("--replay", error, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------------------------
    // The rejections that matter: a parser that guesses is worse than one that refuses.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// THE CENTRAL REJECTION. An unrecognised argument must not be ignored.
    ///
    /// If "--replayy" were silently skipped, the app would open a serial session while the user believed
    /// they were replaying a log: no frames, no capture file, and a status line blaming the port for a
    /// mistake made in the shell. The window would open, so nothing downstream would ever learn that the
    /// command line was wrong.
    /// </summary>
    [Fact]
    public void AnUnrecognisedArgumentIsRejected_NotSilentlyIgnored()
    {
        string error = Rejected("--replayy");

        Assert.Contains("Unrecognised argument", error, StringComparison.Ordinal);
        Assert.Contains(AppLaunchOptions.Usage, error, StringComparison.Ordinal);
    }

    [Fact]
    public void APositionalArgumentIsRejected_BecauseThereIsNoPositionalMeaningToGiveIt()
    {
        // The plan's sketch reads `new ReplaySensorStream(args[0])`, i.e. a bare positional path. That form
        // is rejected on purpose: accepting a bare path would make "EchoEnvironment capture.txt" a replay
        // and "EchoEnvironment --replay capture.txt" a replay, and a typo of the first form would be
        // indistinguishable from an attempt at the second.
        string error = Rejected("capture.txt");

        Assert.Contains("Unrecognised argument", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two sources is a contradiction, and the parser must refuse rather than let the last one win.
    ///
    /// `params` is what makes InlineData bind each value to one command-line argument; a plain string[]
    /// parameter asks xUnit to convert a single scalar into an array, which it correctly refuses
    /// (xUnit1010/1011). The second case repeats --sim rather than pairing two flags, because "the last one
    /// wins" has to be rejected for a repeated flag too.
    ///
    /// Both cases put --sim FIRST on purpose. See
    /// AProblemFoundEarlierIsReportedBeforeALaterConflict_DocumentsLeftToRightValidation for the order that
    /// does not reach the conflict at all, and why that is the correct behaviour rather than a gap.
    /// </summary>
    [Theory]
    [InlineData("--sim", "--replay", "x.txt")]
    [InlineData("--sim", "--sim")]
    public void TwoSourcesAreRejected_RatherThanLastOneWinning(params string[] args)
    {
        string error = Rejected(args);

        Assert.Contains("Only one sensor source", error, StringComparison.Ordinal);

        // The message names the source already chosen, so the user can tell which of the two arguments the
        // parser is complaining about rather than being told only that there are two.
        Assert.Contains("--sim", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE PARSER VALIDATES LEFT TO RIGHT AND REPORTS THE FIRST PROBLEM IT FINDS.
    ///
    /// The first version of TwoSourcesAreRejected included `--replay x.txt --sim` and asserted the conflict
    /// message. It failed, and the failure was the test's error rather than the parser's: --replay's path is
    /// checked for existence as soon as --replay is read, so "no such file: x.txt" is reported before the
    /// parser ever looks at --sim.
    ///
    /// That ordering is the behaviour to keep, not to work around. Of the two problems in that command line,
    /// the missing file is the one the user can act on immediately - a typo in a path - and it is the one
    /// that would otherwise be reported only after Connect, through ReplaySensorStream's Faulted event, in a
    /// window that is already open. Reporting the actionable problem first is what a startup check is for.
    /// Asserted here so the ordering is a recorded decision rather than an accident of where the checks sit.
    /// </summary>
    [Fact]
    public void AProblemFoundEarlierIsReportedBeforeALaterConflict_DocumentsLeftToRightValidation()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"echo-launch-absent-{Guid.NewGuid():N}.txt");
        Assert.False(File.Exists(missing));

        string error = Rejected("--replay", missing, "--sim");

        Assert.Contains("no such file", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(missing, error, StringComparison.Ordinal);

        // The conflict is NOT reported, and that is the point of the test: the file problem was found first.
        Assert.DoesNotContain("Only one sensor source", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The flag spelling is case-insensitive; the replay PATH is not touched by that decision.
    ///
    /// Asserted because "--SIM" being accepted is a convenience, while a path being lower-cased on the way
    /// into a File.Open would be a defect on a case-sensitive filesystem. The parser lower-cases the
    /// argument it matches and stores the path verbatim.
    /// </summary>
    [Fact]
    public void FlagsAreCaseInsensitive_AndTheReplayPathIsStoredVerbatim()
    {
        Assert.Equal(SensorSourceKind.Simulated, Parse("--SIM").Kind);
        Assert.Equal(SensorSourceKind.Simulated, Parse("--Sim").Kind);

        string path = ExistingReplayFile();
        try
        {
            SensorSource source = Parse("--REPLAY", path);
            Assert.Equal(path, source.ReplayPath);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // -----------------------------------------------------------------------------------------
    // The wiring, not just the parsing.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Each source produces the stream it names.
    ///
    /// WHY THIS IS NOT REDUNDANT WITH THE TESTS ABOVE. Those assert what the parser decided; this asserts
    /// the switch that turns the decision into a transport. A parser that returned the right enum while
    /// CreateStream() still returned a SerialSensorStream for every kind would leave --sim and --replay
    /// looking correct in every test above and broken in the app.
    /// </summary>
    [Theory]
    [InlineData("serial")]
    [InlineData("sim")]
    [InlineData("replay")]
    public void EachSourceCreatesTheStreamItNames(string which)
    {
        SensorSource source = which switch
        {
            "serial" => SensorSource.Serial(),
            "sim" => SensorSource.Simulated(),
            "replay" => SensorSource.Replay(ExistingReplayFile()),
            _ => throw new ArgumentOutOfRangeException(nameof(which), which, "Unknown source."),
        };

        ISensorStream stream = source.CreateStream();

        Type expected = which switch
        {
            "serial" => typeof(SerialSensorStream),
            "sim" => typeof(SimulatedSensorStream),
            "replay" => typeof(ReplaySensorStream),
            _ => typeof(SerialSensorStream),
        };

        Assert.IsType(expected, stream);

        // Constructing is not opening: a stream that reports itself open before Open() was called would
        // start delivering frames into a window that has not subscribed yet.
        Assert.False(stream.IsOpen);

        stream.Dispose();

        if (which == "replay")
        {
            File.Delete(source.ReplayPath!);
        }
    }

    /// <summary>
    /// A source is a FACTORY, not a stream, and that is what preserves the existing connection lifecycle.
    ///
    /// MainWindow's Connect_Click creates a stream and Disconnect disposes it. If the composition root had
    /// injected one ready-made instance, the first Disconnect would dispose it and the second Connect would
    /// meet an ObjectDisposedException - and a replay could never be watched twice. Two calls to
    /// CreateStream() must therefore produce two live objects.
    /// </summary>
    [Fact]
    public void CreateStreamReturnsAFreshLiveStreamEveryTime_SoReconnectStillWorks()
    {
        SensorSource source = SensorSource.Simulated();

        ISensorStream first = source.CreateStream();
        first.Dispose();

        ISensorStream second = source.CreateStream();

        Assert.NotSame(first, second);
        Assert.False(second.IsOpen);

        second.Dispose();
    }

    /// <summary>
    /// Capture is offered for exactly one source.
    ///
    /// MainWindow uses this to disable the Capture checkbox rather than accept a request nothing can honour.
    /// Asserted as "exactly one" rather than "serial is true", because the failure this guards is a replay
    /// or simulated session being handed a capture control that can only ever fail.
    /// </summary>
    [Fact]
    public void CaptureIsSupportedForTheSerialSourceAndForNoOther()
    {
        Assert.True(SensorSource.Serial().CaptureSupported);
        Assert.False(SensorSource.Simulated().CaptureSupported);
        Assert.False(SensorSource.Replay("anything.txt").CaptureSupported);
    }

    // -----------------------------------------------------------------------------------------
    // The two XAML/constructor facts 6c depends on, both invisible to a build.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// StartupUri is GONE from App.xaml, and this is the guard that makes its absence a fact.
    ///
    /// §6c warns that a composition root in OnStartup conflicts with StartupUri and that one of the two must
    /// go. Leaving both in place produces TWO windows - the one WPF opens from the URI with a parameterless
    /// constructor, and the one OnStartup shows - and a build reports nothing about it. This is the single
    /// highest-consequence thing 6c can get wrong, and it is undetectable from a compile.
    ///
    /// THE ATTRIBUTE, NOT THE WORD. The first version of this test asserted the absence of "StartupUri" and
    /// failed on App.xaml's own comment, which explains why the attribute was removed. Asserting the absence
    /// of the attribute form `StartupUri=` is the claim actually being made; a comment that names a removed
    /// thing is not that thing coming back, and a guard that cannot tell the difference would train everyone
    /// to delete the explanation.
    /// </summary>
    [Fact]
    public void AppXamlHasNoStartupUriAttribute_SoTheWindowIsConstructedInExactlyOnePlace()
    {
        string appXaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "EchoEnvironment", "App.xaml"));

        Assert.DoesNotContain("StartupUri=", appXaml, StringComparison.Ordinal);

        // The explanation for the removal is kept, and it is deliberately not what the guard looks at.
        Assert.Contains("StartupUri was REMOVED", appXaml, StringComparison.Ordinal);

        // And the composition root does construct the window, so removing StartupUri did not simply remove
        // the window.
        string appCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "EchoEnvironment", "App.xaml.cs"));
        Assert.Contains("protected override void OnStartup(StartupEventArgs e)", appCode, StringComparison.Ordinal);
        Assert.Contains("new MainWindow(source!)", appCode, StringComparison.Ordinal);
        Assert.Contains("window.Show();", appCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// MainWindow has no parameterless constructor.
    ///
    /// WHY THIS MATTERS. A parameterless MainWindow would be a way to build the window without being told
    /// which sensor source to use - which is the exact thing 6c exists to make impossible, and it is one
    /// convenience constructor away from reappearing. If it returns, the composition root becomes optional
    /// and --sim/--replay become decorative.
    /// </summary>
    [Fact]
    public void MainWindowCannotBeBuiltWithoutBeingToldWhichSourceToUse()
    {
        var constructor = Assert.Single(typeof(MainWindow).GetConstructors());

        // Assert.Single rather than Assert.Equal(1, ...): xUnit2013 rejects the latter, and it is the better
        // assertion anyway - it reports the parameters it found rather than a count that disagrees.
        var parameters = Assert.Single(constructor.GetParameters());

        Assert.Equal(typeof(SensorSource), parameters.ParameterType);
    }

    /// <summary>
    /// The App still cannot name a serial type directly, and 6c must not undo Phase 3 to get --sim.
    ///
    /// AppLaunchOptions constructs the three streams, which is legitimate: it is the composition root, and
    /// the streams live in EchoEnvironment.Hardware. What must stay true is that the App references the
    /// seam and not System.IO.Ports - AppAssemblyDoesNotReferenceSystemIoPorts already guards the assembly
    /// edge, and this guards the new file specifically, because a composition root is exactly where someone
    /// would reach for SerialPort "just to check the port exists".
    /// </summary>
    [Fact]
    public void TheCompositionRootUsesTheSeam_AndNeverNamesASerialPort()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "EchoEnvironment", "AppLaunchOptions.cs"));

        Assert.DoesNotContain("System.IO.Ports", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new SerialPort(", source, StringComparison.Ordinal);
        Assert.Contains("using EchoEnvironment.Hardware;", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Walks up from this test assembly to the repository root, the same anchor ArchitectureInvariantTests,
    /// FrameCoalescerTests and OrbitCameraControllerTests use. Duplicated rather than shared for the reason
    /// those files record: making one guard depend on another test class's private helper couples unrelated
    /// guards to one file's internals.
    /// </summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(typeof(AppLaunchOptionsTests).Assembly.Location)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EchoEnvironment.Tests.csproj")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        string? repoRoot = directory!.Parent?.FullName;
        Assert.NotNull(repoRoot);

        return repoRoot!;
    }
}
