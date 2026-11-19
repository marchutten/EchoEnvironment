using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace EchoEnvironment.Rendering;

/// <summary>
/// The drawn things that are not a solid shape: a line, an axis set, an axis label.
///
/// Moved out of MainWindow verbatim at Phase 5 (MIGRATION_PLAN 5). AddAxes and CreateWorldAxes are
/// the two members defect D9 is about — they disagree with each other about which axis is which —
/// and they are deliberately NOT reconciled in this commit. Relabelling axes changes pixels, and this
/// commit is the one the pixel comparison is supposed to prove is neutral. 5d does that on its own.
///
/// CreateColoredBox is qualified with MeshFactory rather than imported with `using static`: the two
/// classes are siblings in this assembly, and spelling out which one builds a box is the kind of
/// detail that stops a later reader assuming they are the same type.
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

    public static void AddAxes(Model3DGroup group, double length, double width)
    {
        double h = length / 2;
        group.Children.Add(MeshFactory.CreateColoredBox(new Point3D(h, 0, 0), length, width, width, Colors.Red));
        group.Children.Add(MeshFactory.CreateColoredBox(new Point3D(0, 0, -h), width, width, length, Colors.DeepSkyBlue));
        group.Children.Add(MeshFactory.CreateColoredBox(new Point3D(0, h, 0), width, length, width, Colors.LimeGreen));
        group.Children.Add(CreateAxisLabel("X", new Point3D(length + 0.08, 0, 0), Colors.Red));
        group.Children.Add(CreateAxisLabel("Y", new Point3D(0, 0, -(length + 0.08)), Colors.DeepSkyBlue));
        group.Children.Add(CreateAxisLabel("Z", new Point3D(0, length + 0.08, 0), Colors.LimeGreen));
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

    public static Model3DGroup CreateWorldAxes()
    {
        var group = new Model3DGroup();
        double length = 1.5;
        double h = length / 2;
        group.Children.Add(MeshFactory.CreateColoredBox(new Point3D(h, 0, 0), length, 0.004, 0.004, Color.FromRgb(120, 40, 40)));
        group.Children.Add(MeshFactory.CreateColoredBox(new Point3D(0, h, 0), 0.004, length, 0.004, Color.FromRgb(40, 120, 40)));
        group.Children.Add(MeshFactory.CreateColoredBox(new Point3D(0, 0, h), 0.004, 0.004, length, Color.FromRgb(40, 80, 120)));
        return group;
    }
}
