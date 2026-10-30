using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Scene;
using static Chip.Core.Tests.MeshOpsTests;

namespace Chip.Core.Tests;

public class PushPullTests
{
    static (PolyMesh Mesh, int Face) SplitBoxTop()
    {
        var m = UnitBox();
        MeshOps.ConnectEdges(m, [new(0, 1), new(2, 3), new(4, 5), new(6, 7)]);
        int face = Enumerable.Range(0, m.Faces.Count).Single(f =>
            m.FaceNormal(f).Y > 0.99f && m.FaceCenter(f).X < 0.5f);
        return (m, face);
    }

    [Fact]
    public void Partial_box_top_extrudes_a_complete_outline_without_triangular_gussets()
    {
        var (m, face) = SplitBoxTop();
        var original = m.Faces[face].Verts.ToHashSet();
        var start = m.Positions.ToArray();
        var (move, sides) = MeshOps.PushPullFaces(m, [face], Vector3.UnitY);

        Assert.Equal(4, sides.Count);
        Assert.Equal(4, move.Count);
        Assert.Empty(move.Intersect(original));
        Assert.All(sides, f => Assert.Equal(4, m.Faces[f].Verts.Length));
        foreach (int v in move) m.Positions[v] += Vector3.UnitY;
        for (int v = 0; v < start.Length; v++) Assert.Equal(start[v], m.Positions[v]);
        Assert.True(IsClosedManifold(m));
        Assert.Empty(MeshOps.Check(m));
    }

    [Fact]
    public void Partial_box_top_gesture_merges_walls_and_keeps_a_rectangular_step_with_undo()
    {
        var (mesh, face) = SplitBoxTop();
        var s = new Session();
        var doc = new Document();
        doc.Add(mesh);
        s.Reset(doc, null);
        s.Sel.ConvertTo(SelectMode.Face, doc);
        s.Sel.Add(ElementRef.Face(mesh.Id, face));
        var g = TransformGesture.Start(s, "Push/Pull", GestureKind.PushPull)!;
        g.Apply(Matrix4x4.CreateTranslation(0, 0.25f, 0), rigid: true);
        g.Apply(Matrix4x4.CreateTranslation(0, 0.5f, 0), rigid: true);
        Assert.All(mesh.Faces, f => Assert.True(f.Verts.Length >= 4));
        Assert.True(IsClosedManifold(mesh));
        Assert.Empty(MeshOps.Check(mesh));
        g.Commit();

        var m = s.Doc.Meshes[0];
        Assert.True(IsClosedManifold(m));
        Assert.Empty(MeshOps.Check(m));
        Assert.All(m.Faces, f => Assert.True(f.Verts.Length >= 4));
        Assert.All(m.Positions, p => Assert.True(p.Y == 0 || p.Y == 1 || p.Y == 1.5f));
        var selected = Assert.Single(s.Sel.Items);
        Assert.Equal(1.5f, m.FaceCenter(selected.A).Y);
        Assert.True(s.Undo());
        Assert.Equal(10, s.Doc.Meshes[0].Faces.Count);
        Assert.Equal(1f, s.Doc.Meshes[0].Bounds().Max.Y);
        Assert.True(s.Redo());
        Assert.True(IsClosedManifold(s.Doc.Meshes[0]));
        Assert.Empty(MeshOps.Check(s.Doc.Meshes[0]));
    }

    [Fact]
    public void Separate_outlines_can_resize_and_extrude_in_the_same_operation()
    {
        var (m, face) = SplitBoxTop();
        var box = UnitBox();
        int offset = m.Positions.Count, boxTop = m.Faces.Count + 3;
        m.Positions.AddRange(box.Positions.Select(p => p + new Vector3(3, 0, 0)));
        m.Faces.AddRange(box.Faces.Select(f => f.Derive(f.Verts.Select(v => v + offset).ToArray())));
        var boxCorners = m.Faces[boxTop].Verts.ToHashSet();

        var (move, sides) = MeshOps.PushPullFaces(m, [face, boxTop], Vector3.UnitY);
        Assert.Equal(4, sides.Count);
        Assert.True(boxCorners.IsSubsetOf(move)); // the separate box still slides instead of gaining sides
        foreach (int v in move) m.Positions[v] += Vector3.UnitY;
        Assert.True(IsClosedManifold(m));
        Assert.Empty(MeshOps.Check(m));
    }

    [Fact]
    public void Cancelling_partial_top_push_pull_restores_the_original_topology()
    {
        var (m, face) = SplitBoxTop();
        var positions = m.Positions.ToArray();
        var loops = m.Faces.Select(f => f.Verts.ToArray()).ToArray();
        var doc = new Document();
        doc.Add(m);
        var s = new Session();
        s.Reset(doc, null);
        s.Sel.ConvertTo(SelectMode.Face, doc);
        s.Sel.Add(ElementRef.Face(m.Id, face));
        var g = TransformGesture.Start(s, "Push/Pull", GestureKind.PushPull)!;
        g.Apply(Matrix4x4.CreateTranslation(0, 0.5f, 0), rigid: true);
        g.Cancel();
        var restored = s.Doc.Meshes[0];
        Assert.Equal(positions, restored.Positions);
        Assert.Equal(loops.Length, restored.Faces.Count);
        for (int f = 0; f < loops.Length; f++) Assert.Equal(loops[f], restored.Faces[f].Verts);
        Assert.Null(s.UndoLabel);
    }
}
