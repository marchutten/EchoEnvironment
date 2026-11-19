namespace EchoEnvironment.Core;

/// <summary>
/// A single parsed sensor frame: 16 or 64 zone distances in millimetres, plus optionally 9 IMU counts.
///
/// Introduced in Phase 3 (§9.7: deferred from Phase 2 step 2d) because ISensorStream.FrameReceived
/// (§3b) is the first consumer that needs a single value type to pass around. Doing it here rather
/// than in Phase 2 means it was driven by a real caller instead of being speculative.
///
/// Distances and Imu are backed by SEPARATE arrays, not views over one shared buffer. That matches
/// the pre-existing TryParse(out int[], out int[]) semantics, pinned by
/// DistancesAndImuAreSeparateArrays_NotViewsOverOneBuffer.
///
/// EQUALITY CAVEAT: this is a record struct, so the compiler generates value equality — but
/// ReadOnlyMemory&lt;T&gt;.Equals compares the underlying array reference and span bounds, not the
/// contents. Two frames holding identical values backed by different arrays are NOT equal. Nothing
/// in the current codebase compares frames; Phase 4's coalescing channel drops frames rather than
/// comparing them. If a future consumer needs content equality, add an explicit Equals override
/// rather than assuming the generated one does it.
///
/// Timestamp is new: nothing in the current app consumes it (MainWindow derives dt from
/// Stopwatch). It exists because Phase 4's coalescing stage and Phase 6's replay stream need to
/// know when a frame was captured.
/// </summary>
public readonly record struct SensorFrame(
    ReadOnlyMemory<int> Distances,
    int ZoneCount,
    ReadOnlyMemory<int> Imu,
    bool HasImu,
    DateTimeOffset Timestamp);
