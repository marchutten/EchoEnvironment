using System;
using System.Windows.Media.Media3D;
using EchoEnvironment.Rendering;
using Xunit;

namespace EchoEnvironment.Tests;

/// <summary>
/// Tests for the normals added in MIGRATION_PLAN Phase 7 (defect line 509): the solid meshes built by
/// MeshFactory had no Normals, so under the DirectionalLight WPF shaded them with undefined fallback
/// normals and the faces lit inconsistently. These assert the normals are present and geometrically
/// correct — count, unit length, and direction — which is a stronger check than eyeballing a screenshot.
///
/// What these tests deliberately do NOT claim: that the app now looks right on screen. That is a visual
/// judgement (the same pixel-comparison caveat every rendering phase carries); these pin the geometry so
/// a future change cannot silently drop or mis-orient a normal.
/// </summary>
public class MeshFactoryNormalsTests
{
    private const double Tolerance = 1e-9;

    [Fact]
    public void Cube_HasOneUnitNormalPerVertex_FacingOutward()
    {
        StaRunner.Run(() =>
        {
            MeshGeometry3D mesh = MeshFactory.CreateCubeMesh(2.0);

            // Flat shading: 12 triangles × 3 unshared vertices. Positions, Normals and TriangleIndices
            // stay in lockstep — a normal count that diverges from the position count is the bug.
            Assert.Equal(36, mesh.Positions.Count);
            Assert.Equal(mesh.Positions.Count, mesh.Normals.Count);
            Assert.Equal(mesh.Positions.Count, mesh.TriangleIndices.Count);

            for (int i = 0; i < mesh.Normals.Count; i++)
            {
                Vector3D n = mesh.Normals[i];
                AssertMath.Close(1.0, n.Length, Tolerance, $"cube normal {i} is not unit length");

                // A cube's flat faces are axis-aligned, so every normal is a signed unit axis vector:
                // exactly one component has magnitude 1 and the other two are zero.
                AssertAxisAlignedUnit(n, $"cube normal {i}");

                // Outward, not inward: for a cube centred at the origin the face centroid points the same
                // way as its outward normal, so their dot product is positive.
                Point3D v = mesh.Positions[i];
                Assert.True(Vector3D.DotProduct(n, (Vector3D)v) > 0,
                    $"cube normal {i} points inward: n={n} at vertex {v}");
            }
        });
    }

    [Fact]
    public void Box_CarriesTheCubeNormalsThroughAnAxisAlignedScale()
    {
        StaRunner.Run(() =>
        {
            // CreateBoxMesh scales a unit cube and copies its normals across; an axis-aligned scale does
            // not change an axis-aligned normal's direction, so the box must be flat-shaded like the cube.
            MeshGeometry3D mesh = MeshFactory.CreateBoxMesh(new Point3D(1, 2, 3), 0.4, 0.03, 0.25);

            Assert.Equal(36, mesh.Positions.Count);
            Assert.Equal(mesh.Positions.Count, mesh.Normals.Count);

            foreach (Vector3D n in mesh.Normals)
            {
                AssertMath.Close(1.0, n.Length, Tolerance, "box normal is not unit length");
                AssertAxisAlignedUnit(n, "box normal");
            }
        });
    }

    [Fact]
    public void Sphere_HasSmoothNormals_RadiallyOutward()
    {
        StaRunner.Run(() =>
        {
            const double radius = 4.0;
            // CreateReferenceSphere returns a GeometryModel3D (mesh + material), so read the mesh off it,
            // matching how OrbitCameraControllerTests unwraps CreateBoardModel().Geometry.
            var model = MeshFactory.CreateReferenceSphere(radius);
            MeshGeometry3D mesh = Assert.IsAssignableFrom<MeshGeometry3D>(model.Geometry!);

            // Smooth shading: one normal per vertex, no duplication, so the counts match exactly.
            Assert.Equal(mesh.Positions.Count, mesh.Normals.Count);
            Assert.True(mesh.Positions.Count > 0);

            for (int i = 0; i < mesh.Normals.Count; i++)
            {
                Vector3D n = mesh.Normals[i];
                AssertMath.Close(1.0, n.Length, Tolerance, $"sphere normal {i} is not unit length");

                // A sphere centred at the origin has its surface normal equal to the unit position vector,
                // so each normal must be parallel to (and point the same way as) its own vertex.
                var radial = (Vector3D)mesh.Positions[i];
                radial.Normalize();
                AssertMath.Close(1.0, Vector3D.DotProduct(n, radial), Tolerance,
                    $"sphere normal {i} is not radially outward");
            }
        });
    }

    private static void AssertAxisAlignedUnit(Vector3D n, string label)
    {
        double ax = Math.Abs(n.X), ay = Math.Abs(n.Y), az = Math.Abs(n.Z);
        int onAxis = (ax > 1 - Tolerance ? 1 : 0) + (ay > 1 - Tolerance ? 1 : 0) + (az > 1 - Tolerance ? 1 : 0);
        Assert.Equal(1, onAxis); // exactly one component is a full unit along its axis
        Assert.True(Math.Abs(ax + ay + az - 1.0) < Tolerance, $"{label} is not axis-aligned: {n}");
    }
}
