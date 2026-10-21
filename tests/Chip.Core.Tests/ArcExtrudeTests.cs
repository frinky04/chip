using System.Numerics;
using Chip.Core.Geometry;
using static Chip.Core.Tests.MeshOpsTests;

namespace Chip.Core.Tests;

public class ArcExtrudeTests
{
    const int Top = 3, PlusX = 1;
    static readonly float Quarter = MathF.PI / 2;

    static void Near(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-4f, $"expected {expected}, got {actual}");

    [Fact]
    public void Bend_starts_along_the_travel_and_curls_toward_the_bend_side()
    {
        var sweep = ArcSweep.Bend(new Vector3(1, 2, 1), Vector3.UnitY, Vector3.UnitX, 2, Quarter, 3);
        Near(new Vector3(3, 2, 1), sweep.AxisPoint);
        var c = new Vector3(1, 2, 1);
        Assert.True(Vector3.Transform(c, sweep.Step(1)).Y > c.Y + 0.5f); // first step rises
        Near(new Vector3(3, 4, 1), Vector3.Transform(c, sweep.Step(3)));  // and ends over the bend side
    }

    [Fact]
    public void Box_top_sweeps_a_closed_quarter_ring_with_the_cap_facing_the_bend()
    {
        var m = Box(Vector3.Zero, new Vector3(2, 2, 2));
        var r = MeshOps.ArcExtrudeFaces(m, [Top], ArcSweep.Bend(new Vector3(1, 2, 1), Vector3.UnitY, Vector3.UnitX, 2, Quarter, 3), smooth: false);

        Assert.Null(r.Collapse);
        Assert.Equal(12, r.SideFaces.Count);
        Assert.Equal(18, m.Faces.Count);
        Assert.True(IsClosedManifold(m));
        Near(Vector3.UnitX, m.FaceNormal(Top));
        Near(new Vector3(3, 4, 1), m.FaceCenter(Top));
        Assert.Empty(m.SoftEdges);
    }

    [Fact]
    public void Smooth_softens_the_rings_between_steps_but_not_the_corners()
    {
        var m = Box(Vector3.Zero, new Vector3(2, 2, 2));
        MeshOps.ArcExtrudeFaces(m, [Top], ArcSweep.Bend(new Vector3(1, 2, 1), Vector3.UnitY, Vector3.UnitX, 2, Quarter, 3), smooth: true);
        Assert.Equal(8, m.SoftEdges.Count); // two inner rings of four edges
    }

    [Fact]
    public void An_axis_on_the_region_edge_folds_that_edge_into_fans()
    {
        var m = Box(Vector3.Zero, new Vector3(2, 2, 2));
        // Radius = half the width: the axis runs along the top's +X edge, which stays put.
        var r = MeshOps.ArcExtrudeFaces(m, [Top], ArcSweep.Bend(new Vector3(1, 2, 1), Vector3.UnitY, Vector3.UnitX, 1, Quarter, 4), smooth: true);

        Assert.NotNull(r.Collapse);
        int top = r.Collapse!.Value.FaceMap[Top];
        Assert.True(top >= 0);
        Assert.True(IsClosedManifold(m));
        Assert.Equal(6 + 4 * 3, m.Faces.Count); // the side on the axis drops; the end sides become triangles
        Near(Vector3.UnitX, m.FaceNormal(top));
        Near(new Vector3(2, 3, 1), m.FaceCenter(top));
        Near(new Vector3(2, 4, 2), m.Bounds().Max);
        Near(m.FaceNormal(PlusX), m.FaceNormal(top)); // flush with the box side it folded onto
    }

    [Fact]
    public void Open_edge_curls_up_into_a_cove()
    {
        var m = new PolyMesh();
        m.Positions.AddRange([new(0, 0, 0), new(0, 0, 1), new(1, 0, 1), new(1, 0, 0)]);
        m.Faces.Add(new Face([0, 1, 2, 3], "dev_a", default));
        // The x = 0 edge leaves along -X; bend up round an axis half a metre above it.
        var sweep = ArcSweep.Bend(new Vector3(0, 0, 0.5f), -Vector3.UnitX, Vector3.UnitY, 0.5f, Quarter, 4);
        var (edges, r) = MeshOps.ArcExtrudeEdges(m, [new Edge(0, 1)], sweep, smooth: true);

        Assert.Equal(4, r.SideFaces.Count);
        Assert.Equal(5, m.Faces.Count);
        Assert.Equal(3, m.SoftEdges.Count);
        var end = Assert.Single(edges);
        Near(new Vector3(-0.5f, 0.5f, 0), m.Positions[end.A]);
        Near(new Vector3(-0.5f, 0.5f, 1), m.Positions[end.B]);
        Assert.True(m.FaceNormal(r.SideFaces[0]).Y > 0.95f);  // the first strip still faces up like the floor
        Assert.True(m.FaceNormal(r.SideFaces[^1]).X > 0.95f); // the last, nearly a wall, faces back into the room
        Assert.True(IsConsistentlyOriented(m));
    }

    [Fact]
    public void Nothing_to_sweep_changes_nothing()
    {
        var m = Box(Vector3.Zero, Vector3.One);
        var r = MeshOps.ArcExtrudeFaces(m, [Top], new ArcSweep(Vector3.Zero, Vector3.Zero, Quarter, 4), smooth: true);
        Assert.Empty(r.SideFaces);
        Assert.Equal(6, m.Faces.Count);
        var (edges, er) = MeshOps.ArcExtrudeEdges(m, [new Edge(0, 1)], ArcSweep.Bend(Vector3.Zero, Vector3.UnitY, Vector3.UnitX, 1, Quarter, 4), smooth: true);
        Assert.Empty(er.SideFaces); // closed edges don't extrude
        Assert.Equal(6, m.Faces.Count);
    }
}
