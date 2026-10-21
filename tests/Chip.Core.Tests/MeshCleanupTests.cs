using System.Numerics;
using Chip.Core.Geometry;
using static Chip.Core.Tests.ClipTests;

namespace Chip.Core.Tests;

/// <summary>Mesh check, clean up and dissolve.</summary>
public class MeshCleanupTests
{
    /// <summary>
    /// A floor and the wall along its z = 0 side, after a door was cut and the wall replaced by a plain face: the floor
    /// still has the door's two corners (4 and 5) on that side, the wall doesn't.
    /// </summary>
    static PolyMesh DoorLeftovers()
    {
        var m = new PolyMesh();
        m.Positions.AddRange([new(0, 0, 0), new(0, 0, 4), new(4, 0, 4), new(4, 0, 0), new(2.5f, 0, 0), new(1.5f, 0, 0), new(4, 3, 0), new(0, 3, 0)]);
        m.Faces.Add(new Face([0, 1, 2, 3, 4, 5], "dev_a", default)); // floor, facing up
        m.Faces.Add(new Face([0, 3, 6, 7], "dev_a", default)); // wall, facing into the room
        Assert.True(m.FaceNormal(0).Y > 0.99f && m.FaceNormal(1).Z > 0.99f);
        return m;
    }

    /// <summary>The two faces the old knife made of a stroke that crossed itself (both outlines cross at (5, 4)).</summary>
    static PolyMesh OldKnifeFigureEights()
    {
        var m = new PolyMesh();
        foreach (var (x, z) in new (float, float)[] { (0, 0), (0, 10), (10, 10), (10, 0), (0, 4), (7, 4), (7, 6), (5, 6), (5, 0) })
            m.Positions.Add(new Vector3(x, 0, z));
        m.Faces.Add(new Face([4, 1, 2, 3, 8, 7, 6, 5], "dev_a", default));
        m.Faces.Add(new Face([8, 0, 4, 5, 6, 7], "dev_a", default));
        return m;
    }

    /// <summary>A 2×1 or 2×2 grid of unit quads on the ground.</summary>
    static PolyMesh Grid(int nx, int nz)
    {
        var m = new PolyMesh();
        for (int x = 0; x <= nx; x++)
            for (int z = 0; z <= nz; z++)
                m.Positions.Add(new Vector3(x, 0, z));
        int V(int x, int z) => x * (nz + 1) + z;
        for (int x = 0; x < nx; x++)
            for (int z = 0; z < nz; z++)
                m.Faces.Add(new Face([V(x, z), V(x, z + 1), V(x + 1, z + 1), V(x + 1, z)], "dev_a", default));
        Assert.True(m.FaceNormal(0).Y > 0.99f);
        return m;
    }

    // ---- Check ----

    [Fact]
    public void A_box_is_clean()
    {
        Assert.Empty(MeshOps.Check(Box(Vector3.Zero, Vector3.One)));
        Assert.Empty(MeshOps.Check(Grid(2, 2)));
    }

    [Fact]
    public void Check_finds_the_door_leftovers_as_t_junctions()
    {
        var issues = MeshOps.Check(DoorLeftovers());
        Assert.Equal(2, issues.Count);
        Assert.All(issues, i => Assert.Equal(MeshIssueKind.TJunction, i.Kind));
        Assert.Equal([4, 5], issues.Select(i => i.Vertex).Order());
        Assert.All(issues, i => Assert.Equal(1, i.Face));
    }

    [Fact]
    public void Check_finds_faces_that_cross_or_pinch_themselves()
    {
        var m = OldKnifeFigureEights();
        Assert.Equal(MeshIssueKind.SelfIntersectingFace, MeshOps.FaceIssue(m, 0));
        Assert.Equal(MeshIssueKind.SelfIntersectingFace, MeshOps.FaceIssue(m, 1));

        var pinched = Grid(1, 1);
        pinched.Positions.AddRange([new(1, 0, -1), new(2, 0, -1)]);
        pinched.Faces[0].Verts = [0, 1, 2, 3, 4, 5, 3];
        Assert.Equal(MeshIssueKind.RepeatedVertex, MeshOps.FaceIssue(pinched, 0));

        var flat = Grid(1, 1);
        flat.Positions[1] = new Vector3(0.5f, 0, 0.5f);
        flat.Positions[2] = new Vector3(1, 0, 1);
        flat.Positions[3] = new Vector3(0.25f, 0, 0.25f);
        Assert.Equal(MeshIssueKind.DegenerateFace, MeshOps.FaceIssue(flat, 0));
    }

    [Fact]
    public void Check_finds_non_manifold_and_flipped_edges()
    {
        var m = Grid(2, 1);
        m.Positions.Add(new Vector3(1, 1, 0.5f));
        m.Faces.Add(new Face([2, 3, 6], "dev_a", default)); // a fin standing on the middle edge: three faces share it
        var kinds = MeshOps.Check(m).Select(i => i.Kind).ToList();
        Assert.Contains(MeshIssueKind.NonManifoldEdge, kinds);

        var flipped = Grid(2, 1);
        MeshOps.FlipFaces(flipped, [1]);
        Assert.Contains(MeshOps.Check(flipped), i => i.Kind == MeshIssueKind.FlippedNeighbour && i.Edge == new Edge(2, 3));
    }

    // ---- Clean up ----

    [Fact]
    public void Clean_up_sews_the_door_leftovers_and_dissolves_them()
    {
        var m = DoorLeftovers();
        var r = MeshOps.CleanUp(m);
        Assert.Equal(2, r.TJunctions);
        Assert.Equal(2, r.Dissolved);
        Assert.Empty(MeshOps.Check(m));
        Assert.All(m.Faces, f => Assert.Equal(4, f.Verts.Length));
        Assert.Equal(6, m.Positions.Count);
        Assert.DoesNotContain(new Edge(r.VertexMap[0], r.VertexMap[3]), m.BoundaryEdges()); // floor and wall share it now
        Assert.Equal(-1, r.VertexMap[4]);
    }

    [Fact]
    public void Clean_up_untangles_the_old_knife_cut()
    {
        var m = OldKnifeFigureEights();
        var r = MeshOps.CleanUp(m);
        Assert.Equal(2, r.Untangled);
        // The corner, the square, and the rest (bridged in two, as the knife now cuts it). The second face's lap round
        // the square ran backwards, cancelling one of the first face's two, so the square is filled once.
        Assert.Equal(4, m.Faces.Count);
        KnifeTests.AssertClean(m, 100);
        Assert.Contains(m.Positions, p => Vector3.Distance(p, new Vector3(5, 0, 4)) < 1e-4f);
    }

    [Fact]
    public void Clean_up_welds_and_drops_what_has_no_area()
    {
        var m = Grid(2, 1);
        m.Positions.Add(m.Positions[2] + new Vector3(0.0004f, 0, 0)); // a near-copy of the shared corner
        m.Faces[1].Verts = m.Faces[1].Verts.Select(v => v == 2 ? m.Positions.Count - 1 : v).ToArray();
        m.Faces.Add(new Face([0, 1, 0], "dev_a", default));
        var r = MeshOps.CleanUp(m);
        Assert.Equal(1, r.Welded);
        Assert.Equal(1, r.RemovedFaces);
        Assert.Equal(-1, r.FaceMap[2]);
        Assert.Empty(MeshOps.Check(m));
        Assert.Equal(6, m.Positions.Count);
    }

    [Fact]
    public void Clean_up_leaves_a_tidy_mesh_alone()
    {
        var box = Box(Vector3.Zero, Vector3.One);
        var before = MeshHash.Of(box);
        Assert.False(MeshOps.CleanUp(box).Changed);
        Assert.Equal(before, MeshHash.Of(box));
    }

    // ---- Dissolve ----

    [Fact]
    public void Dissolving_a_stray_vertex_straightens_the_edge_and_keeps_the_face()
    {
        var m = DoorLeftovers();
        var r = MeshOps.DissolveVertices(m, [4, 5]);
        Assert.Equal(2, r.Dissolved);
        Assert.Equal(2, m.Faces.Count);
        Assert.Equal(4, m.Faces[0].Verts.Length);
        Assert.Empty(MeshOps.Check(m));
    }

    [Fact]
    public void Dissolving_the_middle_of_a_flat_grid_merges_it_into_one_quad()
    {
        var m = Grid(2, 2);
        var r = MeshOps.DissolveVertices(m, [4]);
        Assert.Equal(1, r.Dissolved);
        Assert.Single(m.Faces);
        Assert.Equal(4, m.Faces[0].Verts.Length);
        Assert.Equal(4f, Topology.FaceArea(m, 0), 4);
    }

    [Fact]
    public void A_box_corner_is_not_dissolved_round_the_bend()
    {
        var box = Box(Vector3.Zero, Vector3.One);
        var before = MeshHash.Of(box);
        var r = MeshOps.DissolveVertices(box, [0]);
        Assert.Equal(0, r.Dissolved);
        Assert.Equal(1, r.Skipped);
        Assert.Equal(before, MeshHash.Of(box));
    }

    [Fact]
    public void Dissolving_an_edge_merges_its_faces_and_drops_the_leftover_corners()
    {
        var m = Grid(2, 1);
        var r = MeshOps.DissolveEdges(m, [new Edge(2, 3)]);
        Assert.Equal(1, r.Dissolved);
        Assert.Single(m.Faces);
        Assert.Equal(4, m.Faces[0].Verts.Length);
        Assert.Equal(4, m.Positions.Count);
    }

    [Fact]
    public void An_open_edge_has_nothing_to_dissolve_into()
    {
        var m = Grid(2, 1);
        var r = MeshOps.DissolveEdges(m, [new Edge(0, 1)]);
        Assert.Equal(0, r.Dissolved);
        Assert.Equal(1, r.Skipped);
        Assert.Equal(2, m.Faces.Count);
    }
}
