using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using static Chip.Core.Tests.ClipTests;

namespace Chip.Core.Tests;

public class BevelTests
{
    static PolyMesh Cube() => Box(Vector3.Zero, new Vector3(2, 2, 2));

    static Edge EdgeAt(PolyMesh m, Vector3 a, Vector3 b) =>
        new(m.Positions.FindIndex(p => p == a), m.Positions.FindIndex(p => p == b));

    /// <summary>The top-front edge of the 2 m cube.</summary>
    static Edge TopFront(PolyMesh m) => EdgeAt(m, new Vector3(0, 2, 2), new Vector3(2, 2, 2));

    [Fact]
    public void One_segment_bevel_of_a_box_edge_is_a_closed_chamfer()
    {
        var m = Cube();
        var added = MeshOps.BevelEdges(m, [TopFront(m)], 0.25f, 1);
        Assert.Single(added);
        Assert.Equal(7, m.Faces.Count);
        Assert.Equal(10, m.Positions.Count);
        Assert.True(IsConsistentlyWound(m));
        Assert.Equal(8f - 0.25f * 0.25f, MeshOps.Volume(m), 4); // a right-triangle prism of legs w, length 2 is removed
        var n = m.FaceNormal(added[0]);
        Assert.True(Vector3.Distance(n, Vector3.Normalize(new Vector3(0, 1, 1))) < 1e-4f);
        Assert.Equal(2, m.Faces.Count(f => f.Verts.Length == 5)); // the end faces lose their corner
    }

    [Fact]
    public void Three_segment_bevel_rounds_the_edge_and_folds_the_ends_into_the_end_faces()
    {
        var m = Cube();
        var added = MeshOps.BevelEdges(m, [TopFront(m)], 0.5f, 3);
        Assert.Equal(3, added.Count);
        Assert.Equal(9, m.Faces.Count);
        Assert.Equal(6 + 2 * 4, m.Positions.Count);
        Assert.True(IsConsistentlyWound(m));
        Assert.Equal(2, m.Faces.Count(f => f.Verts.Length == 7)); // end faces: corner replaced by a 4-point arc
        Assert.Equal(2, m.SoftEdges.Count); // between the three bands
        // A rounded profile removes less than the chamfer of the same width but more than nothing.
        float vol = MeshOps.Volume(m);
        Assert.True(vol < 8f && vol > 8f - 0.25f);
        // Every arc point is width away from the arc centre (circular for 90° corners).
        var center = new Vector3(0, 1.5f, 1.5f);
        foreach (var p in m.Positions.Where(p => p.Y > 1.49f && p.Z > 1.49f && p.X == 0))
            Assert.Equal(0.5f, Vector3.Distance(p, center), 4);
    }

    [Fact]
    public void Bevelling_every_box_edge_patches_the_corners()
    {
        var m = Cube();
        var added = MeshOps.BevelEdges(m, m.Edges().ToList(), 0.25f, 1);
        Assert.Equal(12 + 8, added.Count);
        Assert.Equal(26, m.Faces.Count);
        Assert.True(IsConsistentlyWound(m));
        Assert.Equal(24, m.Positions.Count);
        Assert.Equal(8, m.Faces.Count(f => f.Verts.Length == 3));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Rounded_bevel_of_every_box_edge_stays_closed(int segments)
    {
        var m = Cube();
        MeshOps.BevelEdges(m, m.Edges().ToList(), 0.25f, segments);
        Assert.True(IsConsistentlyWound(m));
        Assert.Equal(6 + 12 * segments + 8 * 3 * segments, m.Faces.Count); // curved corners become fans
        Assert.True(MeshOps.Volume(m) < 8f);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Two_edges_meeting_at_a_corner_stay_closed(int segments)
    {
        var m = Cube();
        var corner = new Vector3(2, 2, 2);
        var edges = new[] { EdgeAt(m, new Vector3(0, 2, 2), corner), EdgeAt(m, new Vector3(2, 2, 0), corner) };
        MeshOps.BevelEdges(m, edges, 0.25f, segments);
        Assert.True(IsConsistentlyWound(m));
        if (segments == 1) Assert.Equal(8, m.Faces.Count);
    }

    [Fact]
    public void Vertex_bevel_chamfers_the_corner()
    {
        var m = Cube();
        int v = m.Positions.FindIndex(p => p == new Vector3(2, 2, 2));
        var added = MeshOps.BevelVertices(m, [v], 0.5f);
        Assert.Single(added);
        Assert.Equal(7, m.Faces.Count);
        Assert.True(IsConsistentlyWound(m));
        Assert.Equal(8f - 0.5f * 0.5f * 0.5f / 6, MeshOps.Volume(m), 4);
    }

    [Fact]
    public void Bevel_faces_take_the_adjacent_material_and_are_world_aligned()
    {
        var m = Cube();
        foreach (var f in m.Faces) f.Material = "dev_b";
        var added = MeshOps.BevelEdges(m, [TopFront(m)], 0.25f, 2);
        foreach (var f in added)
        {
            Assert.Equal("dev_b", m.Faces[f].Material);
            var aligned = TextureMapping.AlignToGrid(m.FaceNormal(f), TextureMapping.Default);
            Assert.Equal(aligned.UAxis, m.Faces[f].Map.UAxis);
        }
    }

    [Fact]
    public void Width_is_clamped_so_points_never_slide_past_the_next_vertex()
    {
        var m = Cube();
        MeshOps.BevelEdges(m, [TopFront(m)], 10f, 1);
        Assert.True(IsConsistentlyWound(m));
        Assert.All(m.Positions, p => Assert.True(p.Y >= -1e-4f && p.Z >= -1e-4f));
    }

    [Fact]
    public void Open_border_edges_are_skipped()
    {
        var m = Cube();
        int top = Enumerable.Range(0, 6).Single(f => m.FaceNormal(f).Y > 0.9f);
        MeshOps.DeleteFaces(m, [top]);
        var added = MeshOps.BevelEdges(m, [TopFront(m)], 0.25f, 1);
        Assert.Empty(added);
        Assert.Equal(5, m.Faces.Count);
    }

    [Fact]
    public void Bevel_is_one_undoable_step()
    {
        var doc = new Document();
        var box = doc.Add(Cube());
        var s = new Session();
        s.Reset(doc, null);
        s.Do("Bevel", () => MeshOps.BevelEdges(s.Doc.Find(box.Id)!, [TopFront(s.Doc.Find(box.Id)!)], 0.25f, 3));
        Assert.Equal(9, s.Doc.Meshes[0].Faces.Count);
        s.Undo();
        Assert.Equal(6, s.Doc.Meshes[0].Faces.Count);
        Assert.Equal(8, s.Doc.Meshes[0].Positions.Count);
    }
}
