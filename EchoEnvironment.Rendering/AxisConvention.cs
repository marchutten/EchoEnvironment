using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using EchoEnvironment.Core;

namespace EchoEnvironment.Rendering;

/// <summary>
/// One world axis, as the renderer draws it: its letter, the direction it points, and the colours that
/// mean that letter. The single source defect D9 was missing.
/// </summary>
/// <param name="Letter">The letter a viewer reads for this axis.</param>
/// <param name="Direction">A unit vector along the axis's POSITIVE end, in world coordinates.</param>
/// <param name="GizmoColor">The colour used for the sensor gizmo, which rotates with the board.</param>
/// <param name="ReferenceColor">The muted colour used for the fixed world reference axes, so the two
/// gizmos in one viewport are distinguishable while still meaning the same letter.</param>
public readonly record struct AxisLabel(
    string Letter,
    Vector3D Direction,
    Color GizmoColor,
    Color ReferenceColor);

/// <summary>
/// The one place the renderer decides which letter is which axis and which colour is which letter.
/// Defect D9 (MIGRATION_PLAN 5d) existed because there was no such place.
///
/// THE DEFECT, STATED PRECISELY ENOUGH TO FIX. ScenePrimitives contained two axis gizmos built from
/// hand-written literals:
///
///   AddAxes        - a box along +X labelled "X" (red), a box along -Z labelled "Y" (blue), a box along
///                    +Y labelled "Z" (green).
///   CreateWorldAxes - a box along +X (dark red), a box along +Y (dark green), a box along +Z (dark blue),
///                    with no labels at all.
///
/// Two separate defects sit in that, and only one of them is the one the plan names.
///
/// (1) THE LABEL DOES NOT MATCH THE ARM IT LABELS. AddAxes put a blue box along -Z and stuck the letter
/// "Y" at the end of it, and a green box along +Y with the letter "Z" at its end. Whatever the world
/// frame means, an arm drawn along one axis and labelled with another is simply wrong, and it is provable
/// from the source with no reference frame at all. That is the half 5d fixes.
///
/// (2) THE TWO GIZMOS DISAGREE ABOUT WHICH LETTER IS WHICH COLOUR. AddAxes said blue is Y;
/// CreateWorldAxes said blue is +Z. That is the disagreement the plan describes, and it is fixed by
/// making both read this type.
///
/// THE HALF 5d CANNOT FIX, AND DOES NOT PRETEND TO. The two gizmos live in different scene trees with
/// different transforms: AddAxes goes into _boardGroup, which carries the orbit's view transform
/// (a model remap, then the sensor's orientation, then a 90 degree turn) and so rotates with the board's
/// attitude; CreateWorldAxes goes into boardRoot, which is untransformed and stays fixed. Agreeing on a
/// letter-to-axis-to-colour mapping does NOT make them point the same way on screen, and it should not:
/// a sensor gizmo that pointed the same way as the world gizmo would be lying about the sensor's attitude.
/// What agreement buys is that the comparison a viewer makes between them is meaningful. Finding 6
/// recorded the on-screen disagreement on real hardware; the remap that contributes to it is recorded
/// separately, because removing it is a geometry change and not a labelling one.
///
/// WHY THE Z ARM MOVED FROM -Z TO +Z. AddAxes drew its Z arm along -Z. In this world frame -Z is north
/// (WorldFrame.North), so the arm was at the NEGATIVE end of the Z axis while carrying the bare letter
/// "Z" - a gizmo that labels its own negative end as if it were the positive one. Every axis is now drawn
/// from the origin to its positive end, which is also exactly what CreateWorldAxes already did, so the two
/// gizmos are the same shape. This changes pixels, and 5d is the commit that is allowed to: MIGRATION_PLAN
/// 5c/5d are the behavioural half of Phase 5, and the visual comparison is a human step.
///
/// WHY NO Core TYPE APPEARS IN THE PUBLIC SURFACE. WorldFrame is Core and Color is WPF. A public member
/// returning both - an AxisLabel carrying a Core Vec3 and a WPF Color - would name Core and WPF in the
/// same signature, which is what WpfConversionIsConfinedToOneType calls a conversion point, and that guard
/// exempts only WpfMath and UI types. So WorldFrame is read inside the initialisers and the public shape
/// is WPF-only: AxisLabel carries a Vector3D, and the conversion to it happens by calling WpfMath. The
/// convention's *meaning* stays defined in Core; only its rendering shape lives here.
/// </summary>
public static class AxisConvention
{
    /// <summary>The letter X, along world +X = east (WorldFrame.East).</summary>
    public static readonly AxisLabel X = new("X", WorldFrame.East.ToVector3D(), Colors.Red, Color.FromRgb(120, 40, 40));

    /// <summary>The letter Y, along world +Y = up (WorldFrame.Up).</summary>
    public static readonly AxisLabel Y = new("Y", WorldFrame.Up.ToVector3D(), Colors.LimeGreen, Color.FromRgb(40, 120, 40));

    /// <summary>
    /// The letter Z, along world +Z = SOUTH (WorldFrame.South). North is -Z in this frame, so the Z arm
    /// points away from north. That is not a mistake and it is the convention the whole codebase already
    /// depends on: Mat3.CreateOrientation negates its north column for exactly this reason, and
    /// WorldMap measures azimuth as atan2(X, -Z) for the same reason.
    /// </summary>
    public static readonly AxisLabel Z = new("Z", WorldFrame.South.ToVector3D(), Colors.DeepSkyBlue, Color.FromRgb(40, 80, 120));

    /// <summary>
    /// The three axes in the order both gizmos draw them. Drawn from this list, an axis cannot be added to
    /// one gizmo and forgotten in the other, which is how the two drifted apart in the first place.
    /// </summary>
    public static IReadOnlyList<AxisLabel> WorldAxes { get; } = [X, Y, Z];

    /// <summary>
    /// Looks up an axis by letter. Used by the tests that assert the two gizmos agree, and by anything
    /// that has a letter and needs to check what it means.
    /// </summary>
    public static AxisLabel ByLetter(string letter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(letter);

        foreach (AxisLabel axis in WorldAxes)
        {
            if (string.Equals(axis.Letter, letter, StringComparison.Ordinal))
            {
                return axis;
            }
        }

        throw new KeyNotFoundException($"'{letter}' is not an axis this renderer draws. The letters are X, Y and Z.");
    }
}
