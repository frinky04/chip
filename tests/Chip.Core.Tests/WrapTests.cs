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
    public void Wrap_refuses_faces_that_do_not_touch()
    {
        var m = Box(Vector3.Zero, Vector3.One);
        int top = FaceWithNormal(m, Vector3.UnitY), bottom = FaceWithNormal(m, -Vector3.UnitY);
        Assert.False(Wrap.Across(m, top, bottom, Size));
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
