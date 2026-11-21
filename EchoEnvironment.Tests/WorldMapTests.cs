using EchoEnvironment.Core;
using Xunit;

namespace EchoEnvironment.Tests;

/// <summary>
/// Tests for Core's WorldMap, extracted from MainWindow.AddToMap at Phase 5 commit 2.
///
/// AddToMap was a private method of a Window, so nothing had ever tested it: "keeps only the latest
/// reading per whole degree" was a comment nobody had observed. These are the first tests to exercise
/// that arithmetic, which is the actual reason the type moved to Core rather than to Rendering.
///
/// No WPF here at all - WorldMap is Core, and these tests are the proof that the map arithmetic is
/// testable without a UI.
/// </summary>
public class WorldMapTests
{
    private const double MaxMm = 4000.0;
    private const int Buckets = 16;

    private static WorldMap New() => new(MaxMm, Buckets);

    /// <summary>
    /// Azimuth is measured from north, because the world frame is X = east, Y = up, -Z = north
    /// (WorldFrame). The exact keys are asserted rather than just "they differ": a sign flip in
    /// atan2(world.X, -world.Z) would otherwise still produce a plausible-looking map, mirrored.
    /// </summary>
    [Fact]
    public void CardinalDirectionsUseANorthBasedAzimuth()
    {
        WorldMap map = New();

        map.Add(new Vec3(0, 0, -1), 1000);   // due north
        map.Add(new Vec3(1, 0, 0), 1000);    // due east
        map.Add(new Vec3(0, 0, 1), 1000);    // due south
        map.Add(new Vec3(-1, 0, 0), 1000);   // due west

        Assert.Equal(4, map.Count);

        Assert.True(map.Entries.ContainsKey(Key(0, 0)), "due north must be azimuth 0.");
        Assert.True(map.Entries.ContainsKey(Key(90, 0)), "due east must be azimuth +90.");
        Assert.True(map.Entries.ContainsKey(Key(180, 0)), "due south must be azimuth 180.");
        Assert.True(map.Entries.ContainsKey(Key(-90, 0)), "due west must be azimuth -90.");
    }

    /// <summary>
    /// Straight up and straight down both have an undefined azimuth, so an azimuth-only key would merge
    /// them into one cell. They must not share a cell, and they do not.
    ///
    /// The azimuth they land on is 180, not 0, and that is not a mistake in the test: the code computes
    /// atan2(world.X, -world.Z), and for a point on the Y axis that is atan2(+0.0, -0.0). IEEE 754
    /// defines atan2(+0, -0) as +pi, so a point straight up is filed as azimuth 180 - due south. The
    /// signed zero in -world.Z decides the cell.
    /// </summary>
    [Fact]
    public void StraightUpAndStraightDownAreDifferentCells()
    {
        WorldMap map = New();

        map.Add(new Vec3(0, 1, 0), 1000);
        map.Add(new Vec3(0, -1, 0), 1000);

        Assert.Equal(2, map.Count);
        Assert.True(map.Entries.ContainsKey(Key(180, 90)), "straight up must be elevation +90.");
        Assert.True(map.Entries.ContainsKey(Key(180, -90)), "straight down must be elevation -90.");
    }

    /// <summary>
    /// The consequence of the signed zero above, pinned rather than discovered later: the same geometric
    /// direction reaches two different cells depending on whether X is +0.0 or -0.0, because
    /// atan2(-0, -0) is -pi and atan2(+0, -0) is +pi, and -180 and +180 pack to different keys.
    ///
    /// UNREACHABLE IN PRODUCTION, and stated as such rather than presented as a live bug: UpdateFrame
    /// only ever calls this at elevations of +/-19.6875 degrees (the 8x8 fan at 45 degrees field of view),
    /// so world.Z is never exactly zero and the -0 case never arises. It is pinned because Add is now
    /// public Core API, and a future caller with a genuinely vertical reading would hit it.
    /// </summary>
    [Fact]
    public void VerticalDirectionSplitsOnTheSignOfZeroInX()
    {
        WorldMap map = New();

        map.Add(new Vec3(0.0, 1, 0), 1000);
        map.Add(new Vec3(-0.0, 1, 0), 1000);

        Assert.Equal(2, map.Count);
        Assert.True(map.Entries.ContainsKey(Key(180, 90)), "+0.0 in X must file as azimuth +180.");
        Assert.True(map.Entries.ContainsKey(Key(-180, 90)), "-0.0 in X must file as azimuth -180.");
    }

    /// <summary>
    /// The behaviour the type is named for: a cell holds one reading, and the newest one wins.
    /// </summary>
    [Fact]
    public void KeepsTheLatestReadingPerWholeDegreeCell()
    {
        WorldMap map = New();

        map.Add(new Vec3(0, 0, -1), 1000);
        map.Add(new Vec3(0, 0, -2), 3000);

        Assert.Equal(1, map.Count);

        (Vec3 position, int _) = map.Entries[Key(0, 0)];
        AssertMath.Close(-2, position.Z, AssertMath.Exact, "the later reading's position must win");
    }

    /// <summary>
    /// "Whole degree" means a reading 0.4 degrees from an earlier one overwrites it rather than
    /// opening a second cell. Without this, the map would grow without bound as the sensor moved.
    /// </summary>
    [Fact]
    public void ReadingsLessThanHalfADegreeApartShareACell()
    {
        WorldMap map = New();

        map.Add(new Vec3(0, 0, -1), 1000);
        map.Add(new Vec3(Math.Sin(0.4 * Math.PI / 180.0), 0, -Math.Cos(0.4 * Math.PI / 180.0)), 1000);

        Assert.Equal(1, map.Count);
    }

    /// <summary>The other side of the same rounding rule: a full degree apart is a different cell.</summary>
    [Fact]
    public void ReadingsOneDegreeApartAreDifferentCells()
    {
        WorldMap map = New();

        map.Add(new Vec3(0, 0, -1), 1000);
        map.Add(new Vec3(Math.Sin(1.0 * Math.PI / 180.0), 0, -Math.Cos(1.0 * Math.PI / 180.0)), 1000);

        Assert.Equal(2, map.Count);
    }

    /// <summary>
    /// The bucket index clamps at both ends. Unclamped, a reading above maxDistance would index past
    /// the end of the bucket array in RenderMapIfDue and throw - a crash reachable from a sensor
    /// reading slightly over range.
    /// </summary>
    [Fact]
    public void BucketIsClampedAtBothEnds()
    {
        WorldMap map = New();

        map.Add(new Vec3(0, 0, -1), 0);
        Assert.Equal(0, map.Entries[Key(0, 0)].Bucket);

        map.Clear();
        map.Add(new Vec3(0, 0, -1), 3 * (int)MaxMm);
        Assert.Equal(Buckets - 1, map.Entries[Key(0, 0)].Bucket);

        map.Clear();
        map.Add(new Vec3(0, 0, -1), -500);
        Assert.Equal(0, map.Entries[Key(0, 0)].Bucket);
    }

    /// <summary>
    /// The bucket count is the constructor's, not a constant copied from MainWindow. This is what
    /// lets the arithmetic be tested at a different scale than the app happens to use.
    /// </summary>
    [Fact]
    public void BucketCountComesFromTheConstructor()
    {
        var map = new WorldMap(1000.0, 4);

        map.Add(new Vec3(0, 0, -1), 1000);

        Assert.Equal(3, map.Entries[Key(0, 0)].Bucket);
        Assert.Equal(4, map.Buckets);
        Assert.Equal(1000.0, map.MaxDistanceMm);
    }

    /// <summary>
    /// In MainWindow this arithmetic was only reached with mm >= MinDistanceMm, so the radius was
    /// always positive and the divide by zero in asin(Y / r) was unreachable. Promoting Add to public
    /// Core API makes it reachable, so it fails loudly instead of storing a meaningless cell:
    /// (int)Round(Asin(NaN)) is a valid-looking key computed from a non-number.
    /// </summary>
    [Fact]
    public void OriginIsRejectedRatherThanProducingANaNCell()
    {
        WorldMap map = New();

        ArgumentException ex = Assert.Throws<ArgumentException>(() => map.Add(new Vec3(0, 0, 0), 1000));
        Assert.Equal("world", ex.ParamName);
        Assert.Equal(0, map.Count);
    }

    [Fact]
    public void ClearEmptiesTheMap()
    {
        WorldMap map = New();

        map.Add(new Vec3(0, 0, -1), 1000);
        map.Add(new Vec3(1, 0, 0), 1000);
        Assert.Equal(2, map.Count);

        map.Clear();
        Assert.Equal(0, map.Count);
        Assert.Empty(map.Entries);
    }

    // -----------------------------------------------------------------------------------------
    // Defect D4 (MIGRATION_PLAN 7): the map must know which buckets changed, so a rebuild does not
    // re-emit every bucket on every tick.
    //
    // WHY THESE TESTS EXIST AT ALL. The 324 tests that already passed proved the D4 rewrite did not
    // break the map's arithmetic. They could not prove the rewrite does anything: RenderMapIfDue's cost
    // is not observable through Count, Entries or Clear. A fix that changed nothing observable would
    // have landed green, which is exactly the failure mode §4's "a test that asserts nothing is mistaken
    // for protection" entry exists to warn about. These assert the dirty set directly.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// THE CASE D4 IS ABOUT: a board that has not moved must mark nothing dirty.
    ///
    /// This is the whole saving. A stationary board with Keep-map on used to re-emit every accumulated
    /// cube every 150 ms forever. If a repeated identical reading marked a bucket dirty, the incremental
    /// rebuild would still walk the map on every tick and D4 would only have been renamed.
    /// </summary>
    [Fact]
    public void ARepeatedIdenticalReadingMarksNothingDirty()
    {
        WorldMap map = New();

        map.Add(new Vec3(0, 0, -1), 1000);
        Assert.True(map.HasDirtyBuckets, "the first reading must mark its bucket dirty.");
        Assert.NotEmpty(map.TakeDirtyBuckets());

        map.Add(new Vec3(0, 0, -1), 1000);

        Assert.False(map.HasDirtyBuckets,
            "the same reading in the same cell changes nothing about the drawn map, so it must not schedule a rebuild.");
        Assert.Empty(map.TakeDirtyBuckets());

        // And the reading is still recorded exactly once.
        Assert.Equal(1, map.Count);
    }

    /// <summary>
    /// A reading that genuinely moved must mark its bucket dirty, or the cube on screen would go stale.
    ///
    /// The pair with the test above is what makes both falsifiable: one asserts the clean case is clean,
    /// the other asserts the dirty case is dirty. Either alone would pass against a build that always
    /// marked everything dirty, or against one that never marked anything.
    /// </summary>
    [Fact]
    public void AChangedPositionInAnExistingCellMarksItsBucketDirty()
    {
        WorldMap map = New();

        map.Add(new Vec3(0, 0, -1), 1000);
        map.TakeDirtyBuckets();

        // Same cell (due north, elevation 0), same bucket, different distance.
        map.Add(new Vec3(0, 0, -2), 1000);

        int[] dirty = map.TakeDirtyBuckets();
        Assert.Contains(BucketOf(1000), dirty);
        Assert.Single(dirty);
    }

    /// <summary>
    /// THE SPECIFIC BUG THIS BRANCH EXISTS TO PREVENT: a reading that moves between distance buckets must
    /// mark BOTH buckets dirty.
    ///
    /// The bucket a reading leaves has to be redrawn without it, or the cube stays stamped in the bucket it
    /// no longer belongs to - a map that shows the same reading at two distances. A rebuild that only marks
    /// the destination dirty produces exactly that, and it is invisible in Count and in Entries, because
    /// both are correct; only the drawn geometry is wrong.
    /// </summary>
    [Fact]
    public void AReadingThatMovesBetweenBucketsMarksTheBucketItLeaves_AndTheOneItEnters()
    {
        WorldMap map = New();

        int leaving = BucketOf(1000);
        int entering = BucketOf(3000);
        Assert.NotEqual(leaving, entering);

        map.Add(new Vec3(0, 0, -1), 1000);
        map.TakeDirtyBuckets();

        // Same cell (due north, elevation 0), different distance bucket.
        map.Add(new Vec3(0, 0, -2), 3000);

        int[] dirty = map.TakeDirtyBuckets();

        Assert.Contains(leaving, dirty);
        Assert.Contains(entering, dirty);
        Assert.Equal(2, dirty.Length);

        // And the map itself is consistent: one cell, filed in the new bucket.
        Assert.Equal(1, map.Count);
        Assert.Equal(entering, map.Entries[Key(0, 0)].Bucket);
    }

    /// <summary>
    /// TakeDirtyBuckets CONSUMES. A getter that left the set populated would let a caller mark a bucket
    /// clean as a separate step, and that two-step protocol can half-finish: a rebuild that throws after
    /// taking bucket 4 but before bucket 11 would leave bucket 11 permanently unredrawn.
    /// </summary>
    [Fact]
    public void TakeDirtyBucketsClearsTheSet_SoTheSameChangeIsNotRebuiltTwice()
    {
        WorldMap map = New();

        map.Add(new Vec3(0, 0, -1), 1000);

        int[] first = map.TakeDirtyBuckets();
        Assert.NotEmpty(first);

        Assert.Empty(map.TakeDirtyBuckets());
        Assert.False(map.HasDirtyBuckets);
    }

    /// <summary>
    /// Clear must clear the dirty set too. A map emptied but still marked dirty would schedule a rebuild
    /// that emits nothing, and - worse - a caller that cleared and then took the dirty buckets would find
    /// work left over from readings that no longer exist.
    /// </summary>
    [Fact]
    public void ClearAlsoClearsTheDirtySet()
    {
        WorldMap map = New();

        map.Add(new Vec3(0, 0, -1), 1000);
        map.Add(new Vec3(1, 0, 0), 3000);
        Assert.True(map.HasDirtyBuckets);

        map.Clear();

        Assert.False(map.HasDirtyBuckets);
        Assert.Empty(map.TakeDirtyBuckets());
        Assert.Empty(map.Entries);
    }

    /// <summary>
    /// The bucket index must agree with Entries, cell for cell.
    ///
    /// WHY THIS IS NOT REDUNDANT. RenderMapIfDue now builds geometry from CellsInBucket rather than from
    /// Entries. If the two views disagreed about which bucket a cell belongs to, the map would draw a cube
    /// in the wrong colour - the bucket index IS the colour - while every Entries assertion still passed.
    /// The two views are the same data reached two ways, and that is exactly the kind of duplication that
    /// drifts.
    /// </summary>
    [Fact]
    public void CellsInBucketAgreesWithEntriesForEveryCell()
    {
        WorldMap map = New();

        map.Add(new Vec3(0, 0, -1), 1000);   // due north
        map.Add(new Vec3(1, 0, 0), 1000);    // due east
        map.Add(new Vec3(0, 0, 1), 3000);    // due south, different bucket

        Assert.Equal(3, map.Count);

        foreach ((int cell, (Vec3 position, int bucket)) in map.Entries)
        {
            IReadOnlyDictionary<int, Vec3> cells = map.CellsInBucket(bucket);

            Assert.True(cells.ContainsKey(cell),
                $"cell {cell} is filed in bucket {bucket} by Entries but is absent from CellsInBucket({bucket}).");
            AssertMath.Close(position.X, cells[cell].X, AssertMath.Exact, $"cell {cell}: X agrees between the two views");
            AssertMath.Close(position.Y, cells[cell].Y, AssertMath.Exact, $"cell {cell}: Y agrees between the two views");
            AssertMath.Close(position.Z, cells[cell].Z, AssertMath.Exact, $"cell {cell}: Z agrees between the two views");
        }
    }

    /// <summary>
    /// A bucket with no readings answers as empty, and an empty bucket must not allocate a dictionary.
    ///
    /// The allocation half matters because RenderMapIfDue asks this question for every dirty bucket on
    /// every tick, and most buckets are usually empty: the app uses 16 buckets and a single-distance frame
    /// fills one.
    /// </summary>
    [Fact]
    public void AnEmptyBucketAnswersAsEmpty()
    {
        WorldMap map = New();

        map.Add(new Vec3(0, 0, -1), 1000);

        Assert.Empty(map.CellsInBucket(0));
        Assert.Empty(map.CellsInBucket(Buckets - 1));

        // The same empty answer every time, which is what "allocates nothing" means here.
        Assert.Same(map.CellsInBucket(0), map.CellsInBucket(1));
    }

    /// <summary>
    /// Moving a reading out of a bucket must remove it from that bucket's cells, not merely add it to
    /// another. Without this the old cube stays stamped in the bucket it left and the map shows the same
    /// reading at two distances.
    /// </summary>
    [Fact]
    public void AReadingThatMovesBucketsIsNoLongerInBucketItLeft()
    {
        WorldMap map = New();

        int leaving = BucketOf(1000);
        int entering = BucketOf(3000);

        map.Add(new Vec3(0, 0, -1), 1000);
        Assert.NotEmpty(map.CellsInBucket(leaving));

        map.Add(new Vec3(0, 0, -2), 3000);

        Assert.Empty(map.CellsInBucket(leaving));
        Assert.NotEmpty(map.CellsInBucket(entering));
    }

    /// <summary>
    /// The key packing WorldMap documents: (az + 180) * 181 + (el + 90). Written here rather than
    /// read from the implementation so the tests assert the documented convention instead of
    /// restating whatever the code currently does.
    /// </summary>
    private static int Key(int azimuthDegrees, int elevationDegrees) =>
        (azimuthDegrees + 180) * 181 + elevationDegrees + 90;

    /// <summary>
    /// The bucket index a distance produces, computed the way WorldMap documents rather than read back
    /// from it, so the dirty-bucket assertions name buckets by meaning instead of by whatever the code
    /// happened to return. Same clamp, same rounding, same (Buckets - 1) span.
    /// </summary>
    private static int BucketOf(int mm) =>
        (int)Math.Round(Math.Clamp((double)mm / MaxMm, 0, 1) * (Buckets - 1));
}
