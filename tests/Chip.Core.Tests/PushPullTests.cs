using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Scene;
using static Chip.Core.Tests.MeshOpsTests;

namespace Chip.Core.Tests;

public class PushPullTests
{
    [Theory]
    [InlineData(0.5f)]
    [InlineData(-0.5f)]
    public void Absorbing_strips_preserves_existing_wall_material_seams(float distance)
    {
        var (m, top) = SplitBoxTop();
        var front = Enumerable.Range(0, m.Faces.Count).Where(f => m.FaceNormal(f).Z < -0.99f).ToArray();
        foreach (int f in front) m.Faces[f].Material = m.FaceCenter(f).X < 0.5f ? "left" : "right";
        var doc = new Document();
        doc.Add(m);
        var s = new Session();
        s.Reset(doc, null);
        s.Sel.ConvertTo(SelectMode.Face, doc);
        s.Sel.Add(ElementRef.Face(m.Id, top));
        var g = TransformGesture.Start(s, "Push/Pull", GestureKind.PushPull)!;
        g.Apply(Matrix4x4.CreateTranslation(0, distance, 0), rigid: true);
        g.Commit();
        Assert.True(IsClosedManifold(m));
        Assert.Empty(MeshOps.Check(m));
        int left = m.Faces.IndexOf(Assert.Single(m.Faces, f => f.Material == "left"));
        int right = m.Faces.IndexOf(Assert.Single(m.Faces, f => f.Material == "right"));
        Assert.Equal(0.5f * (1 + distance), Topology.FaceArea(m, left), 4);
        Assert.Equal(0.5f, Topology.FaceArea(m, right), 4);
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(-0.5f)]
    public void Partial_top_trims_or_extends_uncut_walls_during_the_drag(float distance)
    {
        var m = UnitBox();
        // Only split the top. The front/back walls remain single polygons with an extra rim corner.
        MeshOps.ConnectEdges(m, [new(2, 3), new(6, 7)]);
        int top = Enumerable.Range(0, m.Faces.Count).Single(f =>
            m.FaceNormal(f).Y > 0.99f && m.FaceCenter(f).X < 0.5f);
        m.Faces[0].Material = "different wall material";
        m.Faces[0].Tags = TagSet.Of("wall");
        m.Faces[0].UVs = m.FacePoints(0).Select(p => new Vector2(p.Z, p.Y)).ToArray();
        var doc = new Document();
        doc.Add(m);
        var s = new Session();
        s.Reset(doc, null);
        s.Sel.ConvertTo(SelectMode.Face, doc);
        s.Sel.Add(ElementRef.Face(m.Id, top));
        var g = TransformGesture.Start(s, "Push/Pull", GestureKind.PushPull)!;
        foreach (float d in new[] { distance, -distance, distance })
        {
            g.Apply(Matrix4x4.CreateTranslation(0, d, 0), rigid: true);
            Assert.True(IsClosedManifold(m));
            Assert.Empty(MeshOps.Check(m));
            float AreaOnPlane(int axis, float value) => Enumerable.Range(0, m.Faces.Count)
                .Where(f => m.FacePoints(f).All(p => MathF.Abs(p[axis] - value) < 1e-5f))
                .Sum(f => Topology.FaceArea(m, f));
            Assert.Equal(1 + d, AreaOnPlane(0, 0), 4);
            Assert.Equal(1, AreaOnPlane(0, 1), 4);
            Assert.Equal(1 + 0.5f * d, AreaOnPlane(2, 0), 4);
            Assert.Equal(1 + 0.5f * d, AreaOnPlane(2, 1), 4);
            var wall = Assert.Single(m.Faces, f => f.Material == "different wall material");
            Assert.Equal(TagSet.Of("wall"), wall.Tags);
            Assert.NotNull(wall.UVs);
            for (int i = 0; i < wall.Verts.Length; i++)
            {
                var p = m.Positions[wall.Verts[i]];
                Assert.True(Vector2.Distance(new(p.Z, p.Y), wall.UVs[i]) < 1e-4f);
            }
            Assert.Equal(1 + d, m.FaceCenter(Assert.Single(s.Sel.Items).A).Y, 4);
        }
        g.Commit();
        Assert.Empty(MeshOps.Check(m));
    }

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
