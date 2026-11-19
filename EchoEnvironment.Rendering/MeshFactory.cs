using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace EchoEnvironment.Rendering;

/// <summary>
/// Every stateless 3D shape the app draws, moved out of MainWindow at Phase 5 (MIGRATION_PLAN 5).
///
/// The method bodies are a verbatim relocation, not a rewrite: the geometry, the triangle winding,
/// the magic sizes and the Freeze() calls are exactly what MainWindow contained before, so a visual
/// difference after this commit would be evidence of a broken move rather than a design change.
/// That is the point of splitting the move from 5a/5b — the allocation fixes land in their own commit
/// and can then be blamed for anything that looks different.
///
/// These are the only members of the plan's mapping table that are genuinely static and stateless,
/// which is what makes this first commit mechanically checkable. The stateful renderer members
/// (UpdateFrame, UpdateHeatmap, AddToMap, RenderMapIfDue, EnsureZoneModels) follow in their own
/// commit for the same reason.
/// </summary>
public static class MeshFactory
{
    public static MeshGeometry3D CreateCubeMesh(double size)
    {
        double h = size / 2;
        var mesh = new MeshGeometry3D();
        Point3D[] p =
        [
            new(-h, -h, -h), new(h, -h, -h), new(h, h, -h), new(-h, h, -h),
            new(-h, -h, h), new(h, -h, h), new(h, h, h), new(-h, h, h)
        ];
        foreach (Point3D point in p)
        {
            mesh.Positions.Add(point);
        }

        int[] tri =
        [
            0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7,
            0, 1, 5, 0, 5, 4, 3, 7, 6, 3, 6, 2,
            1, 2, 6, 1, 6, 5, 0, 4, 7, 0, 7, 3
        ];
        foreach (int i in tri)
        {
            mesh.TriangleIndices.Add(i);
        }

        mesh.Freeze();
        return mesh;
    }

    public static MeshGeometry3D CreateBoxMesh(Point3D center, double sx, double sy, double sz)
    {
        MeshGeometry3D unit = CreateCubeMesh(1.0);
        var mesh = new MeshGeometry3D();
        foreach (Point3D p in unit.Positions)
        {
            mesh.Positions.Add(new Point3D(center.X + p.X * sx, center.Y + p.Y * sy, center.Z + p.Z * sz));
        }

        foreach (int i in unit.TriangleIndices)
        {
            mesh.TriangleIndices.Add(i);
        }

        mesh.Freeze();
        return mesh;
    }

    public static GeometryModel3D CreateColoredBox(Point3D center, double sx, double sy, double sz, Color color)
    {
        var material = new DiffuseMaterial(new SolidColorBrush(color));
        var model = new GeometryModel3D(CreateBoxMesh(center, sx, sy, sz), material) { BackMaterial = material };
        model.Freeze();
        return model;
    }

    public static GeometryModel3D CreateBoardModel()
    {
        // Sensor looks along -Z, so the front edge of the board is at -Z.
        return CreateColoredBox(new Point3D(0, 0, 0), 0.4, 0.03, 0.25, Color.FromRgb(20, 110, 60));
    }

    // Horizontal raster at z = 0 (WPF Y = 0), fixed in the world so it stays visible at any zoom.
    public static GeometryModel3D CreateGridModel(double extent, double spacing)
    {
        const double half = 0.003;
        var mesh = new MeshGeometry3D();
        int lines = (int)Math.Round(extent / spacing);

        // AddFlatQuad is a local function, not a MainWindow member, so it travels with its host
        // rather than appearing in the plan's mapping table — which lists CreateGridModel but never
        // names this, even though it is the code that actually emits the grid's triangles.
        void AddFlatQuad(double x0, double z0, double x1, double z1)
        {
            int i = mesh.Positions.Count;
            mesh.Positions.Add(new Point3D(x0, 0, z0));
            mesh.Positions.Add(new Point3D(x1, 0, z0));
            mesh.Positions.Add(new Point3D(x1, 0, z1));
            mesh.Positions.Add(new Point3D(x0, 0, z1));
            foreach (int t in new[] { 0, 1, 2, 0, 2, 3 })
            {
                mesh.TriangleIndices.Add(i + t);
            }
        }

        for (int n = -lines; n <= lines; n++)
        {
            double c = n * spacing;
            double w = Math.Sqrt(extent * extent - c * c);
            AddFlatQuad(c - half, -w, c + half, w);
            AddFlatQuad(-w, c - half, w, c + half);
        }

        var material = new EmissiveMaterial(new SolidColorBrush(Color.FromRgb(90, 110, 130)));
        var model = new GeometryModel3D(mesh, material) { BackMaterial = material };
        model.Freeze();
        return model;
    }

    public static GeometryModel3D CreateLineModel()
    {
        var material = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(200, 200, 200)));
        return new GeometryModel3D { Material = material, BackMaterial = material };
    }

    public static GeometryModel3D CreateSensorMarker()
    {
        var material = new DiffuseMaterial(Brushes.Orange);
        var model = new GeometryModel3D(CreateCubeMesh(0.015), material) { BackMaterial = material };
        model.Freeze();
        return model;
    }

    public static GeometryModel3D CreateReferenceSphere(double radius)
    {
        var mesh = new MeshGeometry3D();
        const int rings = 12;
        const int segments = 24;

        for (int i = 0; i <= rings; i++)
        {
            double phi = Math.PI * i / rings;
            for (int j = 0; j <= segments; j++)
            {
                double theta = 2 * Math.PI * j / segments;
                mesh.Positions.Add(new Point3D(
                    radius * Math.Sin(phi) * Math.Cos(theta),
                    radius * Math.Cos(phi),
                    radius * Math.Sin(phi) * Math.Sin(theta)));
            }
        }

        for (int i = 0; i < rings; i++)
        {
            for (int j = 0; j < segments; j++)
            {
                int a = i * (segments + 1) + j;
                int b = a + segments + 1;
                mesh.TriangleIndices.Add(a);
                mesh.TriangleIndices.Add(b);
                mesh.TriangleIndices.Add(a + 1);
                mesh.TriangleIndices.Add(a + 1);
                mesh.TriangleIndices.Add(b);
                mesh.TriangleIndices.Add(b + 1);
            }
        }

        var brush = new SolidColorBrush(Color.FromArgb(28, 120, 160, 255));
        var material = new DiffuseMaterial(brush);
        var model = new GeometryModel3D(mesh, material) { BackMaterial = material };
        model.Freeze();
        return model;
    }
}
