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
///
/// DEFECT D4, FIXED HERE (MIGRATION_PLAN 7). RenderMapIfDue used to rebuild every distance bucket from
/// scratch on every throttle tick, by walking all accumulated entries. The entry count is bounded by the
/// whole-degree grid, 361 azimuth slots x 181 elevation slots = 65,341 cells, and each stamped cube emits
/// eight positions, so a full rebuild is on the order of half a million positions re-emitted every 150 ms
/// - regardless of whether a single reading had changed. A static scene with Keep-map on paid the full
/// cost forever.
///
/// THE FIX IS AN INDEX, NOT A HEURISTIC. _bucketCells is a second view over the same entries: bucket ->
/// cell -> position. It makes "which readings are in bucket 7" answerable without scanning the map, so a
/// rebuild can touch only the buckets that actually changed. It is not a cache of the last result and it
/// does not approximate staleness: the geometry it produces is built from exactly the same entries the
/// full rebuild would have used.
///
/// WHY A SECOND INDEX AND NOT A SCAN. A scan that skips unchanged buckets still has to look at every entry
/// to know that, which is the O(entries) cost D4 is about. The index makes the unchanged case O(1) per
/// Add and O(changed buckets) per rebuild. The cost is honest and stated: the same position is stored
/// twice, once in _entries and once in _bucketCells, bounded by the same 65,341 cells. That is a memory
/// trade for a per-tick CPU saving, and for a map that is redrawn eight times a second it is the right
/// direction.
///
/// WHY "UNCHANGED" IS DECIDED BY EXACT COMPONENT COMPARISON. A reading that lands in the same cell with
/// the same position and the same bucket marks nothing dirty, and that is what makes a static scene free.
/// The comparison is exact rather than tolerant, and the failure mode of that choice is the harmless one:
/// a position differing in the last bits marks its bucket dirty and costs one rebuild that a tolerance
/// would have skipped. A tolerance, by contrast, could mark a genuinely moved reading clean and leave a
/// stale cube on screen - a wrong picture, which is worse than an extra rebuild.
/// </summary>
public sealed class WorldMap
{
    /// <summary>
    /// Width of the elevation axis in the packed key. asin returns [-90, 90], so el + 90 occupies
    /// 0..180 - exactly 181 values - which is what makes (az + 180) * 181 + (el + 90) collision-free.
    /// </summary>
    private const int ElevationSlots = 181;

    private readonly Dictionary<int, (Vec3 Position, int Bucket)> _entries = [];

    /// <summary>
    /// bucket -> cell key -> position. The index that makes a per-bucket rebuild possible without
    /// scanning every entry. Keyed by the same cell key as _entries, so a cell overwrite is O(1) in both
    /// views and the two can never disagree about which bucket a cell belongs to.
    /// </summary>
    private readonly Dictionary<int, Dictionary<int, Vec3>> _bucketCells = [];

    private readonly HashSet<int> _dirtyBuckets = [];

    /// <summary>
    /// Returned instead of a new empty dictionary when a bucket has no cells, so the common
    /// "this bucket is empty" answer allocates nothing.
    /// </summary>
    private static readonly IReadOnlyDictionary<int, Vec3> EmptyBucket = new Dictionary<int, Vec3>();

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
        int cell = (az + 180) * ElevationSlots + el + 90;

        if (_entries.TryGetValue(cell, out (Vec3 Position, int Bucket) existing))
        {
            if (existing.Bucket == bucket && SamePosition(existing.Position, world))
            {
                // The same reading in the same cell: nothing about the drawn map changes, so nothing is
                // marked dirty. This is the case that makes a static scene cost nothing per frame.
                return;
            }

            if (existing.Bucket != bucket)
            {
                // The reading moved between distance buckets, so BOTH buckets now draw something
                // different. Forgetting the old one is the specific bug this branch exists to prevent:
                // the cube would stay stamped in the bucket it left.
                if (_bucketCells.TryGetValue(existing.Bucket, out Dictionary<int, Vec3>? leaving))
                {
                    leaving.Remove(cell);
                }

                _dirtyBuckets.Add(existing.Bucket);
                _dirtyBuckets.Add(bucket);
            }
            else
            {
                _dirtyBuckets.Add(bucket);
            }
        }
        else
        {
            _dirtyBuckets.Add(bucket);
        }

        _entries[cell] = (world, bucket);

        if (!_bucketCells.TryGetValue(bucket, out Dictionary<int, Vec3>? cells))
        {
            cells = [];
            _bucketCells[bucket] = cells;
        }

        cells[cell] = world;
    }

    public void Clear()
    {
        _entries.Clear();
        _bucketCells.Clear();
        _dirtyBuckets.Clear();
    }

    /// <summary>
    /// The cells belonging to one distance bucket, keyed by the same cell key <see cref="Entries"/> uses.
    /// An empty bucket returns a shared empty dictionary rather than allocating one.
    /// </summary>
    public IReadOnlyDictionary<int, Vec3> CellsInBucket(int bucket) =>
        _bucketCells.TryGetValue(bucket, out Dictionary<int, Vec3>? cells) ? cells : EmptyBucket;

    /// <summary>
    /// Consumes the dirty set: returns the buckets whose contents changed since the last call, and clears
    /// the set.
    ///
    /// Consumes rather than reports, deliberately. A getter that leaves the set populated lets a caller
    /// mark a bucket clean after rendering it, which is a two-step protocol that can half-finish: a render
    /// that throws after clearing bucket 3 but before bucket 9 leaves bucket 9 permanently unredrawn.
    /// Taking the whole set in one call makes the rebuild either take its work or not take it, and a
    /// failed rebuild leaves everything dirty for the next tick.
    /// </summary>
    public int[] TakeDirtyBuckets()
    {
        if (_dirtyBuckets.Count == 0)
        {
            return [];
        }

        var taken = new int[_dirtyBuckets.Count];
        _dirtyBuckets.CopyTo(taken);
        _dirtyBuckets.Clear();
        return taken;
    }

    /// <summary>True when at least one bucket has changed since the last <see cref="TakeDirtyBuckets"/>.</summary>
    public bool HasDirtyBuckets => _dirtyBuckets.Count > 0;

    /// <summary>
    /// Exact component comparison. Vec3 defines no == operator and no Equals override, so the default
    /// value-type comparison is what a `==` here would silently fall back to. Naming the three components
    /// says what is being compared and does not depend on ValueType's reflection path.
    /// </summary>
    private static bool SamePosition(in Vec3 a, in Vec3 b) =>
        a.X == b.X && a.Y == b.Y && a.Z == b.Z;
}
