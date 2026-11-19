namespace EchoEnvironment.Core;

/// <summary>
/// The accumulated world map: the latest reading per whole degree of world azimuth and elevation,
/// bucketed by distance.
///
/// Extracted from MainWindow.AddToMap at Phase 5 commit 2 (MIGRATION_PLAN 5 maps that row to
/// Core/WorldMap.cs). The arithmetic is copied element-for-element; the only thing that changed is the
/// position type, from WPF's Point3D to Core's Vec3, because Core compiles as plain net10.0 and cannot
/// name a PresentationCore type.
///
/// The reason to put this in Core is not the file location. AddToMap was a private method of a Window,
/// so no test could ever reach it: "keeps only the latest reading per whole degree" was a comment, not
/// something anyone had observed. In Core it is reachable, and WorldMapTests is the first test that has
/// ever exercised this arithmetic.
///
/// maxDistanceMm and buckets are constructor parameters rather than constants, for the same reason
/// ColorScale takes maxDistance: the arithmetic can be tested at any scale. MainWindow passes its own
/// MaxDistanceMm and MapBuckets, which is what the map was always calibrated to.
/// </summary>
public sealed class WorldMap
{
    /// <summary>
    /// Width of the elevation axis in the packed key. asin returns [-90, 90], so el + 90 occupies
    /// 0..180 - exactly 181 values - which is what makes (az + 180) * 181 + (el + 90) collision-free.
    /// </summary>
    private const int ElevationSlots = 181;

    private readonly Dictionary<int, (Vec3 Position, int Bucket)> _entries = [];

    public WorldMap(double maxDistanceMm, int buckets)
    {
        MaxDistanceMm = maxDistanceMm;
        Buckets = buckets;
    }

    public double MaxDistanceMm { get; }

    public int Buckets { get; }

    public int Count => _entries.Count;

    public IReadOnlyDictionary<int, (Vec3 Position, int Bucket)> Entries => _entries;

    /// <summary>
    /// Records one reading at a world position, overwriting any earlier reading that lands in the same
    /// whole-degree cell.
    ///
    /// Azimuth is measured from north: atan2(X, -Z), because the world frame is X = east, Y = up,
    /// -Z = north (WorldFrame). A point due north gives 0, due east gives 90, due south gives 180.
    /// Elevation is asin(Y / |position|), so straight up is 90.
    ///
    /// The zero-length case is rejected rather than computed. In MainWindow this arithmetic was only
    /// ever reached with mm >= MinDistanceMm, so the radius was always positive and a divide by zero
    /// was unreachable. Promoting it to public Core API makes that case reachable by a caller, and the
    /// unguarded version produced elevation = (int)Round(Asin(NaN)) - a silently meaningless cell rather
    /// than a failure. Failing loudly is the right default for a precondition a caller can violate.
    /// </summary>
    public void Add(in Vec3 world, int mm)
    {
        double r = world.Length;
        if (r == 0)
        {
            throw new ArgumentException(
                "A world position at the origin has no azimuth or elevation.",
                nameof(world));
        }

        int az = (int)Math.Round(Math.Atan2(world.X, -world.Z) * 180.0 / Math.PI);
        int el = (int)Math.Round(Math.Asin(Math.Clamp(world.Y / r, -1, 1)) * 180.0 / Math.PI);
        int bucket = (int)Math.Round(Math.Clamp(mm / MaxDistanceMm, 0, 1) * (Buckets - 1));
        _entries[(az + 180) * ElevationSlots + el + 90] = (world, bucket);
    }

    public void Clear() => _entries.Clear();
}
