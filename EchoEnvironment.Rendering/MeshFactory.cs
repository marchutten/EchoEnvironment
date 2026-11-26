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

        int[] tri =
        [
            0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7,
            0, 1, 5, 0, 5, 4, 3, 7, 6, 3, 6, 2,
            1, 2, 6, 1, 6, 5, 0, 4, 7, 0, 7, 3
        ];

        // Flat shading (MIGRATION_PLAN Phase 7, defect line 509): each triangle gets its own three
        // vertices and the face normal derived from its own winding, so a box under the DirectionalLight
        // shades each face by its orientation instead of falling back to WPF's undefined normals. The
        // winding is byte-for-byte the shared-vertex version above, so which faces are front/back is
        // unchanged — only the vertex count grows from 8 to 36, because flat faces cannot share corners.
        for (int t = 0; t < tri.Length; t += 3)
        {
            Point3D a = p[tri[t]], b = p[tri[t + 1]], c = p[tri[t + 2]];
            Vector3D n = Vector3D.CrossProduct(b - a, c - a);
            n.Normalize();
            mesh.Positions.Add(a); mesh.Normals.Add(n);
            mesh.Positions.Add(b); mesh.Normals.Add(n);
            mesh.Positions.Add(c); mesh.Normals.Add(n);
            int first = mesh.Positions.Count - 3;
            mesh.TriangleIndices.Add(first);
            mesh.TriangleIndices.Add(first + 1);
            mesh.TriangleIndices.Add(first + 2);
        }

        mesh.Freeze();
        return mesh;
    }

    public static MeshGeometry3D CreateBoxMesh(Point3D center, double sx, double sy, double sz)
    {
        MeshGeometry3D unit = CreateCubeMesh(1.0);
        var mesh = new MeshGeometry3D();
        for (int i = 0; i < unit.Positions.Count; i++)
        {
            Point3D p = unit.Positions[i];
            mesh.Positions.Add(new Point3D(center.X + p.X * sx, center.Y + p.Y * sy, center.Z + p.Z * sz));
            // The cube's normals are axis-aligned, so an axis-aligned scale leaves their direction
            // unchanged; copy them straight across rather than re-deriving them.
            mesh.Normals.Add(unit.Normals[i]);
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
        // The board is thin on Z so its face normal is the optical axis: the sensor looks along Z
        // (WorldFrame.SensorLookAxis), and the arm perpendicular to the board face is therefore the Z
        // arm. Previously the board was thin on Y, which made the Y arm poke out of the face — a gizmo
        // whose normal axis (Y) disagreed with its look axis (Z). The front edge is at +Z.
        return CreateColoredBox(new Point3D(0, 0, 0), 0.4, 0.25, 0.03, Color.FromRgb(20, 110, 60));
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
                var point = new Point3D(
                    radius * Math.Sin(phi) * Math.Cos(theta),
                    radius * Math.Cos(phi),
                    radius * Math.Sin(phi) * Math.Sin(theta));
                mesh.Positions.Add(point);
                // Smooth shading: a sphere centred at the origin has its surface normal equal to the
                // unit position vector, so every vertex points radially outward. This is the one shape
                // here that wants smooth (per-vertex) normals rather than flat (per-face) ones.
                var normal = (Vector3D)point;
                normal.Normalize();
                mesh.Normals.Add(normal);
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
