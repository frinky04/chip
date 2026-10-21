using System.Numerics;
using Chip.Core.Geometry;

namespace Chip.Core.Tests;

public class TriangulatorTests
{
    /// <summary>A face on the ground (facing up) through the given (x, z) corners.</summary>
    static PolyMesh Ground(params (float X, float Z)[] corners)
    {
        var m = new PolyMesh();
        foreach (var (x, z) in corners) m.Positions.Add(new Vector3(x, 0, z));
        m.Faces.Add(new Face(Enumerable.Range(0, corners.Length).ToArray(), "dev_a", default));
        Assert.True(m.FaceNormal(0).Y > 0.99f);
        return m;
    }

    /// <summary>n - 2 triangles, none flipped, covering exactly the face, and every boundary edge used.</summary>
    static void AssertCovers(PolyMesh m, float? expected = null)
    {
        var tris = new List<int>();
        Triangulator.Triangulate(m, 0, tris);
        var v = m.Faces[0].Verts;
        int n = v.Length;
        Assert.Equal(3 * (n - 2), tris.Count);
        float area = 0;
        var edges = new HashSet<(int, int)>();
        for (int i = 0; i < tris.Count; i += 3)
        {
            var (a, b, c) = (m.Positions[v[tris[i]]], m.Positions[v[tris[i + 1]]], m.Positions[v[tris[i + 2]]]);
            float up = Vector3.Cross(b - a, c - a).Y;
            Assert.True(up >= -1e-5f, "a triangle is wound against the face");
            area += up / 2;
            for (int k = 0; k < 3; k++) edges.Add((tris[i + k], tris[i + (k + 1) % 3]));
        }
        Assert.Equal(expected ?? Topology.FaceArea(m, 0), area, 3);
        for (int i = 0; i < n; i++) Assert.Contains((i, (i + 1) % n), edges);
    }

    [Fact]
    public void A_concave_face_is_filled_without_spilling_outside() =>
        AssertCovers(Ground((0, 0), (0, 4), (4, 4), (4, 3), (1, 3), (1, 0)));

    [Fact]
    public void Straight_through_corners_keep_their_edges() =>
        // A rectangle with extra points on every side, as knife cuts and T-junction repairs leave them.
        AssertCovers(Ground((0, 0), (0, 1), (0, 2), (0, 3), (1, 3), (2, 3), (3, 3), (3, 2), (3, 1), (3, 0), (2, 0), (1, 0)));

    [Fact]
    public void An_l_with_a_straight_run_into_its_inner_corner() =>
        // The shape that made the old ear clipper give up and fan across the outside.
        AssertCovers(Ground((0, 4), (0, 10), (10, 10), (10, 0), (5, 0), (5, 4), (2.5f, 4)));

    [Fact]
    public void A_comb_is_filled_tooth_by_tooth() =>
        AssertCovers(Ground((0, 0), (0, 5), (1, 5), (1, 1), (2, 1), (2, 5), (3, 5), (3, 1), (4, 1), (4, 5), (5, 5), (5, 0)));

    [Fact]
    public void A_far_away_sliver_is_still_filled() =>
        // Float precision at 2 km from the origin, on a 5 cm wide strip with a collinear point.
        AssertCovers(Ground((2000, 2000), (2000, 2003), (2000.05f, 2003), (2000.05f, 2001.5f), (2000.05f, 2000)), 3 * (2000.05f - 2000));
}
