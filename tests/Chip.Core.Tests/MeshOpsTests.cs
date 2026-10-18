using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Core.Tests;

/// <summary>Stream A mesh operations: topology stays valid, surfaces propagate, counts are exact.</summary>
public class MeshOpsTests
{
    // CreateBox faces: 0 -X, 1 +X, 2 -Y, 3 +Y [2,6,7,3], 4 -Z, 5 +Z. Vertex i: bit0 = max X, bit1 = max Y, bit2 = max Z.
    const int Top = 3;

    internal static PolyMesh Box(Vector3 min, Vector3 max, string material = "dev_a") =>
        MeshOps.CreateBox(min, max, material, TextureMapping.Default);

    internal static PolyMesh UnitBox() => Box(Vector3.Zero, Vector3.One);

    /// <summary>Every edge is shared by exactly two faces running opposite ways.</summary>
    internal static bool IsClosedManifold(PolyMesh m) =>
        m.Faces.SelectMany(f => f.Edges()).GroupBy(e => e).All(g => g.Count() == 2) && IsConsistentlyOriented(m);

    /// <summary>No directed edge appears twice, so neighbouring faces agree on which side is out.</summary>
    internal static bool IsConsistentlyOriented(PolyMesh m)
    {
        var seen = new HashSet<(int, int)>();
        foreach (var f in m.Faces)
            for (int i = 0; i < f.Verts.Length; i++)
                if (!seen.Add((f.Verts[i], f.Verts[(i + 1) % f.Verts.Length]))) return false;
        return true;
    }

    internal static void AssertValid(PolyMesh m)
    {
        foreach (var f in m.Faces)
        {
            Assert.True(f.Verts.Length >= 3);
            Assert.All(f.Verts, v => Assert.InRange(v, 0, m.Positions.Count - 1));
            if (f.UVs != null) Assert.Equal(f.Verts.Length, f.UVs.Length);
        }
        var live = m.Edges().ToHashSet();
        Assert.All(m.SoftEdges, e => Assert.Contains(e, live));
    }

    static void AssertOutward(PolyMesh m, Vector3 inside)
    {
        for (int f = 0; f < m.Faces.Count; f++)
            Assert.True(Vector3.Dot(m.FaceNormal(f), m.FaceCenter(f) - inside) > 0, $"face {f} points inward");
    }

    /// <summary>An open strip of unit quads along +X in the XZ plane, facing up.</summary>
    static PolyMesh Strip(int quads)
    {
        var m = new PolyMesh();
        for (int i = 0; i <= quads; i++) { m.Positions.Add(new(i, 0, 0)); m.Positions.Add(new(i, 0, 1)); }
        for (int i = 0; i < quads; i++)
            m.Faces.Add(new Face([2 * i, 2 * i + 1, 2 * i + 3, 2 * i + 2], "dev_a", TextureMapping.AlignToGrid(Vector3.UnitY, TextureMapping.Default)));
        return m;
    }

    // ---- Connect ----

    [Fact]
    public void Connecting_two_corners_of_a_face_splits_it_and_keeps_the_box_closed()
    {
        var m = UnitBox();
        m.Faces[Top].Material = "top";
        var chords = MeshOps.ConnectVertices(m, [2, 7]);
        Assert.Equal([new Edge(2, 7)], chords);
        Assert.Equal(7, m.Faces.Count);
        Assert.True(IsClosedManifold(m));
        Assert.Equal(2, m.Faces.Count(f => f.Material == "top"));
        Assert.All(m.Faces.Where(f => f.Material == "top"), f => Assert.Equal(3, f.Verts.Length));
        AssertValid(m);
    }

    [Fact]
    public void Connecting_adjacent_corners_changes_nothing()
    {
        var m = UnitBox();
        Assert.Empty(MeshOps.ConnectVertices(m, [2, 6]));
        Assert.Equal(6, m.Faces.Count);
    }

    [Fact]
    public void Connecting_an_edge_ring_splits_each_side_face_once()
    {
        var m = UnitBox();
        m.Faces[0].UVs = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        var chords = MeshOps.ConnectEdges(m, [new(0, 2), new(1, 3), new(4, 6), new(5, 7)]);
        Assert.Equal(4, chords.Count);
        Assert.Equal(10, m.Faces.Count);
        Assert.Equal(12, m.Positions.Count);
        Assert.True(IsClosedManifold(m));
        Assert.All(chords, e => Assert.Equal(0.5f, m.Positions[e.A].Y, 5));
        AssertValid(m);
        AssertOutward(m, new Vector3(0.5f));
    }

    [Fact]
    public void Connecting_a_single_edge_inserts_its_midpoint_without_opening_the_mesh()
    {
        var m = UnitBox();
        m.SoftEdges.Add(new Edge(0, 2));
        Assert.Empty(MeshOps.ConnectEdges(m, [new(0, 2)]));
        Assert.Equal(6, m.Faces.Count);
        Assert.Equal(9, m.Positions.Count);
        Assert.True(IsClosedManifold(m));
        Assert.Equal(2, m.SoftEdges.Count); // both halves stay soft
        AssertValid(m);
    }

    // ---- Merge faces ----

    [Fact]
    public void Merging_split_faces_restores_one_face_with_the_kept_surface()
    {
        var m = UnitBox();
        MeshOps.ConnectVertices(m, [2, 7]);
        m.Faces[6].Material = "other";
        var r = MeshOps.MergeFaces(m, [Top, 6], keep: Top);
        Assert.Equal(6, m.Faces.Count);
        Assert.True(IsClosedManifold(m));
        Assert.Single(r.Merged);
        Assert.Equal("dev_a", m.Faces[r.Merged[0]].Material);
        Assert.Equal(4, m.Faces[r.Merged[0]].Verts.Length);
        Assert.Equal(-1, r.FaceMap[6]);
        AssertValid(m);
    }

    [Fact]
    public void Merging_coplanar_quads_drops_corners_nothing_else_uses()
    {
        var m = Strip(2);
        var r = MeshOps.MergeFaces(m, [0, 1]);
        Assert.Single(m.Faces);
        Assert.Equal(4, m.Faces[0].Verts.Length);
        Assert.Equal(4, m.Positions.Count);
        Assert.Equal(0, r.Skipped);
    }

    [Fact]
    public void A_closed_shell_cannot_merge_into_one_face()
    {
        var m = UnitBox();
        var r = MeshOps.MergeFaces(m, Enumerable.Range(0, 6));
        Assert.Equal(1, r.Skipped);
        Assert.Equal(6, m.Faces.Count);
    }

    // ---- Bridge ----

    static PolyMesh TwoBoxes()
    {
        var m = Box(Vector3.Zero, Vector3.One);
        MeshOps.MergeMeshes(m, [Box(new Vector3(3, 0, 0), new Vector3(4, 1, 1), "dev_b")]);
        return m;
    }

    [Fact]
    public void Bridging_facing_faces_makes_one_closed_tube()
    {
        var m = TwoBoxes();
        var quads = MeshOps.BridgeFaces(m, 1, 6, out var error); // +X of the first box, -X of the second
        Assert.Null(error);
        Assert.Equal(4, quads!.Count);
        Assert.Equal(14, m.Faces.Count);
        Assert.True(IsClosedManifold(m));
        foreach (var q in quads)
        {
            Assert.Equal("dev_a", m.Faces[q].Material);
            var c = m.FaceCenter(q);
            var outward = c - new Vector3(c.X, 0.5f, 0.5f);
            Assert.True(Vector3.Dot(m.FaceNormal(q), outward) > 0);
            var xs = m.Faces[q].Verts.Select(v => m.Positions[v].X).Distinct().Order().ToArray();
            Assert.Equal([1f, 3f], xs); // straight across, not twisted
        }
        AssertValid(m);
    }

    [Fact]
    public void Bridging_faces_with_different_corner_counts_explains_why_not()
    {
        var m = TwoBoxes();
        MeshOps.ConnectVertices(m, [8, 14]); // splits the second box's -X face into triangles
        Assert.Null(MeshOps.BridgeFaces(m, 1, 6, out var error));
        Assert.Contains("same number of corners", error);
    }

    [Fact]
    public void Bridging_two_open_box_ends_by_their_edge_loops_closes_the_mesh()
    {
        var m = TwoBoxes();
        var loopA = m.Faces[1].Edges().ToList();
        var loopB = m.Faces[6].Edges().ToList();
        MeshOps.DeleteFaces(m, [1, 6]); // no vertex is orphaned, so indices stay
        var quads = MeshOps.BridgeEdges(m, loopA.Concat(loopB), out var error);
        Assert.Null(error);
        Assert.Equal(4, quads!.Count);
        Assert.True(IsClosedManifold(m));
    }

    [Fact]
    public void Bridging_two_open_edges_joins_the_planes_with_a_consistent_quad()
    {
        var m = Strip(1);
        MeshOps.MergeMeshes(m, [Shifted(Strip(1), new Vector3(2, 0, 0))]);
        var quads = MeshOps.BridgeEdges(m, [new Edge(2, 3), new Edge(4, 5)], out var error);
        Assert.Null(error);
        int quad = Assert.Single(quads!);
        Assert.True(IsConsistentlyOriented(m));
        Assert.True(m.FaceNormal(quad).Y > 0.99f);
        Assert.Null(MeshOps.BridgeEdges(m, [new Edge(0, 1)], out var single)); // one chain is not enough
        Assert.NotNull(single);
    }

    static PolyMesh Shifted(PolyMesh m, Vector3 d)
    {
        for (int i = 0; i < m.Positions.Count; i++) m.Positions[i] += d;
        return m;
    }

    // ---- Inset / outset ----

    [Fact]
    public void Inset_shrinks_the_face_inside_a_ring_and_keeps_the_box_closed()
    {
        var m = Box(Vector3.Zero, new Vector3(2, 2, 2));
        m.Faces[Top].Material = "top";
        var r = MeshOps.Inset(m, [Top]);
        Assert.Equal(0.98f, r.MaxInset, 3);
        r.Apply(m, 0.5f, outset: false);
        Assert.Equal(10, m.Faces.Count);
        Assert.Equal(4, r.RingFaces.Count);
        Assert.True(IsClosedManifold(m));
        Assert.All(m.Faces[Top].Verts.Select(v => m.Positions[v]), p =>
        {
            Assert.InRange(p.X, 0.499f, 1.501f);
            Assert.InRange(p.Z, 0.499f, 1.501f);
            Assert.Equal(2f, p.Y, 5);
        });
        Assert.All(r.RingFaces, f =>
        {
            Assert.Equal("top", m.Faces[f].Material);
            Assert.True(m.FaceNormal(f).Y > 0.99f);
        });
        AssertValid(m);
    }

    [Fact]
    public void Inset_of_a_region_keeps_inner_edges_shared()
    {
        var m = Strip(2);
        var r = MeshOps.Inset(m, [0, 1]);
        r.Apply(m, 0.25f, outset: false);
        Assert.Equal(6, r.RingFaces.Count);
        Assert.Equal(8, m.Faces.Count);
        Assert.True(IsConsistentlyOriented(m));
        var shared = m.Faces[0].Edges().Intersect(m.Faces[1].Edges()).Single();
        Assert.Equal(1f, m.Positions[shared.A].X, 5);
        Assert.Equal(0.75f - 0.25f, MathF.Abs(m.Positions[shared.A].Z - m.Positions[shared.B].Z), 5);
    }

    [Fact]
    public void Outset_keeps_the_face_and_pushes_the_old_outline_out()
    {
        var m = Box(Vector3.Zero, new Vector3(2, 2, 2));
        var r = MeshOps.Inset(m, [Top]);
        r.Apply(m, 0.5f, outset: true);
        Assert.True(IsClosedManifold(m));
        var top = m.Faces[Top].Verts.Select(v => m.Positions[v]).ToList();
        Assert.Equal(0f, top.Min(p => p.X), 5);
        Assert.Equal(2f, top.Max(p => p.X), 5);
        Assert.Equal(-0.5f, m.Bounds().Min.X, 5);
        Assert.Equal(2.5f, m.Bounds().Max.X, 5);
    }

    // ---- Collapse / weld ----

    [Fact]
    public void Collapsing_a_box_top_makes_a_pyramid()
    {
        var m = UnitBox();
        var (_, faceMap, points) = MeshOps.Collapse(m, [m.Faces[Top].Verts]);
        Assert.Equal(-1, faceMap[Top]);
        Assert.Equal(5, m.Faces.Count);
        Assert.Equal(5, m.Positions.Count);
        Assert.True(IsClosedManifold(m));
        var apex = m.Positions[Assert.Single(points)];
        Assert.Equal(new Vector3(0.5f, 1, 0.5f), apex);
        AssertValid(m);
    }

    [Fact]
    public void Collapsing_an_edge_keeps_the_mesh_closed()
    {
        var m = UnitBox();
        MeshOps.Collapse(m, [[2, 3]]);
        Assert.Equal(7, m.Positions.Count);
        Assert.Equal(6, m.Faces.Count);
        Assert.True(IsClosedManifold(m));
    }

    [Fact]
    public void Weld_merges_vertices_within_a_millimetre_only()
    {
        var m = Box(Vector3.Zero, Vector3.One);
        MeshOps.MergeMeshes(m, [Box(new Vector3(1.0005f, 0, 0), new Vector3(2, 1, 1))]);
        var r = MeshOps.WeldOverlapping(m);
        Assert.Equal(4, r!.Value.Welded);
        Assert.Equal(12, m.Positions.Count);
        AssertValid(m);

        var far = Box(Vector3.Zero, Vector3.One);
        MeshOps.MergeMeshes(far, [Box(new Vector3(1.002f, 0, 0), new Vector3(2, 1, 1))]);
        Assert.Null(MeshOps.WeldOverlapping(far));
    }

    // ---- Mirror / detach / merge meshes ----

    [Fact]
    public void Mirroring_a_box_across_a_pivot_keeps_it_closed_and_facing_out()
    {
        var m = UnitBox();
        MeshOps.Mirror(m, Enumerable.Range(0, 8).ToList(), new Vector3(2, 0, 0), Vector3.UnitX);
        Assert.Equal(3f, m.Bounds().Min.X, 5);
        Assert.Equal(4f, m.Bounds().Max.X, 5);
        Assert.True(IsClosedManifold(m));
        AssertOutward(m, new Vector3(3.5f, 0.5f, 0.5f));
    }

    [Fact]
    public void Copying_faces_makes_a_compact_mesh_with_their_surfaces()
    {
        var m = UnitBox();
        m.Faces[Top].Material = "top";
        m.Faces[Top].UVs = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        m.SoftEdges.Add(new Edge(2, 6));
        m.SoftEdges.Add(new Edge(0, 1));
        var copy = MeshOps.CopyFaces(m, [Top]);
        Assert.Single(copy.Faces);
        Assert.Equal(4, copy.Positions.Count);
        Assert.Equal("top", copy.Faces[0].Material);
        Assert.Equal(4, copy.Faces[0].UVs!.Length);
        Assert.Single(copy.SoftEdges);
        Assert.Equal(6, m.Faces.Count);
        AssertValid(copy);
    }

    [Fact]
    public void Merging_meshes_appends_faces_and_offsets_soft_edges()
    {
        var a = UnitBox();
        var b = Box(new Vector3(2, 0, 0), new Vector3(3, 1, 1), "dev_b");
        b.SoftEdges.Add(new Edge(0, 2));
        MeshOps.MergeMeshes(a, [b]);
        Assert.Equal(12, a.Faces.Count);
        Assert.Equal(16, a.Positions.Count);
        Assert.Contains(new Edge(8, 10), a.SoftEdges);
        Assert.Equal(6, a.Faces.Count(f => f.Material == "dev_b"));
        AssertValid(a);
    }

    // ---- Push/pull and edge extrude ----

    [Fact]
    public void Extruded_sides_flush_with_the_walls_merge_into_them()
    {
        var m = UnitBox();
        var (move, sides) = MeshOps.ExtrudeFaces(m, [Top]);
        foreach (var v in move) m.Positions[v] += Vector3.UnitY;
        var r = MeshOps.MergeCoplanarSides(m, sides, new HashSet<int> { Top });
        Assert.NotNull(r);
        Assert.Equal(6, m.Faces.Count);
        Assert.Equal(8, m.Positions.Count);
        Assert.True(IsClosedManifold(m));
        Assert.Equal(2f, m.Bounds().Max.Y, 5);
    }

    [Fact]
    public void Push_pull_on_a_patch_of_floor_extrudes_it()
    {
        var m = Strip(3);
        var (move, sides) = MeshOps.PushPullFaces(m, [1], Vector3.UnitY);
        Assert.Equal(4, sides.Count);
        Assert.Equal(4, move.Count);
        foreach (var v in move) m.Positions[v] += Vector3.UnitY;
        Assert.True(IsConsistentlyOriented(m));
        Assert.Equal(7, m.Faces.Count);
    }

    [Fact]
    public void Edge_extrude_continues_an_open_surface()
    {
        var m = Strip(1);
        var (edges, sides) = MeshOps.ExtrudeEdges(m, [new Edge(2, 3), new Edge(0, 2)]);
        Assert.Equal(2, sides.Count);
        Assert.Equal(2, edges.Count);
        Assert.Equal(7, m.Positions.Count); // the corner both edges share is duplicated once
        Assert.True(IsConsistentlyOriented(m));

        var box = UnitBox();
        Assert.Empty(MeshOps.ExtrudeEdges(box, [new Edge(0, 2)]).SideFaces); // not an open edge
    }
}
