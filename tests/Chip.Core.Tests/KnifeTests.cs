using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Texturing;
using static Chip.Core.Tests.ClipTests;

namespace Chip.Core.Tests;

public class KnifeTests
{
    static readonly Vector3 Above = new(1, 10, 1);

    /// <summary>One upward-facing 2×2 quad on the ground, x and z in [0, 2].</summary>
    static PolyMesh Quad()
    {
        var m = new PolyMesh();
        m.Positions.AddRange([new(0, 0, 0), new(0, 0, 2), new(2, 0, 2), new(2, 0, 0)]);
        m.Faces.Add(new Face([0, 1, 2, 3], "dev_a", TextureMapping.AlignToGrid(Vector3.UnitY, TextureMapping.Default)));
        Assert.True(m.FaceNormal(0).Y > 0.99f);
        return m;
    }

    [Fact]
    public void A_line_across_a_quad_splits_it_in_two()
    {
        var m = Quad();
        var r = MeshOps.Knife(m, [new Vector3(-1, 0, 1), new Vector3(3, 0, 1)], Above);
        Assert.Equal(2, m.Faces.Count);
        Assert.Single(r.NewFaces);
        Assert.Single(r.CutEdges);
        Assert.Equal(6, m.Positions.Count);
        Assert.Contains(m.Positions, p => Vector3.Distance(p, new Vector3(0, 0, 1)) < 1e-4f);
        Assert.Contains(m.Positions, p => Vector3.Distance(p, new Vector3(2, 0, 1)) < 1e-4f);
        Assert.All(m.Faces, f => Assert.Equal(4, f.Verts.Length));
        Assert.All(Enumerable.Range(0, 2), f => Assert.True(m.FaceNormal(f).Y > 0.99f));
        Assert.Equal(4f, Topology.FaceArea(m, 0) + Topology.FaceArea(m, 1), 4);
    }

    [Fact]
    public void A_bent_line_adds_its_inner_point_as_a_vertex()
    {
        var m = Quad();
        var r = MeshOps.Knife(m, [new Vector3(-1, 0, 0.5f), new Vector3(1, 0, 1.5f), new Vector3(3, 0, 0.5f)], Above);
        Assert.Equal(2, m.Faces.Count);
        Assert.Equal(2, r.CutEdges.Count);
        Assert.Contains(m.Positions, p => Vector3.Distance(p, new Vector3(1, 0, 1.5f)) < 1e-4f);
        Assert.Equal(4f, Topology.FaceArea(m, 0) + Topology.FaceArea(m, 1), 4);
    }

    [Fact]
    public void A_line_that_ends_inside_the_face_carries_on_to_the_far_border()
    {
        var m = Quad();
        var r = MeshOps.Knife(m, [new Vector3(-1, 0, 1), new Vector3(1, 0, 1)], Above);
        Assert.Equal(2, m.Faces.Count);
        Assert.Single(r.CutEdges);
        Assert.Equal(6, m.Positions.Count); // no vertex where the line stopped
        Assert.Contains(m.Positions, p => Vector3.Distance(p, new Vector3(2, 0, 1)) < 1e-4f);
    }

    [Fact]
    public void A_line_wholly_inside_a_face_extends_both_ways()
    {
        var m = Quad();
        MeshOps.Knife(m, [new Vector3(0.5f, 0, 1), new Vector3(1.5f, 0, 1)], Above);
        Assert.Equal(2, m.Faces.Count);
        Assert.Contains(m.Positions, p => Vector3.Distance(p, new Vector3(0, 0, 1)) < 1e-4f);
        Assert.Contains(m.Positions, p => Vector3.Distance(p, new Vector3(2, 0, 1)) < 1e-4f);
        Assert.Equal(4f, Topology.FaceArea(m, 0) + Topology.FaceArea(m, 1), 4);
    }

    [Fact]
    public void An_extension_that_reaches_a_corner_splits_through_it()
    {
        var m = Quad();
        MeshOps.Knife(m, [new Vector3(0.5f, 0, 0.5f), new Vector3(1, 0, 1)], Above);
        Assert.Equal(2, m.Faces.Count);
        Assert.Equal(4, m.Positions.Count); // diagonal corner to corner: no new vertices
        Assert.All(m.Faces, f => Assert.Equal(3, f.Verts.Length));
    }

    [Fact]
    public void The_extension_stays_inside_the_face_the_line_ends_in()
    {
        // Two coplanar quads sharing the x = 2 edge. The line enters the first and stops inside it.
        var m = Quad();
        m.Positions.AddRange([new(4, 0, 2), new(4, 0, 0)]);
        m.Faces.Add(new Face([3, 2, 4, 5], "dev_a", m.Faces[0].Map));
        Assert.True(m.FaceNormal(1).Y > 0.99f);

        MeshOps.Knife(m, [new Vector3(-1, 0, 1), new Vector3(1, 0, 1)], Above);
        Assert.Equal(3, m.Faces.Count); // first quad split, second only gains the shared-edge vertex
        Assert.Equal(5, m.Faces[1].Verts.Length);
        Assert.DoesNotContain(m.Positions, p => Vector3.Distance(p, new Vector3(4, 0, 1)) < 1e-4f);
    }

    [Fact]
    public void Cutting_a_box_top_keeps_it_closed_and_splits_the_shared_edges()
    {
        var box = Box(Vector3.Zero, new Vector3(2, 2, 2));
        int top = Enumerable.Range(0, 6).Single(f => box.FaceNormal(f).Y > 0.9f);
        MeshOps.Knife(box, [new Vector3(-1, 2, 1), new Vector3(3, 2, 1)], new Vector3(1, 12, 1), [top]);
        Assert.Equal(7, box.Faces.Count);
        Assert.True(IsConsistentlyWound(box));
        Assert.Equal(8f, MeshOps.Volume(box), 3);
        Assert.Equal(2, box.Faces.Count(f => f.Verts.Length == 5)); // the two side faces gained the cut vertex
    }

    [Fact]
    public void A_line_across_two_faces_from_above_cuts_only_what_faces_the_eye()
    {
        var box = Box(Vector3.Zero, new Vector3(2, 2, 2));
        MeshOps.Knife(box, [new Vector3(-1, 2, 1), new Vector3(3, 2, 1)], new Vector3(1, 12, 1));
        Assert.Equal(7, box.Faces.Count); // the bottom faces away and is untouched
        Assert.True(IsConsistentlyWound(box));
    }

    /// <summary>A 10×10 floor quad, x and z in [0, 10], facing up.</summary>
    static PolyMesh Floor()
    {
        var m = new PolyMesh();
        m.Positions.AddRange([new(0, 0, 0), new(0, 0, 10), new(10, 0, 10), new(10, 0, 0)]);
        m.Faces.Add(new Face([0, 1, 2, 3], "dev_a", TextureMapping.AlignToGrid(Vector3.UnitY, TextureMapping.Default)));
        return m;
    }

    static readonly Vector3 InFront = new(5, 3, 14); // low and in front, like a player camera in a room

    static Vector3 Ground(float x, float z) => new(x, 0, z);

    /// <summary>Every face is a simple polygon whose triangles cover exactly its area, and the pieces still add up.</summary>
    internal static void AssertClean(PolyMesh m, float total)
    {
        Assert.Empty(MeshOps.Check(m));
        float sum = 0;
        for (int f = 0; f < m.Faces.Count; f++)
        {
            Assert.True(m.FaceNormal(f).Y > 0.99f, $"face {f} flipped");
            float area = Topology.FaceArea(m, f);
            Assert.Equal(area, TriangleArea(m, f), 3);
            sum += area;
        }
        Assert.Equal(total, sum, 3);
    }

    static float TriangleArea(PolyMesh m, int f)
    {
        var tris = new List<int>();
        Triangulator.Triangulate(m, f, tris);
        var v = m.Faces[f].Verts;
        float area = 0;
        for (int i = 0; i < tris.Count; i += 3)
            area += Vector3.Cross(m.Positions[v[tris[i + 1]]] - m.Positions[v[tris[i]]], m.Positions[v[tris[i + 2]]] - m.Positions[v[tris[i]]]).Length() / 2;
        return area;
    }

    static int FaceWithArea(PolyMesh m, float area) =>
        Enumerable.Range(0, m.Faces.Count).Single(f => MathF.Abs(Topology.FaceArea(m, f) - area) < 1e-3f);

    [Theory]
    [InlineData(5, 3, 14)]
    [InlineData(5, 20, 5)]
    public void A_stroke_that_crosses_itself_cuts_out_the_square_it_loops_round(float ex, float ey, float ez)
    {
        // From the left wall, right, down, left, then up to the far wall: the last leg crosses the first.
        var m = Floor();
        var r = MeshOps.Knife(m, [Ground(0, 4), Ground(7, 4), Ground(7, 6), Ground(5, 6), Ground(5, 0)], new Vector3(ex, ey, ez));
        // The corner, the square, and the rest: that wraps round the square, touching it only where the stroke crossed,
        // so a face can't hold it whole and one bridge splits it in two.
        Assert.Equal(4, m.Faces.Count);
        AssertClean(m, 100);
        Assert.Equal(4, m.Faces[FaceWithArea(m, 4)].Verts.Length); // the square
        Assert.Equal(4, m.Faces[FaceWithArea(m, 20)].Verts.Length); // the corner between the walls and the cut
        Assert.Contains(m.Positions, p => Vector3.Distance(p, Ground(5, 4)) < 1e-4f); // where the stroke crossed itself
        Assert.Equal(7, r.CutEdges.Count); // the stroke's six pieces and the bridge
    }

    [Fact]
    public void A_closed_loop_inside_a_face_becomes_its_own_face_bridged_to_the_border()
    {
        var m = Floor();
        MeshOps.Knife(m, [Ground(3, 3), Ground(6, 3), Ground(6, 6), Ground(3, 6), Ground(3, 3)], InFront);
        AssertClean(m, 100);
        Assert.Equal(4, m.Faces[FaceWithArea(m, 9)].Verts.Length);
        Assert.Equal(3, m.Faces.Count); // the hole and the two halves round it
    }

    [Fact]
    public void A_loop_hanging_off_the_border_by_one_point_gets_one_bridge()
    {
        var m = Floor();
        MeshOps.Knife(m, [Ground(0, 5), Ground(3, 3), Ground(3, 7), Ground(0, 5)], InFront);
        AssertClean(m, 100);
        Assert.Equal(3, m.Faces[FaceWithArea(m, 6)].Verts.Length);
        Assert.Equal(3, m.Faces.Count);
    }

    [Fact]
    public void A_stroke_that_ends_on_its_own_line_closes_the_loop_instead_of_running_on()
    {
        // In from the left, round, and back down at a slant onto the first leg at (4, 2).
        var m = Floor();
        MeshOps.Knife(m, [Ground(-1, 2), Ground(6, 2), Ground(6, 6), Ground(2, 6), Ground(4, 2)], InFront);
        AssertClean(m, 100);
        Assert.Contains(m.Faces, f => f.Verts.Length == 4 && f.Verts.Any(v => Vector3.Distance(m.Positions[v], Ground(2, 6)) < 1e-4f)
            && f.Verts.Any(v => Vector3.Distance(m.Positions[v], Ground(4, 2)) < 1e-4f));
        // Running on would have carried the slant to the near edge at (5, 0).
        Assert.DoesNotContain(m.Positions, p => Vector3.Distance(p, Ground(5, 0)) < 0.1f);
    }

    [Fact]
    public void A_stroke_through_its_own_loop_cuts_every_region_it_makes()
    {
        var m = Floor();
        MeshOps.Knife(m, [Ground(-1, 2), Ground(6, 2), Ground(6, 6), Ground(2, 6), Ground(2, 3), Ground(8, 3), Ground(8, 11)], InFront);
        AssertClean(m, 100);
        Assert.Equal(4, m.Faces[FaceWithArea(m, 12)].Verts.Length); // inside the loop, above the stroke's second pass
    }

    [Fact]
    public void Cutting_a_face_that_crosses_itself_untangles_it()
    {
        // What the old knife made of the stroke above: a rectangle and a square joined where the outline crosses at (5, 4).
        var m = new PolyMesh();
        m.Positions.AddRange([Ground(5, 0), Ground(0, 0), Ground(0, 4), Ground(7, 4), Ground(7, 6), Ground(5, 6)]);
        m.Faces.Add(new Face([0, 1, 2, 3, 4, 5], "dev_a", default));
        Assert.Equal(MeshIssueKind.SelfIntersectingFace, MeshOps.FaceIssue(m, 0));
        MeshOps.Knife(m, [Ground(-1, 2), Ground(11, 2)], new Vector3(3, 20, 3), frontFacingOnly: false);
        Assert.Equal(2, m.Faces.Count); // the rectangle, cut in two; the square was wound backwards, so it was never filled
        AssertClean(m, 20);
    }

    [Fact]
    public void A_line_along_an_existing_edge_cuts_nothing()
    {
        var m = Floor();
        var r = MeshOps.Knife(m, [Ground(-1, 0), Ground(11, 0)], InFront);
        Assert.Empty(r.NewFaces);
        Assert.Single(m.Faces);
        Assert.Equal(4, m.Faces[0].Verts.Length);
    }

    [Fact]
    public void A_concave_face_is_cut_only_where_the_line_is_inside_it()
    {
        // An L: the line runs across both arms and over the notch between them.
        var m = new PolyMesh();
        m.Positions.AddRange([Ground(0, 0), Ground(0, 4), Ground(4, 4), Ground(4, 3), Ground(1, 3), Ground(1, 0)]);
        m.Faces.Add(new Face([0, 1, 2, 3, 4, 5], "dev_a", default));
        Assert.True(m.FaceNormal(0).Y > 0.99f);
        MeshOps.Knife(m, [Ground(0.5f, -1), Ground(3, 5)], new Vector3(2, 20, 2));
        AssertClean(m, 7);
    }

    [Fact]
    public void Explicit_uvs_and_material_carry_onto_the_pieces()
    {
        var m = Quad();
        m.Faces[0].Material = "dev_b";
        m.Faces[0].UVs = m.Faces[0].Verts.Select(v => new Vector2(m.Positions[v].X, m.Positions[v].Z) * 0.5f).ToArray();
        MeshOps.Knife(m, [new Vector3(-1, 0, 1), new Vector3(3, 0, 1)], Above);
        foreach (var f in m.Faces)
        {
            Assert.Equal("dev_b", f.Material);
            for (int i = 0; i < f.Verts.Length; i++)
            {
                var p = m.Positions[f.Verts[i]];
                Assert.True(Vector2.Distance(new Vector2(p.X, p.Z) * 0.5f, f.UVs![i]) < 1e-4f);
            }
        }
    }
}
