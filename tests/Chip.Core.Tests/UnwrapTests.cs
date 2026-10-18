using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Texturing;
using static Chip.Core.Tests.TexturingTests;

namespace Chip.Core.Tests;

/// <summary>Unwraps write explicit UVs at 512 px/m and keep strips continuous.</summary>
public class UnwrapTests
{
    static List<int> All(PolyMesh m) => Enumerable.Range(0, m.Faces.Count).ToList();

    [Theory]
    [InlineData(UnwrapMode.Planar)]
    [InlineData(UnwrapMode.Conforming)]
    [InlineData(UnwrapMode.FollowQuads)]
    public void Unwraps_match_the_texel_density_on_every_edge(UnwrapMode mode)
    {
        var m = FloorStrip(2, 0.5f, 3);
        m.Faces[1].Material = "wide"; // 1024 px: half the UV per metre, same texels per metre
        Unwrap.Apply(m, All(m), mode, Size);
        for (int f = 0; f < m.Faces.Count; f++)
        {
            Assert.NotNull(m.Faces[f].UVs);
            for (int i = 0; i < 4; i++)
            {
                var face = m.Faces[f];
                var (w, h) = Size(face.Material);
                var uv = face.UVs!;
                var d = uv[(i + 1) % 4] - uv[i];
                var world = Vector3.Distance(m.Positions[face.Verts[i]], m.Positions[face.Verts[(i + 1) % 4]]);
                float texels = new Vector2(d.X * w, d.Y * h).Length();
                AssertNear(TextureMapping.TexelsPerMeter, texels / world, 0.5f);
            }
        }
    }

    [Fact]
    public void Conforming_preserves_every_face_shape_and_joins_a_box_along_a_spanning_tree()
    {
        var m = Box(Vector3.Zero, new Vector3(3, 2, 1));
        Unwrap.Apply(m, All(m), UnwrapMode.Conforming, Size);
        for (int f = 0; f < 6; f++)
            for (int i = 0; i < 4; i++) AssertNear(1, Density(m, f, i));
        int seamless = 0;
        var edgeFaces = Topology.EdgeFaces(m);
        foreach (var (e, faces) in edgeFaces)
            if (Vector2.Distance(UvAt(m, faces[0], e.A), UvAt(m, faces[1], e.A)) < Eps &&
                Vector2.Distance(UvAt(m, faces[0], e.B), UvAt(m, faces[1], e.B)) < Eps) seamless++;
        Assert.True(seamless >= 5, $"only {seamless} seamless edges");
    }

    [Fact]
    public void Conforming_a_flat_floor_matches_align_to_face()
    {
        var m = FloorStrip(1, 2);
        var projected = UvSpace.Get(new FaceRef(m, 1), Size);
        var align = TextureMapping.AlignToFace(m.FaceNormal(1), m.Faces[1].Map);
        m.Faces[1].Map = align;
        var expected = UvSpace.Get(new FaceRef(m, 1), Size);
        Unwrap.Apply(m, All(m), UnwrapMode.Conforming, Size);
        for (int i = 0; i < 4; i++) AssertNear(expected[i], m.Faces[1].UVs![i]);
        _ = projected;
    }

    [Theory]
    [InlineData(QuadSpacing.Length)]
    [InlineData(QuadSpacing.Even)]
    [InlineData(QuadSpacing.LengthAverage)]
    public void Follow_quads_on_a_curved_strip_is_continuous_across_shared_edges(QuadSpacing spacing)
    {
        var m = Arc(6);
        Unwrap.Apply(m, All(m), UnwrapMode.FollowQuads, Size, spacing, reference: 0);
        for (int f = 0; f + 1 < m.Faces.Count; f++) AssertSeamless(m, f, f + 1);
        // The strip comes out straight: every quad is an axis-aligned rectangle in UV.
        foreach (var face in m.Faces)
        {
            var (min, max) = UvSpace.Bounds(face.UVs!);
            foreach (var uv in face.UVs!)
                Assert.True((MathF.Abs(uv.X - min.X) < Eps || MathF.Abs(uv.X - max.X) < Eps) &&
                            (MathF.Abs(uv.Y - min.Y) < Eps || MathF.Abs(uv.Y - max.Y) < Eps));
        }
    }

    [Fact]
    public void Follow_quads_spacing_sizes_rows_by_length_or_evenly()
    {
        float[] lengths = [1, 2, 0.5f];
        var length = FloorStrip(lengths);
        Unwrap.Apply(length, All(length), UnwrapMode.FollowQuads, Size, QuadSpacing.Length, reference: 0);
        var even = FloorStrip(lengths);
        Unwrap.Apply(even, All(even), UnwrapMode.FollowQuads, Size, QuadSpacing.Even, reference: 0);
        for (int f = 0; f < lengths.Length; f++)
        {
            var (lmin, lmax) = UvSpace.Bounds(length.Faces[f].UVs!);
            var (emin, emax) = UvSpace.Bounds(even.Faces[f].UVs!);
            AssertNear(lengths[f], lmax.X - lmin.X);
            AssertNear(lengths[0], emax.X - emin.X);
            AssertNear(1, lmax.Y - lmin.Y);
        }
    }

    [Fact]
    public void Length_average_keeps_a_grid_seamless_across_rows()
    {
        // Two strips side by side whose segment lengths differ per row: a 2 x 3 grid of quads.
        var m = new PolyMesh();
        float[][] xs = [[0, 1, 3, 4], [0, 1.5f, 2.5f, 4], [0, 1, 3, 4]];
        for (int r = 0; r < 3; r++)
            foreach (var x in xs[r]) m.Positions.Add(new Vector3(x, 0, r));
        for (int r = 0; r < 2; r++)
            for (int c = 0; c < 3; c++)
            {
                int a = r * 4 + c, b = (r + 1) * 4 + c;
                m.Faces.Add(new Face([a, b, b + 1, a + 1], "dev", TextureMapping.Default));
            }
        Unwrap.Apply(m, All(m), UnwrapMode.FollowQuads, Size, QuadSpacing.LengthAverage, reference: 0);
        var edgeFaces = Topology.EdgeFaces(m);
        foreach (var (_, faces) in edgeFaces)
            if (faces.Count == 2) AssertSeamless(m, faces[0], faces[1]);
    }

    [Fact]
    public void Square_makes_every_quad_a_square_of_the_mean_edge()
    {
        var m = FloorStrip(1, 3);
        Unwrap.Apply(m, All(m), UnwrapMode.Square, Size, reference: 0);
        float mean = m.Faces.SelectMany(f => f.Edges()).Average(e => Vector3.Distance(m.Positions[e.A], m.Positions[e.B]));
        foreach (var face in m.Faces)
        {
            var (min, max) = UvSpace.Bounds(face.UVs!);
            AssertNear(mean, max.X - min.X);
            AssertNear(mean, max.Y - min.Y);
        }
        AssertSeamless(m, 0, 1);
    }

    [Fact]
    public void Use_existing_freezes_the_projection()
    {
        var m = Box(Vector3.Zero, new Vector3(2, 1, 1));
        var before = Enumerable.Range(0, 6).Select(f => UvSpace.Get(new FaceRef(m, f), Size)).ToList();
        Unwrap.Apply(m, All(m), UnwrapMode.UseExisting, Size);
        for (int f = 0; f < 6; f++) Assert.Equal(before[f], m.Faces[f].UVs);
    }

    [Fact]
    public void Non_quads_fall_back_to_conforming_in_follow_quads()
    {
        var m = new PolyMesh();
        m.Positions.AddRange([new(0, 0, 0), new(0, 0, 1), new(1, 0, 1), new(1, 0, 0), new(2, 0, 0.5f)]);
        m.Faces.Add(new Face([0, 1, 2, 3], "dev", TextureMapping.Default));
        m.Faces.Add(new Face([3, 2, 4], "dev", TextureMapping.Default));
        Unwrap.Apply(m, All(m), UnwrapMode.FollowQuads, Size);
        Assert.Equal(3, m.Faces[1].UVs!.Length);
        for (int i = 0; i < 3; i++) AssertNear(1, Density(m, 1, i));
    }
}
