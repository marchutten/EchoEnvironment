using System.Collections.Generic;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using EchoEnvironment.Core;
using EchoEnvironment.Rendering;
using Xunit;

namespace EchoEnvironment.Tests;

/// <summary>
/// Tests for BrushCache, the fix for defect D3 (MIGRATION_PLAN 5a).
///
/// WHY THIS FILE NEEDS NO STA THREAD. Every other test that touches the renderer runs through
/// StaRunner, and the reason recorded in ZoneRendererTests is that `new UniformGrid()` needs an
/// apartment. Nothing here constructs a FrameworkElement: SolidColorBrush and DiffuseMaterial are
/// Freezables, and Freezables construct on a pooled thread. That is a measurement, not an accident -
/// it separates the part of the rendering layer that is apartment-free from the part that is not, which
/// is the same distinction Finding 3 made about ZoneRenderer. If a test in this file ever starts
/// failing with "The calling thread must be STA", this class has acquired a UI dependency.
///
/// WHAT THESE TESTS ARE FOR. The reuse is an optimisation, and an optimisation that is not asserted is
/// an optimisation that can be silently undone by the next edit. The assertions below are identity
/// assertions - Assert.Same - because "the colour is still right" is already pinned by
/// ZoneRendererTests.ZoneColourIsTheCoreRampAtThatDistance. What is new here is that the same object
/// comes back.
/// </summary>
public class BrushCacheTests
{
    private const double MaxMm = 4000.0;

    /// <summary>
    /// The defect, stated as a measurable thing: the same distance asked for the same colour twice and
    /// got two objects. Now it gets one.
    /// </summary>
    [Fact]
    public void SameDistanceReturnsTheSameFrozenBrushInstance()
    {
        var cache = new BrushCache();

        SolidColorBrush first = cache.BrushFor(1500, MaxMm);
        SolidColorBrush second = cache.BrushFor(1500, MaxMm);

        Assert.Same(first, second);
        Assert.True(first.IsFrozen, "a brush shared across visuals must be frozen, or mutating it would repaint everything using it.");
    }

    /// <summary>
    /// The reuse must not cost colour accuracy. The plan's sketch was a 256-entry lookup table indexed
    /// by the ramp's bucketing, which is cheaper to build but returns the colour of the bucket rather
    /// than the colour of the reading - a quantisation, so the fix for an allocation defect would change
    /// pixels. Keying on the exact Rgb is what makes the two assertions below both true at once.
    /// </summary>
    [Fact]
    public void CachedBrushIsTheExactRampColour_NotTheColourOfABucket()
    {
        var cache = new BrushCache();

        // Two distances that land in the same 256-entry bucket would have shared a colour under the
        // plan's lookup table. They must not share one here.
        const int near = 1000;
        const int slightlyFurther = 1010;

        SolidColorBrush nearBrush = cache.BrushFor(near, MaxMm);
        SolidColorBrush furtherBrush = cache.BrushFor(slightlyFurther, MaxMm);

        Assert.Equal(ColorScale.For(near, MaxMm).ToColor(), nearBrush.Color);
        Assert.Equal(ColorScale.For(slightlyFurther, MaxMm).ToColor(), furtherBrush.Color);
        Assert.NotEqual(nearBrush.Color, furtherBrush.Color);
        Assert.NotSame(nearBrush, furtherBrush);
    }

    /// <summary>
    /// The claim in BrushCache's own comment: the cache cannot grow without bound, because the ramp is a
    /// function of one clamped scalar and its three channels are monotone in it. Measured rather than
    /// trusted - a cache with no eviction policy is only safe while that bound is real.
    /// </summary>
    [Fact]
    public void DistinctColoursAreBoundedByTheRamp_NotByTheNumberOfDistances()
    {
        var cache = new BrushCache();
        var distinct = new HashSet<Color>();

        // Every integer distance from zero to twice the ramp's maximum, i.e. four times the distances a
        // 64-zone frame could ever produce in one frame. If the cache were keyed on distance rather than
        // on colour, this would produce 8,001 brushes.
        for (int mm = 0; mm <= 2 * (int)MaxMm; mm++)
        {
            distinct.Add(cache.BrushFor(mm, MaxMm).Color);
        }

        // The bound asserted here is the one BrushCache's comment argues for: R falls 255 to 0, B rises
        // 0 to 255, G rises 0 to 200 and falls back, so a new colour can only appear where one of those
        // byte values changes. 256 + 256 + 201 is the ceiling; the measured count is well under it.
        Assert.True(distinct.Count <= 256 + 256 + 201,
            $"the ramp produced {distinct.Count} distinct colours from 8,001 distances; that exceeds the 713-colour ceiling its monotone channels imply, so BrushCache's no-eviction design is no longer safe.");
        Assert.True(distinct.Count < 8_001,
            $"8,001 distances produced {distinct.Count} colours; if those are equal the cache is keyed on distance, not on colour.");
    }

    /// <summary>
    /// Materials are the bigger half of the fix: UpdateFrame recoloured 64 zones per frame through a
    /// cast to DiffuseMaterial plus a new brush. A cached frozen material removes the cast as well as
    /// the allocation.
    /// </summary>
    [Fact]
    public void MaterialIsCachedFrozen_AndSharesTheBrushOfTheSameColour()
    {
        var cache = new BrushCache();

        DiffuseMaterial first = cache.MaterialFor(2000, MaxMm);
        DiffuseMaterial second = cache.MaterialFor(2000, MaxMm);

        Assert.Same(first, second);
        Assert.True(first.IsFrozen, "a material assigned to both Material and BackMaterial of shared models must be frozen.");
        Assert.Same(cache.BrushFor(2000, MaxMm), first.Brush);
    }

    /// <summary>
    /// A colour that is not on the ramp - the grey of an out-of-range heat cell - gets the same reuse
    /// through a separate key, because it was never a Core Rgb to begin with.
    /// </summary>
    [Fact]
    public void FlatColourIsCachedSeparately_AndIsNotConfusedWithTheRamp()
    {
        var cache = new BrushCache();
        var grey = Color.FromRgb(50, 50, 50);

        SolidColorBrush first = cache.BrushFor(grey);
        SolidColorBrush second = cache.BrushFor(grey);

        Assert.Same(first, second);
        Assert.Equal(grey, first.Color);
        Assert.True(first.IsFrozen);

        // The ramp also produces a near-grey somewhere; the flat path must not return the ramp's brush
        // for it, or an invalid cell would be indistinguishable from a real reading.
        Assert.NotSame(first, cache.BrushFor(2500, MaxMm));
    }

    /// <summary>
    /// A cache instance is not shared state. Two renderers in two windows, or two renderers in two
    /// parallel test collections on separate STA threads, get separate dictionaries. This is the reason
    /// BrushCache is an injected instance rather than the static class the plan's sketch implies.
    /// </summary>
    [Fact]
    public void TwoCacheInstancesDoNotShareBrushes()
    {
        var first = new BrushCache();
        var second = new BrushCache();

        Assert.NotSame(first.BrushFor(1500, MaxMm), second.BrushFor(1500, MaxMm));
        Assert.Equal(first.BrushFor(1500, MaxMm).Color, second.BrushFor(1500, MaxMm).Color);
    }

    /// <summary>
    /// The architecture rule, applied to the new type. Rgb is a Core type and Brush is a WPF one; a
    /// public BrushFor(Rgb) would name both in one signature, which is what
    /// WpfConversionIsConfinedToOneType calls a conversion point, and that guard exempts only WpfMath
    /// and UI types. Same shape as ZoneRendererDoesNotExposeCoreTypes: Rgb stays the private key of a
    /// private dictionary and the public surface speaks only distances.
    /// </summary>
    [Fact]
    public void BrushCacheSpeaksWpf_AndNamesNoCoreTypeInItsPublicSurface()
    {
        Assert.Equal("EchoEnvironment.Rendering", typeof(BrushCache).Assembly.GetName().Name);

        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;

        foreach (MethodInfo method in typeof(BrushCache).GetMethods(flags))
        {
            Assert.False(method.ReturnType.Assembly.GetName().Name == "EchoEnvironment.Core",
                $"BrushCache.{method.Name} returns {method.ReturnType.FullName}; a public Core type here would be a second Core-to-WPF conversion point.");
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                Assert.False(parameter.ParameterType.Assembly.GetName().Name == "EchoEnvironment.Core",
                    $"BrushCache.{method.Name} parameter '{parameter.Name}' is {parameter.ParameterType.FullName}; the ramp's Rgb must stay a private key.");
            }
        }

        // The positive half, so the assertions above are measuring a real surface rather than an empty one.
        Assert.Contains(typeof(BrushCache).GetMethods(flags), method => method.ReturnType == typeof(SolidColorBrush));
    }
}
