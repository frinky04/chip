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
    public void A_line_that_ends_inside_the_face_cuts_nothing()
    {
        var m = Quad();
        var r = MeshOps.Knife(m, [new Vector3(-1, 0, 1), new Vector3(1, 0, 1)], Above);
        Assert.Single(m.Faces);
        Assert.Empty(r.CutEdges);
        Assert.Equal(4, m.Positions.Count);
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
