using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace EchoEnvironment.Rendering;

/// <summary>
/// The drawn things that are not a solid shape: a line, an axis set, an axis label.
///
/// Moved out of MainWindow verbatim at Phase 5 (MIGRATION_PLAN 5). AddAxes and CreateWorldAxes are the
/// two members defect D9 is about, and they were deliberately NOT reconciled in that commit: relabelling
/// axes changes pixels, and commit 1 was the one the pixel comparison was supposed to prove neutral.
/// They are reconciled here, at 5d, which is the commit Phase 5 allows to change pixels.
///
/// WHAT RECONCILED MEANS, AND WHAT IT DOES NOT MEAN. Both gizmos now read the same AxisConvention, so a
/// letter means one axis and a colour means one letter everywhere in this file. That fixes the provable
/// half of D9 - AddAxes drew a blue arm along -Z and labelled it "Y", and a green arm along +Y and labelled
/// it "Z". It does NOT make the two gizmos point the same way on screen, and they should not: AddAxes lives
/// in _boardGroup, which carries the sensor's attitude and rotates, while CreateWorldAxes lives in
/// boardRoot, which is untransformed and stays fixed. A sensor gizmo that pointed where the world gizmo
/// points would be lying about the sensor. See AxisConvention's class comment for the full separation, and
/// Finding 6 for the on-screen disagreement as it appeared on real hardware.
///
/// CreateColoredBox is qualified with MeshFactory rather than imported with `using static`: the two classes
/// are siblings in this assembly, and spelling out which one builds a box is the kind of detail that stops
/// a later reader assuming they are the same type.
/// </summary>
public static class ScenePrimitives
{
    private const double LineHalfWidth = 0.0005;

    // WPF 3D has no line primitive, so each line is two crossed thin quads.
    public static void AddLine(MeshGeometry3D mesh, Vector3D end)
    {
        Vector3D dir = end;
        dir.Normalize();
        Vector3D side = Vector3D.CrossProduct(dir, Math.Abs(dir.Y) > 0.99 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0));
        side.Normalize();
        Vector3D up = Vector3D.CrossProduct(dir, side);
        up.Normalize();

        foreach (Vector3D offset in new[] { side * LineHalfWidth, up * LineHalfWidth })
        {
            int i = mesh.Positions.Count;
            mesh.Positions.Add(new Point3D(-offset.X, -offset.Y, -offset.Z));
            mesh.Positions.Add(new Point3D(offset.X, offset.Y, offset.Z));
            mesh.Positions.Add(new Point3D(end.X + offset.X, end.Y + offset.Y, end.Z + offset.Z));
            mesh.Positions.Add(new Point3D(end.X - offset.X, end.Y - offset.Y, end.Z - offset.Z));
            int[] tri = [0, 1, 2, 0, 2, 3];
            foreach (int t in tri)
            {
                mesh.TriangleIndices.Add(i + t);
            }
        }
    }

    /// <summary>
    /// The labelled sensor gizmo: one arm per world axis, each labelled with the letter of the axis it is
    /// actually drawn along. Rewritten at Phase 5d to derive from AxisConvention.
    ///
    /// WHAT IT USED TO BE. Three hand-written boxes and three hand-written letters:
    ///
    ///     CreateColoredBox(new Point3D(h, 0, 0),  length, width, width, Colors.Red);
    ///     CreateColoredBox(new Point3D(0, 0, -h),  width, width, length, Colors.DeepSkyBlue);
    ///     CreateColoredBox(new Point3D(0, h, 0),   width, length, width, Colors.LimeGreen);
    ///     CreateAxisLabel("X", new Point3D(length + 0.08, 0, 0),        Colors.Red);
    ///     CreateAxisLabel("Y", new Point3D(0, 0, -(length + 0.08)),     Colors.DeepSkyBlue);
    ///     CreateAxisLabel("Z", new Point3D(0, length + 0.08, 0),        Colors.LimeGreen);
    ///
    /// Read as drawing rather than as intent: a blue arm drawn along -Z carrying the letter "Y", and a
    /// green arm drawn along +Y carrying the letter "Z". An arm drawn along one axis and labelled with
    /// another is wrong on its own terms, with no reference frame needed to prove it. The letters and the
    /// arms are now produced from one list, so the pairing cannot drift again.
    ///
    /// THE Z ARM MOVED FROM -Z TO +Z. -Z is north in this world frame, so the old gizmo labelled the
    /// negative end of the Z axis with the bare letter "Z". Every arm now runs from the origin to its
    /// positive end, which is also what CreateWorldAxes already did. This changes pixels, and 5d is the
    /// commit Phase 5 allows it in.
    /// </summary>
    public static void AddAxes(Model3DGroup group, double length, double width)
    {
        ArgumentNullException.ThrowIfNull(group);

        double h = length / 2;

        foreach (AxisLabel axis in AxisConvention.WorldAxes)
        {
            Vector3D direction = axis.Direction;
            var end = new Point3D(direction.X, direction.Y, direction.Z);
            (double sx, double sy, double sz) = BoxExtents(direction, length, width);

            group.Children.Add(MeshFactory.CreateColoredBox(
                new Point3D(end.X * h, end.Y * h, end.Z * h),
                sx,
                sy,
                sz,
                axis.GizmoColor));

            group.Children.Add(CreateAxisLabel(
                axis.Letter,
                new Point3D(end.X * (length + LabelGap), end.Y * (length + LabelGap), end.Z * (length + LabelGap)),
                axis.GizmoColor));
        }
    }

    /// <summary>
    /// The unlabelled fixed world reference axes, also derived from AxisConvention so its colours mean the
    /// same letters the gizmo's colours mean. Rewritten at Phase 5d.
    ///
    /// WHY IT STAYS UNLABELLED. It sits in boardRoot, which is untransformed, while the labelled gizmo
    /// sits in _boardGroup, which rotates with the board's attitude. Two sets of letters in one viewport,
    /// one of them spinning, is worse than one set of letters and a fixed colour reference. What 5d fixes
    /// is that blue used to mean "Y" here and "+Z" there; both now mean Z.
    /// </summary>
    public static Model3DGroup CreateWorldAxes()
    {
        var group = new Model3DGroup();
        const double length = 1.5;
        const double thickness = 0.004;
        double h = length / 2;

        foreach (AxisLabel axis in AxisConvention.WorldAxes)
        {
            Vector3D direction = axis.Direction;
            var end = new Point3D(direction.X, direction.Y, direction.Z);
            (double sx, double sy, double sz) = BoxExtents(direction, length, thickness);

            group.Children.Add(MeshFactory.CreateColoredBox(
                new Point3D(end.X * h, end.Y * h, end.Z * h),
                sx,
                sy,
                sz,
                axis.ReferenceColor));
        }

        return group;
    }

    /// <summary>
    /// Distance from the origin to an axis label, past the end of the arm it labels. Was an inline
    /// "+ 0.08" repeated three times in AddAxes.
    /// </summary>
    private const double LabelGap = 0.08;

    /// <summary>
    /// The box's three component extents: `along` on the axis's own component, `across` on the other two.
    /// The world axes are cardinal, so exactly one component of the direction is non-zero and the choice is
    /// that component. Asserted rather than assumed: a non-cardinal direction would silently produce a box
    /// that is long in the wrong dimension, which is a geometry defect that renders plausibly.
    /// </summary>
    private static (double X, double Y, double Z) BoxExtents(Vector3D direction, double along, double across)
    {
        if (Math.Abs(direction.X) > 0.5)
        {
            return (along, across, across);
        }

        if (Math.Abs(direction.Y) > 0.5)
        {
            return (across, along, across);
        }

        if (Math.Abs(direction.Z) > 0.5)
        {
            return (across, across, along);
        }

        throw new ArgumentException(
            $"Axis directions must be cardinal for the gizmo boxes to be built by component; got ({direction.X}, {direction.Y}, {direction.Z}).",
            nameof(direction));
    }

    public static GeometryModel3D CreateAxisLabel(string text, Point3D center, Color color)
    {
        var host = new Grid { Width = 64, Height = 64 };
        host.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 52,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(color),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        host.Measure(new Size(64, 64));
        host.Arrange(new Rect(0, 0, 64, 64));

        const double s = 0.06;
        var mesh = new MeshGeometry3D();
        mesh.Positions.Add(new Point3D(center.X - s, center.Y - s, center.Z));
        mesh.Positions.Add(new Point3D(center.X + s, center.Y - s, center.Z));
        mesh.Positions.Add(new Point3D(center.X + s, center.Y + s, center.Z));
        mesh.Positions.Add(new Point3D(center.X - s, center.Y + s, center.Z));
        mesh.TextureCoordinates.Add(new Point(0, 1));
        mesh.TextureCoordinates.Add(new Point(1, 1));
        mesh.TextureCoordinates.Add(new Point(1, 0));
        mesh.TextureCoordinates.Add(new Point(0, 0));
        foreach (int i in new[] { 0, 1, 2, 0, 2, 3 })
        {
            mesh.TriangleIndices.Add(i);
        }

        var material = new EmissiveMaterial(new VisualBrush(host));
        return new GeometryModel3D(mesh, material) { BackMaterial = material };
    }

}
