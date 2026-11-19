using EchoEnvironment.Core;

namespace EchoEnvironment.Hardware;

/// <summary>
/// The pose the simulator feeds to the IMU channel. The names and the axis triples are the same
/// poses <c>OrientationFilterTests</c> pins, and the counts are derived the same way (world up and
/// world north expressed in sensor coordinates, scaled so every count is an exact integer). That is
/// what makes this the interactive version of the Phase 1 known-pose tests: feed
/// <see cref="SimulationPose.FlatFacingNorth"/> and the viewport must show the board level and
/// facing north, which is exactly the assertion the test suite already makes numerically.
/// </summary>
public enum SimulationPose
{
    /// <summary>Board flat and level, sensor X east, looking north.</summary>
    FlatFacingNorth,

    /// <summary>Board flat and level, looking east.</summary>
    FlatFacingEast,

    /// <summary>Tilted up 36.87° (3-4-5 triangle, so every count is an exact integer).</summary>
    TiltedUp345,

    /// <summary>Board upside down, still looking north.</summary>
    InvertedFacingNorth,

    /// <summary>A fully general pose with no zero or repeated elements.</summary>
    GeneralPose,
}

/// <summary>
/// Synthetic frames with no hardware attached (Phase 3c) — the highest-leverage item in the plan,
/// because it makes the app developable with no sensor and gives the visualizer something to render.
///
/// The zone channel is a travelling wave across the grid, chosen to stay inside the App's visible
/// band (20 mm to 4000 mm) so zones are actually drawn rather than hidden by the range check. The
/// IMU channel is a fixed, exactly-integer pose, so the orientation filter converges to a pose the
/// test suite already knows the answer for.
///
/// THREADING: frames are raised on a timer thread, never the UI thread — see ISensorStream.
/// </summary>
public sealed class SimulatedSensorStream : ISensorStream
{
    /// <summary>Matches the App's visible distance band, so simulated zones are not filtered out.</summary>
    private const double MinDistanceMm = 20.0;
    private const double MaxDistanceMm = 4000.0;

    private readonly object _gate = new();
    private Timer? _timer;
    private long _frameIndex;
    private bool _open;
    private bool _disposed;

    public event EventHandler<SensorFrame>? FrameReceived;

    public event EventHandler<string>? StatusChanged;

    public event EventHandler<Exception>? Faulted;

    /// <summary>
    /// The simulator generates its own frames, so it has no line to reject and this event never
    /// fires. It is written with explicit accessors rather than as a field-like event for two
    /// reasons: TreatWarningsAsErrors turns CS0067 ("event is never used") into a build failure, and
    /// a simulator that claims it can reject a line would be lying about the contract. The member
    /// exists because ISensorStream requires it, and SensorStreamTests asserts it on every
    /// implementation.
    /// </summary>
    public event EventHandler<string>? LineRejected
    {
        add { }
        remove { }
    }

    /// <summary>16 (4x4) or 64 (8x8). Any other value is rejected at the first frame.</summary>
    public int ZoneCount { get; set; } = 64;

    /// <summary>Milliseconds between frames. Clamped to at least 1.</summary>
    public int IntervalMilliseconds { get; set; } = 10;

    /// <summary>Seconds for the zone wave to complete one cycle.</summary>
    public double SweepPeriodSeconds { get; set; } = 4.0;

    /// <summary>The pose reported on the IMU channel.</summary>
    public SimulationPose Pose { get; set; } = SimulationPose.FlatFacingNorth;

    /// <summary>
    /// Raw gyro counts injected on the gyro channel. Zero keeps the board at rest, which is what
    /// lets the filter converge on <see cref="Pose"/> and stay there. A non-zero value is above the
    /// filter's stationary threshold and drives the estimate away from the declared pose.
    /// </summary>
    public Vec3 GyroCounts { get; set; }

    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return _open;
            }
        }
    }

    /// <summary>Frames emitted so far. The simulator never rejects a line, so this equals frames produced.</summary>
    public long FramesProduced => Interlocked.Read(ref _frameIndex);

    public IReadOnlyList<PortDescriptor> ListPorts() => [new PortDescriptor("SIM")];

    /// <summary>portName and baudRate are ignored: the simulator has no port and its own pacing.</summary>
    public void Open(string portName, int baudRate)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SimulatedSensorStream));
        }

        lock (_gate)
        {
            if (_open)
            {
                throw new InvalidOperationException("The simulator is already running.");
            }

            _open = true;
            _timer = new Timer(Tick, null, 0, Math.Max(1, IntervalMilliseconds));
        }

        StatusChanged?.Invoke(this, $"Simulating {Pose}, {ZoneCount} zones");
    }

    public void Close()
    {
        Timer? timer;
        lock (_gate)
        {
            if (!_open)
            {
                return;
            }

            _open = false;
            timer = _timer;
            _timer = null;
        }

        timer?.Dispose();
    }

    public void Dispose()
    {
        Close();
        _disposed = true;
    }

    private void Tick(object? state)
    {
        if (!IsOpen)
        {
            return;
        }

        try
        {
            int zones = ZoneCount;
            if (zones is not (16 or 64))
            {
                throw new InvalidOperationException($"ZoneCount must be 16 or 64, got {zones}.");
            }

            long frame = Interlocked.Increment(ref _frameIndex);
            int[] distances = new int[zones];
            double phase = 2 * Math.PI * frame * IntervalMilliseconds / 1000.0 / SweepPeriodSeconds;

            for (int i = 0; i < zones; i++)
            {
                double wave = 0.5 + 0.5 * Math.Sin(phase + 2 * Math.PI * i / zones);
                distances[i] = (int)(MinDistanceMm + wave * (MaxDistanceMm - MinDistanceMm));
            }

            int[] imu = ImuCounts();
            FrameReceived?.Invoke(this, new SensorFrame(distances, zones, imu, HasImu: true, DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            Faulted?.Invoke(this, ex);
        }
    }

    /// <summary>
    /// accel = world up in sensor coordinates, mag = world north in sensor coordinates, scaled so
    /// every count is an exact integer. Same derivation as OrientationFilterTests.PoseFrame: a world
    /// vector v appears with component v_j = (sensor axis j) . v, and north is world -Z.
    /// </summary>
    private int[] ImuCounts()
    {
        (Vec3 x, Vec3 y, Vec3 z, double scale) = Pose switch
        {
            SimulationPose.FlatFacingNorth => (new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1), 500.0),
            SimulationPose.FlatFacingEast => (new Vec3(0, 0, 1), new Vec3(0, 1, 0), new Vec3(-1, 0, 0), 500.0),
            SimulationPose.TiltedUp345 => (new Vec3(1, 0, 0), new Vec3(0, 0.8, 0.6), new Vec3(0, -0.6, 0.8), 500.0),
            SimulationPose.InvertedFacingNorth => (new Vec3(-1, 0, 0), new Vec3(0, -1, 0), new Vec3(0, 0, 1), 500.0),
            SimulationPose.GeneralPose => (new Vec3(2.0 / 3, 1.0 / 3, 2.0 / 3), new Vec3(-2.0 / 3, 2.0 / 3, 1.0 / 3), new Vec3(-1.0 / 3, -2.0 / 3, 2.0 / 3), 1500.0),
            _ => throw new InvalidOperationException($"Unknown pose: {Pose}."),
        };

        Vec3 accel = new Vec3(x.Y, y.Y, z.Y) * scale;
        Vec3 mag = new Vec3(-x.Z, -y.Z, -z.Z) * scale;

        return
        [
            (int)Math.Round(GyroCounts.X), (int)Math.Round(GyroCounts.Y), (int)Math.Round(GyroCounts.Z),
            (int)Math.Round(accel.X), (int)Math.Round(accel.Y), (int)Math.Round(accel.Z),
            (int)Math.Round(mag.X), (int)Math.Round(mag.Y), (int)Math.Round(mag.Z),
        ];
    }
}
