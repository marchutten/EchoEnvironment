using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using EchoEnvironment.Core;

namespace EchoEnvironment.Rendering;

/// <summary>
/// Reused frozen brushes and materials, keyed by the colour they paint (defect D3, MIGRATION_PLAN 5a).
///
/// THE DEFECT. UpdateFrame recoloured every zone and UpdateHeatmap repainted every cell by constructing
/// a new SolidColorBrush each time: 64 + 64 = 128 brush allocations per rendered frame. At 60 Hz that is
/// 7,680 objects a second, all of them short-lived, all of them on the UI thread, and all of them
/// identical to brushes that already exist. The heat grid made it worse than it looks: a cell that shows
/// the same distance in two consecutive frames got a brand-new brush with the same colour in both.
///
/// WHY KEYED BY COLOUR AND NOT BY A BUCKET INDEX. The plan sketches "256 pre-frozen brushes indexed by
/// the ramp's bucketing". That is cheaper to build but it quantises the colours: a bucket lookup returns
/// the colour of the bucket, not the colour of the reading, so the fix for an allocation defect would
/// quietly change pixels. Keying on the exact Rgb the ramp already produces means a cache hit returns a
/// brush whose colour is bit-for-bit the one that a fresh allocation would have had. The optimisation is
/// then provably neutral, which is what lets it sit in the same commit as the transform reuse.
///
/// WHY THE CACHE CANNOT GROW WITHOUT LIMIT. ColorScale.For is a function of one scalar, t = mm /
/// maxDistance clamped to [0, 1]. Its three channels are monotone in t: R falls 255 to 0, B rises 0 to
/// 255, G rises 0 to 200 and falls back. A new Rgb can therefore only appear at a point where one of
/// those byte values changes, and there are at most 256 + 256 + 201 of them. The number of distinct
/// colours the ramp can ever produce is bounded by that, whatever the distances are - which is why this
/// cache needs no eviction policy, no size cap and no generation counter. BrushCacheDistinctColoursAre
/// BoundedByTheRampNotByTheData measures it instead of trusting the arithmetic.
///
/// WHY AN INSTANCE AND NOT A STATIC CLASS. A process-wide mutable dictionary would be shared by every
/// ZoneRenderer in the process, including the ones xunit runs in parallel test collections on separate
/// STA threads. That is a data race introduced by an optimisation, in a codebase that already has one
/// recorded thread-safety exposure (LineFramer, Finding 11). One cache per renderer has the same reuse
/// inside the frame loop that matters and no shared mutable state at all. MIGRATION_PLAN 6c passes a
/// BrushCache to the view model; when that arrives, this instance is what gets handed over.
///
/// WHY NO Core TYPE APPEARS IN THE PUBLIC SURFACE. Rgb is a Core type and Brush is a WPF one. A public
/// BrushFor(Rgb) would name both in one signature, which is exactly what WpfConversionIsConfinedToOneType
/// calls a conversion point, and that guard exempts only WpfMath and UI types. So Rgb is the private key
/// of a private dictionary and the public API speaks only distances and WPF - the same decision
/// ZoneRenderer made about its WorldMap.
/// </summary>
public sealed class BrushCache
{
    private readonly Dictionary<Rgb, SolidColorBrush> _rampBrushes = [];
    private readonly Dictionary<Rgb, DiffuseMaterial> _rampMaterials = [];
    private readonly Dictionary<Color, SolidColorBrush> _flatBrushes = [];

    /// <summary>
    /// The brush for a distance, exactly the colour ColorScale.For would have produced. Repeated calls
    /// for the same distance return the same frozen brush instance.
    /// </summary>
    public SolidColorBrush BrushFor(double mm, double maxDistanceMm) => RampBrush(ColorScale.For(mm, maxDistanceMm));

    /// <summary>
    /// A frozen DiffuseMaterial painting that same colour, shared by every model that wants it.
    ///
    /// Returned frozen and shared, so a caller that assigns it to both Material and BackMaterial of one
    /// GeometryModel3D is safe: a frozen Freezable has no owner and may be referenced by many visuals.
    /// Assigning it to only one of the two is not safe, and that is a trap worth naming because the code
    /// this replaced mutated a single material object that was referenced by both, so front and back
    /// faces always matched. Swapping Material for a cached one while leaving BackMaterial where it was
    /// makes the back faces keep their old colour.
    /// </summary>
    public DiffuseMaterial MaterialFor(double mm, double maxDistanceMm)
    {
        Rgb rgb = ColorScale.For(mm, maxDistanceMm);
        if (_rampMaterials.TryGetValue(rgb, out DiffuseMaterial? material))
        {
            return material;
        }

        var created = new DiffuseMaterial(RampBrush(rgb));
        created.Freeze();
        _rampMaterials[rgb] = created;
        return created;
    }

    /// <summary>
    /// A frozen brush for a colour that is not on the ramp - the grey of an out-of-range heat cell, for
    /// instance. Same reuse argument, different source: this one is keyed on the WPF Color because the
    /// colour was never a Core Rgb to begin with.
    /// </summary>
    public SolidColorBrush BrushFor(Color color)
    {
        if (_flatBrushes.TryGetValue(color, out SolidColorBrush? brush))
        {
            return brush;
        }

        var created = new SolidColorBrush(color);
        created.Freeze();
        _flatBrushes[color] = created;
        return created;
    }

    private SolidColorBrush RampBrush(Rgb rgb)
    {
        if (_rampBrushes.TryGetValue(rgb, out SolidColorBrush? brush))
        {
            return brush;
        }

        var created = new SolidColorBrush(rgb.ToColor());
        created.Freeze();
        _rampBrushes[rgb] = created;
        return created;
    }
}
