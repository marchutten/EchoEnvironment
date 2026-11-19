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

    /// <summary>
    /// The key packing WorldMap documents: (az + 180) * 181 + (el + 90). Written here rather than
    /// read from the implementation so the tests assert the documented convention instead of
    /// restating whatever the code currently does.
    /// </summary>
    private static int Key(int azimuthDegrees, int elevationDegrees) =>
        (azimuthDegrees + 180) * 181 + elevationDegrees + 90;
}
