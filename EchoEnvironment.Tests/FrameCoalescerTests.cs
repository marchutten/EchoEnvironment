using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EchoEnvironment.Core;
using EchoEnvironment.Rendering;
using Xunit;

namespace EchoEnvironment.Tests;

/// <summary>
/// Phase 4 tests for defect D2: the coalescing stage between the transport thread and the UI.
///
/// D2 was that every parsed line became its own Dispatcher.BeginInvoke, each performing a full frame
/// render. The pending-render queue therefore grew at the frame arrival rate, and the latency of what
/// the user saw grew with it. The fix is a hand-off slot that cannot hold more than one frame, so
/// queue depth is a constant and the render rate becomes a UI decision instead of a baud-rate
/// consequence.
///
/// These tests are the reason FrameCoalescer lives in Core rather than in MainWindow as the plan's
/// sketch said: a coalescer inside a Window can only be checked by running the app against a sensor at
/// max baud, which is a hardware-only check and therefore not a test.
/// </summary>
public class FrameCoalescerTests
{
    private static SensorFrame Frame(int distanceMm) =>
        new(new[] { distanceMm, distanceMm, distanceMm, distanceMm }, 4, Array.Empty<int>(), false, DateTimeOffset.UnixEpoch);

    // ------------------------------------------------------------------
    // The drop policy: which frame survives when the producer outruns the UI.
    // ------------------------------------------------------------------

    /// <summary>
    /// The test that would have failed had Phase 4's sketch been copied verbatim.
    ///
    /// The sketch writes FullMode = BoundedChannelFullMode.DropWrite with the comment "newest frame
    /// wins". Those contradict each other: DropWrite discards the item being WRITTEN, so a full
    /// channel keeps the frame it already holds — the stale one — and throws away the fresh one.
    /// "Newest frame wins" is DropOldest. This was chosen by observed behaviour, not by the enum's
    /// name, and this is the test that pins the choice.
    /// </summary>
    [Fact]
    public void KeepsTheNewestFrame_DiscardsTheStaleOne()
    {
        var coalescer = new FrameCoalescer();

        coalescer.Write(Frame(100));
        coalescer.Write(Frame(200));
        coalescer.Write(Frame(300));

        Assert.True(coalescer.TryRead(out SensorFrame frame));
        Assert.Equal(300, frame.Distances.Span[0]);
        Assert.Equal(2, coalescer.DroppedFrames);
    }

    /// <summary>
    /// A capacity-1 channel that ever held two frames would mean the UI can queue work behind a
    /// render, which is exactly the shape D2 had. Pending is the quantity latency is made of, so its
    /// bound is asserted directly rather than inferred from the constructor argument.
    /// </summary>
    [Fact]
    public void PendingNeverExceedsOneFrame_RegardlessOfHowFastTheProducerIs()
    {
        var coalescer = new FrameCoalescer();

        for (int i = 1; i <= 500; i++)
        {
            coalescer.Write(Frame(i));
            Assert.True(coalescer.Pending <= 1, $"Pending grew to {coalescer.Pending} after {i} writes.");
        }

        Assert.Equal(1, coalescer.Pending);
        Assert.Equal(499, coalescer.DroppedFrames);
    }

    /// <summary>
    /// The drain side of the same bound: a UI draining at its own policy rate sees exactly one frame
    /// per drain and never a backlog, no matter how many frames arrived in between.
    ///
    /// THE FIRST VERSION OF THIS TEST ASSERTED Pending == 1 AFTER THE READ, AND FAILED. That was a
    /// misreading of my own policy, not a defect in it: a burst of 60 leaves exactly 1 waiting, and
    /// the read consumes it, so 0 is the correct value afterwards. The assertion that actually matters
    /// is the one before the read — 60 arrivals collapsed to 1 renderable frame — so it is asserted
    /// there instead of being inferred after the fact.
    /// </summary>
    [Fact]
    public void OneFramePerDrain_ArrivalRateDoesNotCreateABacklog()
    {
        var coalescer = new FrameCoalescer();
        int drained = 0;

        for (int tick = 0; tick < 10; tick++)
        {
            for (int burst = 0; burst < 60; burst++)
            {
                coalescer.Write(Frame(tick * 100 + burst));
            }

            // 60 arrivals, one renderable frame. This is the whole of D2's fix, measured.
            Assert.Equal(1, coalescer.Pending);

            Assert.True(coalescer.TryRead(out SensorFrame frame));
            drained++;

            // The frame that survives a burst is the newest one, not the first one.
            Assert.Equal(tick * 100 + 59, frame.Distances.Span[0]);
            Assert.Equal(0, coalescer.Pending);
        }

        Assert.Equal(10, drained);

        // 600 written, 10 rendered, 590 coalesced away.
        Assert.Equal(590, coalescer.DroppedFrames);
    }

    /// <summary>
    /// The number shown in StatusText must count only frames the policy coalesced away — not frames
    /// discarded deliberately by Clear, and not frames that were rendered.
    ///
    /// THIS TEST WAS FIRST WRITTEN AS AN IDENTITY AND THAT FORM ASSERTED NOTHING. It read
    /// "written == read + cleared + pending + DroppedFrames", but DroppedFrames is DEFINED as
    /// written - read - cleared - pending, so the equation holds by construction however wrong the
    /// counters get. It was caught by review, not by failing. Replaced here with a hand-computed
    /// expectation derived from a specific sequence, which is falsifiable: fold Clear into the drop
    /// count and this reports 41; count a rendered frame as dropped and it reports 41 as well.
    ///
    /// The sequence, and the arithmetic behind 40:
    ///   37 writes        -> 1 waiting, 36 coalesced away
    ///   1 read           -> 0 waiting, 36
    ///   Clear on empty   -> no-op, 36
    ///   5 writes         -> 1 waiting, 40
    ///   Clear            -> 0 waiting, 40, and the cleared frame is NOT a drop
    /// </summary>
    [Fact]
    public void DroppedFramesCountsOnlyCoalescedAwayFrames_NotClearedOnes()
    {
        var coalescer = new FrameCoalescer();

        for (int i = 0; i < 37; i++)
        {
            coalescer.Write(Frame(i));
        }

        Assert.True(coalescer.TryRead(out _));
        coalescer.Clear();

        for (int i = 0; i < 5; i++)
        {
            coalescer.Write(Frame(i));
        }

        coalescer.Clear();

        Assert.Equal(0, coalescer.Pending);
        Assert.Equal(40, coalescer.DroppedFrames);
    }

    /// <summary>
    /// Clear is tracked apart from DroppedFrames on purpose: a frame discarded on disconnect was
    /// discarded deliberately, not coalesced away. Folding the two together would make the visible
    /// drop counter lie about the policy it is supposed to explain.
    /// </summary>
    [Fact]
    public void ClearDiscardsPendingFramesWithoutCountingThemAsDrops()
    {
        var coalescer = new FrameCoalescer();
        coalescer.Write(Frame(123));
        Assert.Equal(1, coalescer.Pending);

        coalescer.Clear();

        Assert.Equal(0, coalescer.Pending);
        Assert.Equal(0, coalescer.DroppedFrames);
        Assert.False(coalescer.TryRead(out SensorFrame frame));
        Assert.Equal(default, frame);
    }

    /// <summary>
    /// An empty drain must be a no-op that reports honestly. A timer tick that "rendered" a default
    /// frame would blank the scene between arrivals.
    /// </summary>
    [Fact]
    public void TryReadOnAnEmptyCoalescerReportsFalseAndYieldsNoFrame()
    {
        var coalescer = new FrameCoalescer();

        Assert.False(coalescer.TryRead(out SensorFrame frame));
        Assert.Equal(default, frame);
        Assert.Equal(0, coalescer.DroppedFrames);
        Assert.Equal(0, coalescer.Pending);
    }

    /// <summary>
    /// Write is called from the transport callback. A producer that can block the transport is a
    /// defect in its own right, so the non-blocking claim is checked with a timeout rather than
    /// asserted. The timeout is generous: the point is "does not hang", not "is fast".
    /// </summary>
    [Fact]
    public async Task WriteNeverBlocksTheProducer()
    {
        var coalescer = new FrameCoalescer();

        Task writes = Task.Run(() =>
        {
            for (int i = 0; i < 5000; i++)
            {
                coalescer.Write(Frame(i));
            }
        });

        // Task.Wait is a blocking call, which the xunit analyzer rejects (xUnit1031) because a test
        // that blocks can deadlock the runner. WhenAny plus a delay is the non-blocking form of the
        // same question: did the producer finish, or did it hang?
        Task finished = await Task.WhenAny(writes, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(finished == writes, "Write blocked the producer.");
        Assert.Equal(1, coalescer.Pending);
        Assert.Equal(4999, coalescer.DroppedFrames);
    }

    /// <summary>
    /// SerialPort raises DataReceived on thread-pool threads, so overlapping writes are possible in
    /// the real transport. The bound must survive that, and the drop counter must not lose increments.
    ///
    /// THE FIRST VERSION OF THE COUNTER ASSERTION WAS A TAUTOLOGY AND WAS CAUGHT BY REVIEW, NOT BY
    /// FAILING. It read "written - 1 == DroppedFrames + Pending", but DroppedFrames is DEFINED as
    /// written - read - cleared - pending, so that equation holds by construction no matter how wrong
    /// the counters become. Replaced with a hand-computed expectation: 1600 writes, one drained, so
    /// exactly 1599 were coalesced away. Let a single Interlocked.Increment be lost under contention
    /// and this reports 1598 and fails.
    /// </summary>
    [Fact]
    public void ConcurrentWritersKeepTheBoundAndTheCountersExact()
    {
        var coalescer = new FrameCoalescer();
        const int writers = 8;
        const int perWriter = 200;

        Parallel.For(0, writers, _ =>
        {
            for (int i = 0; i < perWriter; i++)
            {
                coalescer.Write(Frame(i));
            }
        });

        Assert.True(coalescer.Pending <= 1, $"pending grew past one slot: {coalescer.Pending}");
        Assert.True(coalescer.TryRead(out _));

        // 1600 written, 1 drained, 1599 coalesced away — computed here, not derived from the
        // property's own definition.
        Assert.Equal(1599, coalescer.DroppedFrames);
        Assert.Equal(0, coalescer.Pending);
    }

    /// <summary>
    /// Guards the counter's width the same way LineFramer's DroppedBytes is guarded: a counter that
    /// wraps is worse than no counter, because it reads as "no frames dropped".
    /// </summary>
    [Fact]
    public void CountersAreLongNotInt()
    {
        Type type = typeof(FrameCoalescer);

        foreach (string name in new[] { "Pending", "DroppedFrames" })
        {
            PropertyInfo property = type.GetProperty(name)!;
            Assert.True(property.PropertyType == typeof(long),
                $"{name} must be long; a counter that wraps reads as 'no frames dropped'.");
        }

        foreach (string name in new[] { "_written", "_read", "_cleared" })
        {
            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.True(field.FieldType == typeof(long), $"{name} must be long.");
        }
    }

    // ------------------------------------------------------------------
    // Where the policy lives, and what MainWindow actually does with it.
    // ------------------------------------------------------------------

    /// <summary>
    /// The plan's sketch puts the channel in the App layer. It is in Core instead, because D2 is a
    /// behaviour fix and behaviour must be testable headlessly. This guard is written in both
    /// directions so it is falsifiable: Core must hold the policy, and the App must actually use it
    /// rather than keeping its own copy or its own BeginInvoke path.
    /// </summary>
    [Fact]
    public void CoalescingPolicyLivesInCore_NotInTheApp()
    {
        Assembly core = typeof(FrameCoalescer).Assembly;

        // "System.Windows.Threading" is not an assembly name — DispatcherTimer lives in
        // PresentationFramework — so listing it here would assert nothing. The guard checks the
        // assemblies that actually carry WPF types, the same list ArchitectureInvariantTests uses.
        Assert.All(
            core.GetReferencedAssemblies().Select(name => name.Name ?? string.Empty),
            name => Assert.False(
                name is "PresentationCore" or "PresentationFramework" or "WindowsBase" or "System.Windows.Forms",
                $"Core acquired {name}; the coalescer must stay testable without a UI."));

        FieldInfo? field = typeof(MainWindow).GetField("_coalescer", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        Assert.Equal(typeof(FrameCoalescer), field!.FieldType);
    }

    /// <summary>
    /// Phase 4's acceptance line in §5: "Frame rate is a declared UI setting, not a baud-rate side effect."
    /// A setting the user cannot reach is not a setting, so the control and its handler are checked in the
    /// actual XAML and code-behind. These assertions fail if the control is removed, if its handler is
    /// unwired, or if the per-frame marshalling is reinstated — which is what makes them guards rather than
    /// decoration.
    ///
    /// WHAT PHASE 6b CHANGED HERE, AND WHAT IT DID NOT. The rate's policy moved to MainViewModel; the timer
    /// stayed on the window, per §2 ("the policy (rate, drop counter) belongs on the view model; the timer is
    /// a UI-thread mechanism and can stay with the view"), so the two timer assertions below still read
    /// MainWindow.xaml.cs and still mean what they said. The frame path changed shape: MainWindow no longer
    /// subscribes to the transport itself. MainViewModel subscribes and re-raises the frame as FrameArrived,
    /// and the window's subscription writes it into the coalescer. So the D2 claim — the frame path hands the
    /// frame to the coalescer and returns, without queueing a render — is now asserted against that
    /// subscription instead of against a method called Stream_FrameReceived, which no longer exists in the
    /// window. The claim is unchanged; only the place it is checked has moved.
    /// </summary>
    [Fact]
    public void RenderRateIsAUserVisibleSetting_AndFrameReceivedNoLongerMarshalsPerFrame()
    {
        string appDir = FindAppSourceDirectory();
        string xaml = File.ReadAllText(Path.Combine(appDir, "MainWindow.xaml"));
        string code = File.ReadAllText(Path.Combine(appDir, "MainWindow.xaml.cs"));

        Assert.Contains("RenderRateComboBox", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectionChanged=\"RenderRate_SelectionChanged\"", xaml, StringComparison.Ordinal);

        Assert.Contains("_renderTimer.Tick += RenderTimer_Tick;", code, StringComparison.Ordinal);
        Assert.Contains("_renderTimer.Interval = RenderInterval(", code, StringComparison.Ordinal);

        // The view's frame path: one subscription, one write into the coalescer, nothing else. If
        // Dispatcher.BeginInvoke reappears anywhere in it, D2 is back.
        Assert.Contains("_viewModel.FrameArrived += (_, frame) => _coalescer.Write(frame);", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Dispatcher.BeginInvoke", ExtractMethod(code, "private void RenderTimer_Tick"), StringComparison.Ordinal);

        // And the transport-side half, which since 6b lives in MainViewModel: it forwards the frame and does
        // not touch UI state, which is how a type that cannot hold a Dispatcher still honours
        // ISensorStream's "the consumer marshals" contract.
        string viewModel = File.ReadAllText(Path.Combine(appDir, "MainViewModel.cs"));
        Assert.Contains("private void Stream_FrameReceived(object? sender, SensorFrame frame) => FrameArrived?.Invoke(sender, frame);", viewModel, StringComparison.Ordinal);

        // Stripped before the absence is asserted, and that stripping is the whole point. MainViewModel's own
        // class comment quotes `Dispatcher.BeginInvoke` twice while explaining why this type cannot call it.
        // Asserting DoesNotContain over the raw file trips on that explanation - the hazard
        // ArchitectureInvariantTests records for StripLineComments: a guard that punishes documentation of a
        // removed thing trains everyone to delete the documentation. ExtractMethod cannot be used here either:
        // these three handlers are expression-bodied, so brace matching would run on past them into some other
        // method's body and measure the wrong code entirely. So the assertion measures the file with the prose
        // removed, and the explanation stays where it is.
        string viewModelCode = StripLineComments(viewModel);
        Assert.DoesNotContain("Dispatcher", viewModelCode, StringComparison.Ordinal);

        // The positive half: the status handler forwards rather than marshalling. Without this, the assertion
        // above would pass on a handler that had simply been deleted.
        Assert.Contains("TransportAnnounced?.Invoke", viewModelCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Removes // line comments from source text, so a source-text guard measures code and not prose.
    ///
    /// Duplicated from ArchitectureInvariantTests rather than shared, for the reason that file already
    /// records: making one guard depend on another test class's private helper couples two unrelated guards to
    /// one file's internals.
    ///
    /// THE LIMITATION, stated rather than hidden: this strips from "//" to end of line without parsing, so a
    /// "//" inside a string literal would be mistaken for the start of a comment. MainViewModel.cs contains no
    /// such literal today; if one is added, this over-strips and the guard weakens silently.
    /// </summary>
    private static string StripLineComments(string source)
    {
        var builder = new System.Text.StringBuilder();
        foreach (string line in source.Split('\n'))
        {
            int comment = line.IndexOf("//", StringComparison.Ordinal);
            builder.AppendLine(comment >= 0 ? line[..comment] : line);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Returns the body of a method by brace matching, so assertions can be made about one method
    /// instead of about the whole 780-line file. Without this, "the file contains no BeginInvoke"
    /// would be false anyway — StatusText and rejected lines still marshal through it, correctly.
    /// </summary>
    private static string ExtractMethod(string source, string signatureStart)
    {
        int start = source.IndexOf(signatureStart, StringComparison.Ordinal);
        Assert.True(start >= 0, $"method '{signatureStart}' not found in MainWindow.xaml.cs");

        int open = source.IndexOf('{', start);
        Assert.True(open >= 0, $"no body found for '{signatureStart}'.");

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}' && --depth == 0)
            {
                return source.Substring(start, i - start + 1);
            }
        }

        Assert.Fail($"unbalanced braces after '{signatureStart}'.");
        return string.Empty;
    }

    /// <summary>
    /// 60 Hz is the rate the plan names. Pinned so the default is a decision on the record rather than a
    /// number that happened to be typed.
    ///
    /// Since Phase 6b the rate lives on MainViewModel, which is where §2 says the *policy* belongs ("the
    /// render timer, the FrameCoalescer instance and the render-rate combo box are currently MainWindow state.
    /// The policy (rate, drop counter) belongs on the view model"). MainWindow reads this constant to populate
    /// the combo box and to set the timer's initial interval, so the constant is public on the view model and
    /// Public is in the flags - it was a private const on MainWindow before 6b, and a lookup that still asked
    /// only for NonPublic would report a member that legitimately had to become shared as missing.
    /// </summary>
    [Fact]
    public void DefaultRenderRateIsThePlannedSixtyHertz()
    {
        FieldInfo field = typeof(MainViewModel)
            .GetField("DefaultRenderRateHz", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;

        Assert.Equal(60, (int)field.GetValue(null)!);
    }

    /// <summary>
    /// THE RATE IS ONE NUMBER, DECLARED ONCE. §2's warning about 6b is that splitting the policy from the timer
    /// "wrongly is how the rate ends up as a field on a Window again". A window that declared its own rate
    /// while the view model declared another would pass every guard written before 6b, because each guard
    /// would find the copy it was already looking for. This one looks for the absence of the second copy.
    /// </summary>
    [Fact]
    public void TheRenderRateIsDeclaredOnce_NotOnBothTheViewAndTheViewModel()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;

        Assert.NotNull(typeof(MainViewModel).GetField("DefaultRenderRateHz", flags));
        Assert.Null(typeof(MainWindow).GetField("DefaultRenderRateHz", flags));
        Assert.Null(typeof(MainWindow).GetField("RenderRateChoices", flags));

        // And the view still has to be able to reach the choices, or the toolbar would be empty.
        Assert.NotNull(typeof(MainViewModel).GetField("RenderRateChoices", flags));
    }

    // -----------------------------------------------------------------------------------------
    // §12.8 item 4 — the half of Phase 5 commit 1 did not reach: the per-frame ToArray() copies.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// THE RENDER PATH ALLOCATES NOTHING FOR THE FRAME'S OWN VALUES.
    ///
    /// WHY THIS IS A SOURCE-TEXT GUARD AND NOT A MEASUREMENT. RenderFrame is private on a Window that no
    /// test can construct, and "this method allocates zero bytes per frame" is not observable from a unit
    /// test in a way that would survive the GC. What IS observable, and what would have made the whole
    /// change invisible if left unguarded, is the two calls that used to be here: `frame.Imu.ToArray()` and
    /// `frame.Distances.ToArray()`. SensorFrame stores both as ReadOnlyMemory, so passing them to an int[]
    /// parameter meant copying 9 and 64 integers on every rendered frame purely to satisfy a signature.
    /// Asserting their absence in the extracted method body is the claim that can fail.
    ///
    /// WHY THE DOC COMMENT IS NOT A PROBLEM HERE. ExtractMethod starts at the signature and brace-matches
    /// the body, so RenderFrame's own explanation of what it used to do — which names ToArray() twice — is
    /// not part of what is searched. That is also why the assertion is scoped to one method rather than to
    /// the file: the file legitimately mentions the removed calls in prose.
    /// </summary>
    [Fact]
    public void RenderFrameNoLongerCopiesTheFramesOwnArrays()
    {
        string code = File.ReadAllText(Path.Combine(FindAppSourceDirectory(), "MainWindow.xaml.cs"));

        string renderFrame = ExtractMethod(code, "private void RenderFrame");

        Assert.DoesNotContain(".ToArray()", renderFrame, StringComparison.Ordinal);

        // And positively, that it reads the frame's storage in place rather than having simply deleted the
        // values along with the copies.
        Assert.Contains("frame.Imu.Span", renderFrame, StringComparison.Ordinal);
        Assert.Contains("frame.Distances.Span", renderFrame, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE SIGNATURES THAT MADE THE COPIES NECESSARY ARE GONE.
    ///
    /// Removing ToArray() from RenderFrame alone would not have been a fix: it would have failed to compile
    /// until the consumers stopped asking for int[]. Asserting the parameter types from the compiled
    /// assemblies is what stops the copies reappearing "to fix a build error" — the signature is the reason
    /// they existed, so the signature is what has to stay changed.
    /// </summary>
    [Fact]
    public void TheConsumersTakeSpans_NotArrays()
    {
        MethodInfo updateFrame = typeof(ZoneRenderer).GetMethod("UpdateFrame")!;
        Assert.Equal(typeof(ReadOnlySpan<int>), updateFrame.GetParameters()[0].ParameterType);

        MethodInfo filterUpdate = typeof(OrientationFilter).GetMethod("Update")!;
        Assert.Equal(typeof(ReadOnlySpan<int>), filterUpdate.GetParameters()[0].ParameterType);
    }

    /// <summary>
    /// THE STATUS LINE SAYS THE SAME THING IT SAID BEFORE.
    ///
    /// RenderFrame used to compute the visible-zone count and the maximum distance with LINQ over the array
    /// (`distances.Count(d => ...)` and `distances.Max()`). A span is not IEnumerable, so those became one
    /// explicit loop. The numbers printed are user-visible text, and a loop that gets the boundary wrong
    /// would change the status line while every zone still rendered correctly — a regression a screenshot
    /// would show and no existing test would catch.
    ///
    /// WHAT THIS CAN AND CANNOT PROVE. It cannot execute RenderFrame: it is private on a Window nothing here
    /// can construct. What it does is hold the loop's logic and the LINQ it replaced side by side over the
    /// same inputs, including the boundary values the app actually filters on (MinDistanceMm 20,
    /// MaxDistanceMm 4000) and readings outside them, and assert they agree. If the loop's comparison or its
    /// initial value is changed, the two diverge and this says so. It is a transcription check, not a
    /// substitute for running the app, and it does not claim to have observed the status line.
    /// </summary>
    [Theory]
    // All inside the band, all outside it, both boundaries exactly, and a negative reading.
    // Deliberately NO empty case. On an empty sequence LINQ's Max() throws while the loop leaves maxMm at
    // int.MinValue, so the two sides of the comparison below cannot both be evaluated. The input is also
    // unreachable: IllegalValueCounts_AreRejected pins that every token count except 16, 25, 64 and 73 is
    // rejected, and SensorFrameOverload_AcceptsAndRejectsExactlyWhatTheArrayOverloadDoes pins
    // frame.ZoneCount to distances.Length - so a frame that reaches RenderFrame holds 16 or 64 zones.
    [InlineData(new int[] { 1000, 1000, 1000 })]
    [InlineData(new int[] { 10, 5000 })]
    [InlineData(new int[] { 20, 4000 })]
    [InlineData(new int[] { 19, 20, 4000, 4001 })]
    [InlineData(new int[] { -500, 0, 20 })]
    public void TheStatusLineLoopAgreesWithTheLinqItReplaced(params int[] distances)
    {
        // The band comes from MainWindow, not from constants declared here. A test that hard-codes 20 and
        // 4000 keeps passing after someone changes the app's band and leaves the test's behind - which is
        // the specific way a passing test stops meaning anything.
        (double minMm, double maxMm) = AppVisibleBand();

        int visibleLinq = distances.Count(d => d >= minMm && d <= maxMm);
        int maxLinq = distances.Max();

        ReadOnlySpan<int> span = distances;
        int visibleLoop = 0;
        int maxLoop = int.MinValue;
        for (int i = 0; i < span.Length; i++)
        {
            int mm = span[i];
            if (mm >= minMm && mm <= maxMm)
            {
                visibleLoop++;
            }

            if (mm > maxLoop)
            {
                maxLoop = mm;
            }
        }

        Assert.Equal(visibleLinq, visibleLoop);
        Assert.Equal(maxLinq, maxLoop);
    }

    /// <summary>
    /// The band the status line counts against, pinned to the constants the application declares rather than
    /// to the numbers a test would like them to be. Read by reflection for the same reason
    /// DefaultRenderRateIsThePlannedSixtyHertz does: these are constants on a type, and the only honest way to
    /// assert a constant is to read it from the type that declares it.
    ///
    /// Since Phase 6b that type is MainViewModel, not MainWindow: §2 assigns the status text and its
    /// arithmetic to the view model, and MainWindow now takes its scene constants FROM MainViewModel instead
    /// of declaring its own copy. Reading them by reflection is what makes the test survive that move and
    /// still mean something: it follows the constants to wherever the application keeps them, and a move that
    /// left two copies behind would show up here as two numbers that no longer agree.
    ///
    /// The expected values are asserted here as well as read, so a change to the app's band fails loudly at
    /// the point of change instead of silently moving every comparison in the test above.
    /// </summary>
    [Fact]
    public void TheVisibleBandTheStatusLineCountsAgainstIsTheAppsOwnConstants()
    {
        (double minMm, double maxMm) = AppVisibleBand();

        Assert.Equal(20.0, minMm);
        Assert.Equal(4000.0, maxMm);
    }

    /// <summary>
    /// The application's own MinDistanceMm / MaxDistanceMm, read from the compiled type that declares them.
    ///
    /// Public is in the flags because since Phase 6b these constants are public: MainWindow reads them to
    /// build the zone renderer, so they cannot be private to the type that owns them. Before 6b they were
    /// private consts on MainWindow and this lookup used only NonPublic | Static. The flag change is not a
    /// loosening - it follows the constants to the type that now declares them, and a lookup that still asked
    /// for a private constant would fail on a member that legitimately had to become shared.
    /// </summary>
    private static (double Min, double Max) AppVisibleBand()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;

        FieldInfo min = typeof(MainViewModel).GetField("MinDistanceMm", flags)!;
        FieldInfo max = typeof(MainViewModel).GetField("MaxDistanceMm", flags)!;

        return ((double)min.GetValue(null)!, (double)max.GetValue(null)!);
    }

    /// <summary>
    /// Walks up from the test assembly's output directory to the App project directory. The test
    /// project references the App project, so both bin trees sit under the same repo root.
    /// </summary>
    /// <summary>
    /// Locates the App project's source directory from the test assembly's own location.
    ///
    /// The App assembly is COPIED into the test project's output folder, so its Location points into
    /// EchoEnvironment.Tests\bin\Debug\net10.0-windows — walking up from it visits only bin
    /// directories, EchoEnvironment.Tests, and the repo root, none of which contains MainWindow.xaml.
    /// The first version of this helper did exactly that and failed with a null directory.
    ///
    /// The reliable anchor is this test project's own .csproj: it is copied next to its assembly by
    /// nothing, so it is found by walking up from the repo layout rather than from the bin tree. Its
    /// parent is the repo root, and the App project sits beside it.
    /// </summary>
    private static string FindAppSourceDirectory()
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(typeof(FrameCoalescerTests).Assembly.Location)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EchoEnvironment.Tests.csproj")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        string? repoRoot = directory!.Parent?.FullName;
        Assert.NotNull(repoRoot);

        string appDirectory = Path.Combine(repoRoot!, "EchoEnvironment");
        Assert.True(File.Exists(Path.Combine(appDirectory, "MainWindow.xaml")),
            $"expected the App project at {appDirectory}; the repository layout has moved.");
        Assert.True(File.Exists(Path.Combine(appDirectory, "MainWindow.xaml.cs")),
            $"expected MainWindow.xaml.cs at {appDirectory}.");

        return appDirectory;
    }
}
