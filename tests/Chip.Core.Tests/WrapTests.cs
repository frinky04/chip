using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Texturing;
using static Chip.Core.Tests.TexturingTests;

namespace Chip.Core.Tests;

/// <summary>Wrap continues a face's texture across a shared edge; Flow continues quads along a strip.</summary>
public class WrapTests
{
    /// <summary>The far corners of <paramref name="dst"/> lie on the other side of the shared edge from <paramref name="src"/>'s, in UV.</summary>
    static void AssertUnfoldedAway(PolyMesh m, int src, int dst)
    {
        var e = UvSpace.SharedEdge(m, src, dst)!.Value;
        var a = UvAt(m, src, e.A);
        var b = UvAt(m, src, e.B);
        float Side(Vector2 p) => (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
        var srcFar = m.Faces[src].Verts.Where(v => v != e.A && v != e.B).Select(v => Side(UvAt(m, src, v))).ToList();
        var dstFar = m.Faces[dst].Verts.Where(v => v != e.A && v != e.B).Select(v => Side(UvAt(m, dst, v))).ToList();
        Assert.All(srcFar, s => Assert.All(dstFar, d => Assert.True(s * d < 0, $"src side {s}, dst side {d}")));
    }

    [Fact]
    public void Wrapping_a_projection_continues_it_seamlessly_and_stays_a_projection()
    {
        var m = Box(Vector3.Zero, new Vector3(2, 1, 3));
        int top = FaceWithNormal(m, Vector3.UnitY), side = FaceWithNormal(m, Vector3.UnitX);
        m.Faces[top].Map = TextureMapping.AlignToGrid(Vector3.UnitY, m.Faces[top].Map) with { Rotation = 20, ShiftU = 40 };
        m.Faces[top].Material = "wide";
        Assert.True(Wrap.Across(m, top, side, Size));
        Assert.Null(m.Faces[side].UVs);
        Assert.Equal("wide", m.Faces[side].Material);
        AssertSeamless(m, top, side);
        AssertUnfoldedAway(m, top, side);
    }

    [Fact]
    public void Wrapping_explicit_uvs_extends_them_at_the_same_density()
    {
        var m = Box(Vector3.Zero, new Vector3(2, 1, 3));
        int top = FaceWithNormal(m, Vector3.UnitY), side = FaceWithNormal(m, -Vector3.UnitZ);
        Unwrap.Apply(m, [top], UnwrapMode.Conforming, Size);
        Justify.Rotate([new FaceRef(m, top)], 33, Size);
        Assert.True(Wrap.Across(m, top, side, Size));
        Assert.NotNull(m.Faces[side].UVs);
        AssertSeamless(m, top, side);
        AssertUnfoldedAway(m, top, side);
        for (int i = 0; i < 4; i++) AssertNear(1, Density(m, side, i));
    }

    [Fact]
    public void Across_needs_a_shared_edge_but_Onto_does_not()
    {
        var m = Box(Vector3.Zero, Vector3.One);
        int top = FaceWithNormal(m, Vector3.UnitY), bottom = FaceWithNormal(m, -Vector3.UnitY);
        Assert.False(Wrap.Across(m, top, bottom, Size));
        Assert.True(Wrap.Onto(m, top, m, bottom, Size));
        Assert.False(Wrap.Onto(m, top, m, top, Size));
    }

    [Fact]
    public void The_plane_fold_is_the_shared_edge_fold_for_neighbours()
    {
        var m = Box(Vector3.Zero, new Vector3(2, 1, 3));
        int top = FaceWithNormal(m, Vector3.UnitY), side = FaceWithNormal(m, Vector3.UnitX);
        var e = UvSpace.SharedEdge(m, top, side)!.Value;
        var byEdge = Wrap.Hinge(m, top, side, e.A, e.B);
        var byPlanes = Wrap.PlaneHinge(m, top, m, side);
        foreach (var p in new[] { Vector3.Zero, new Vector3(1, 1, 2), new Vector3(-3, 5, 7) })
            Assert.True(Vector3.Distance(Vector3.Transform(p, byEdge), Vector3.Transform(p, byPlanes)) < 1e-4f);
    }

    [Fact]
    public void Wrapping_onto_a_separate_mesh_continues_as_if_the_planes_met()
    {
        // A's top is the plane y = 1. B's -X wall stands at x = 5 with its bottom edge on that plane, but the two
        // meshes don't touch. Where B meets A's plane, both textures must agree.
        var a = Box(Vector3.Zero, Vector3.One);
        var b = Box(new Vector3(5, 1, 0), new Vector3(6, 2, 1));
        int top = FaceWithNormal(a, Vector3.UnitY), wall = FaceWithNormal(b, -Vector3.UnitX);
        a.Faces[top].Map = TextureMapping.AlignToGrid(Vector3.UnitY, a.Faces[top].Map) with { Rotation = 20, ShiftU = 40 };
        a.Faces[top].Material = "wide";

        Assert.True(Wrap.Onto(a, top, b, wall, Size));
        Assert.Equal("wide", b.Faces[wall].Material);
        Assert.Null(b.Faces[wall].UVs); // a projection stays a projection
        foreach (int v in b.Faces[wall].Verts.Where(v => MathF.Abs(b.Positions[v].Y - 1) < 1e-5f))
        {
            var p = b.Positions[v];
            var fromA = a.Faces[top].Map.Project(p, 1024, 512);
            Assert.True(Vector2.Distance(fromA, UvAt(b, wall, v)) < 1e-3f, $"{fromA} vs {UvAt(b, wall, v)}");
        }
    }

    [Fact]
    public void Wrapping_onto_parallel_faces_keeps_the_density_both_ways()
    {
        var a = Box(Vector3.Zero, Vector3.One);
        var b = Box(new Vector3(3, 0, 0), new Vector3(4, 1, 1));
        int top = FaceWithNormal(a, Vector3.UnitY);
        Unwrap.Apply(a, [top], UnwrapMode.Conforming, Size);
        foreach (var n in new[] { Vector3.UnitY, -Vector3.UnitY })
        {
            int f = FaceWithNormal(b, n);
            Assert.True(Wrap.Onto(a, top, b, f, Size));
            for (int i = 0; i < 4; i++) AssertNear(1, Density(b, f, i));
        }
    }

    [Fact]
    public void Wrap_to_selection_chains_around_a_box()
    {
        var m = Box(Vector3.Zero, new Vector3(2, 1, 3));
        int[] ring = [FaceWithNormal(m, Vector3.UnitX), FaceWithNormal(m, Vector3.UnitZ), FaceWithNormal(m, -Vector3.UnitX), FaceWithNormal(m, -Vector3.UnitZ)];
        var done = Wrap.ToSelection(m, ring[0], ring[1..], Size);
        Assert.Equal(3, done.Count);
        // Each wrapped face is seamless with the face it was wrapped from (both neighbours of the source, then the far side).
        AssertSeamless(m, ring[0], ring[1]);
        AssertSeamless(m, ring[0], ring[3]);
        Assert.True(UvSpace.SharedEdge(m, ring[1], ring[2]) != null);
    }

    [Fact]
    public void Flow_follows_a_curved_strip_from_the_source_quad()
    {
        var m = Arc(5);
        m.Faces[0].Material = "wide";
        var strip = Wrap.Flow(m, 0, 1, Size);
        Assert.Equal([1, 2, 3, 4], strip);
        for (int f = 0; f + 1 < m.Faces.Count; f++) AssertSeamless(m, f, f + 1);
        Assert.All(m.Faces, f => Assert.Equal("wide", f.Material));
        Assert.All(m.Faces, f => Assert.NotNull(f.UVs));
    }

    [Fact]
    public void Flow_stops_at_a_closed_loop()
    {
        var m = Box(Vector3.Zero, Vector3.One);
        int a = FaceWithNormal(m, Vector3.UnitX), b = FaceWithNormal(m, Vector3.UnitZ);
        var strip = Wrap.Flow(m, a, b, Size);
        Assert.Equal(3, strip.Count); // the other three sides of the ring, then back at the source
        Assert.DoesNotContain(a, strip);
    }
}
