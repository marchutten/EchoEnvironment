using System.Threading;
using System.Threading.Channels;

namespace EchoEnvironment.Core;

/// <summary>
/// The Phase 4 coalescing stage: a one-slot hand-off between the transport thread and the UI timer,
/// sized so that a producer which outruns the UI replaces the stale frame instead of queueing behind
/// it (defect D2).
///
/// WHAT D2 WAS. MainWindow turned every parsed line into its own Dispatcher.BeginInvoke, and each
/// one performed a full frame render. The dispatcher queue therefore grew at the frame arrival rate
/// and drained at whatever rate a full render happened to take. When the producer was faster than the
/// renderer - which is what 115200 baud exists for - the queue grew without bound and the latency of
/// what the user saw grew with it. The fix is not a faster render. It is refusing to accept more than
/// one frame's worth of pending work, so the queue depth is a constant and latency is bounded by it.
///
/// DEVIATION FROM THE PLAN, AND THE PLAN WAS WRONG HERE. Phase 4's sketch writes
/// <c>FullMode = BoundedChannelFullMode.DropWrite</c> with the comment "newest frame wins". Those two
/// contradict each other. DropWrite discards the item being WRITTEN, so a full channel keeps the frame
/// it already holds - the stale one - and throws away the fresh one. "Newest frame wins" is
/// <see cref="BoundedChannelFullMode.DropOldest"/> (the pre-.NET 8 name for the same behaviour is
/// DropRead), which discards the frame already in the channel to make room for the new one. The mode
/// used here is DropOldest, and
/// <c>FrameCoalescerTests.KeepsTheNewestFrame_DiscardsTheStaleOne</c> is the test that would have
/// failed had the sketch been copied. It was chosen by observed behaviour, not by the enum's name.
///
/// DEVIATION FROM THE PLAN: the class lives in Core, not in the App as the sketch says. The
/// migration's whole premise is that behaviour must be testable headlessly - Core is net10.0 with no
/// WPF reference, enforced by ArchitectureInvariantTests - and D2 is a behaviour fix. A coalescer
/// written inside MainWindow could only be checked by running the app against a sensor at max baud,
/// which is a hardware-only check and therefore a check that does not exist in the suite. The policy
/// is in Core; MainWindow keeps only the wiring.
/// <c>CoalescingPolicyLivesInCore_NotInTheApp</c> pins that boundary in both directions.
///
/// WHY A CHANNEL RATHER THAN A LOCKED FIELD. A capacity-1 bounded channel with drop-on-overwrite is
/// exactly the slot this needs, and it is the shape the plan asked for. It is not used asynchronously:
/// MainWindow's DispatcherTimer calls <see cref="TryRead"/> synchronously on the UI thread.
/// </summary>
public sealed class FrameCoalescer
{
    private readonly Channel<SensorFrame> _channel = Channel.CreateBounded<SensorFrame>(
        new BoundedChannelOptions(1)
        {
            // Newest frame wins: a write to a full channel discards the frame already waiting.
            FullMode = BoundedChannelFullMode.DropOldest,

            // The UI timer is the only reader, so a read cannot race another read.
            SingleReader = true,

            // SerialPort raises DataReceived on thread-pool threads rather than one dedicated thread,
            // so two writes can in principle overlap. Declaring this false is what makes the
            // Interlocked counter below necessary rather than decorative.
            SingleWriter = false,
        });

    private long _written;
    private long _read;
    private long _cleared;

    /// <summary>
    /// Hands a frame to the UI. Never blocks, never fails, never throws: this is called from the
    /// transport callback, and a producer that can block the transport is a defect in its own right.
    /// </summary>
    public void Write(SensorFrame frame)
    {
        // The return value is deliberately ignored. With DropOldest it is always true, so a drop
        // counter built on "TryWrite returned false" would report zero forever and read like
        // protection while asserting nothing.
        _channel.Writer.TryWrite(frame);
        Interlocked.Increment(ref _written);
    }

    /// <summary>
    /// Takes the newest pending frame, or returns false when none is waiting. Called from the UI
    /// thread by the render timer.
    /// </summary>
    public bool TryRead(out SensorFrame frame)
    {
        if (!_channel.Reader.TryRead(out frame))
        {
            frame = default;
            return false;
        }

        // Plain increment, not Interlocked: SingleReader is true and Clear is only ever called from
        // the same UI thread, so there is no competing writer of this counter.
        _read++;
        return true;
    }

    /// <summary>
    /// Discards whatever is waiting without counting it as a drop. MainWindow calls this on
    /// disconnect so a frame left over from the previous connection is never rendered as if it were
    /// fresh. It is tracked separately from <see cref="DroppedFrames"/> because it was discarded on
    /// purpose, not coalesced away: folding it in would make the drop counter lie about the policy.
    /// </summary>
    public void Clear()
    {
        while (_channel.Reader.TryRead(out _))
        {
            _cleared++;
        }
    }

    /// <summary>
    /// Frames waiting to be rendered. Can never exceed 1, and that bound is the entire fix: queue
    /// depth is what latency is made of.
    /// </summary>
    public long Pending => _channel.Reader.Count;

    /// <summary>
    /// Frames that arrived and were replaced before anything could render them. This is the number
    /// Phase 4's gate asks to be visible in StatusText: a drop policy nobody can see looks like a
    /// stuttering UI with no explanation attached.
    ///
    /// Computed as an identity rather than counted at the moment of dropping, because with DropOldest
    /// the discard happens inside TryWrite and is not observable from outside it:
    /// written = read + cleared + pending + dropped.
    ///
    /// THE CONSEQUENCE OF THAT FORM: the identity alone proves nothing about this number, because the
    /// identity holds by construction however wrong the three counters become. So the value is pinned
    /// in tests by hand-computed expectations from specific sequences instead — see
    /// FrameCoalescerTests.DroppedFramesCountsOnlyCoalescedAwayFrames_NotClearedOnes and
    /// ConcurrentWritersKeepTheBoundAndTheCountersExact.
    /// </summary>
    public long DroppedFrames => Interlocked.Read(ref _written) - _read - _cleared - Pending;
}
