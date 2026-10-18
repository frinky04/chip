using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Geometry.Primitives;
using Chip.Core.Texturing;

namespace Chip.Core.Tests;

public class PrimitiveTests
{
    const string Mat = "dev_measurewall01a";

    public static IEnumerable<object[]> Shapes() => Enum.GetValues<PrimitiveShape>().Select(s => new object[] { s });

    /// <summary>Block-tool gestures: on the ground, drawn backwards, on a wall, and hanging from a ceiling.</summary>
    static readonly PrimitiveFrame[] Frames =
    [
        PrimitiveFrame.FromBlock(new(0, 0, 0), new(4, 0, 2), Vector3.UnitY, 3),
        PrimitiveFrame.FromBlock(new(4, 1, 2), new(-1, 1, -3), Vector3.UnitY, 2),
        PrimitiveFrame.FromBlock(new(2, 0, 0), new(2, 3, 5), Vector3.UnitX, -1.5f),
        PrimitiveFrame.FromBlock(new(0, 4, 0), new(2, 4, 6), -Vector3.UnitY, 2.5f, turn: true),
    ];

    /// <summary>Every edge is used by exactly two faces, in opposite directions (closed, consistently wound).</summary>
    static void AssertClosedAndConsistent(PolyMesh m)
    {
        var directed = new Dictionary<(int, int), int>();
        foreach (var f in m.Faces)
        {
            Assert.True(f.Verts.Length >= 3);
            Assert.Equal(f.Verts.Length, f.Verts.Distinct().Count());
            for (int i = 0; i < f.Verts.Length; i++)
            {
                var key = (f.Verts[i], f.Verts[(i + 1) % f.Verts.Length]);
                directed[key] = directed.GetValueOrDefault(key) + 1;
            }
        }
        foreach (var ((a, b), count) in directed)
        {
            Assert.Equal(1, count);
            Assert.True(directed.ContainsKey((b, a)), $"edge {a}-{b} is open or wound the same way twice");
        }
    }

    static void AssertOutward(PolyMesh m) => Assert.True(Primitive.SignedVolume(m) > 0, "faces wind inward");

    static void AssertFits(PolyMesh m, PrimitiveFrame frame)
    {
        var (min, max) = m.Bounds();
        Assert.True(Vector3.Distance(min, frame.Min) < 1e-4f, $"min {min} != {frame.Min}");
        Assert.True(Vector3.Distance(max, frame.Max) < 1e-4f, $"max {max} != {frame.Max}");
    }

    static PolyMesh Build(PrimitiveShape shape, PrimitiveFrame frame, PrimitiveOptions? o = null) =>
        Primitive.Build(shape, o ?? new PrimitiveOptions(), frame, Mat, TextureMapping.Default);

    [Theory, MemberData(nameof(Shapes))]
    public void Every_shape_is_closed_outward_and_fits_its_box(PrimitiveShape shape)
    {
        foreach (var frame in Frames)
        {
            var m = Build(shape, frame);
            AssertClosedAndConsistent(m);
            AssertOutward(m);
            AssertFits(m, frame);
            Assert.All(m.Faces, f => Assert.Equal(Mat, f.Material));
            Assert.All(m.Faces, f => Assert.Null(f.UVs));
            for (int i = 0; i < m.Faces.Count; i++)
                Assert.Equal(TextureMapping.AlignToGrid(m.FaceNormal(i), TextureMapping.Default).UAxis, m.Faces[i].Map.UAxis);
        }
    }

    [Theory, MemberData(nameof(Shapes))]
    public void Every_shape_survives_extreme_parameters(PrimitiveShape shape)
    {
        var low = new PrimitiveOptions
        {
            Sides = 0, Steps = 0, ArchSegments = 0, ArchDegrees = 1, ArchThickness = 99, SphereRings = 0, SphereSegments = 0,
            TorusSegments = 0, TorusSides = 0, TorusTube = 9, SpiralInner = 0, SpiralDegrees = 0, SpiralSolid = true,
        };
        var high = new PrimitiveOptions
        {
            Sides = 64, Steps = 40, ArchSegments = 32, ArchDegrees = 400, ArchThickness = 0.01f, SphereRings = 24, SphereSegments = 48,
            TorusSegments = 48, TorusSides = 24, TorusTube = 0.05f, SpiralInner = 0.9f, SpiralDegrees = 720, SpiralClockwise = true,
        };
        foreach (var o in new[] { low, high })
        {
            var m = Build(shape, Frames[0], o);
            AssertClosedAndConsistent(m);
            AssertOutward(m);
            AssertFits(m, Frames[0]);
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(24)]
    public void Cylinder_has_the_requested_side_count(int sides)
    {
        var m = Build(PrimitiveShape.Cylinder, Frames[0], new PrimitiveOptions { Sides = sides });
        Assert.Equal(sides + 2, m.Faces.Count);
        Assert.Equal(2, m.Faces.Count(f => f.Verts.Length == sides));
        // Convex: every face points away from the centre.
        var c = (Frames[0].Min + Frames[0].Max) * 0.5f;
        for (int f = 0; f < m.Faces.Count; f++) Assert.True(Vector3.Dot(m.FaceNormal(f), m.FaceCenter(f) - c) > 0);
        // Side seams are smooth, cap rims stay hard.
        Assert.Equal(sides, m.SoftEdges.Count);
    }

    [Fact]
    public void Eight_sided_cylinder_sits_flush_on_the_axes()
    {
        var m = Build(PrimitiveShape.Cylinder, Frames[0], new PrimitiveOptions { Sides = 8 });
        int axisFacing = Enumerable.Range(0, m.Faces.Count).Count(f => MathF.Abs(m.FaceNormal(f).X) > 0.999f || MathF.Abs(m.FaceNormal(f).Z) > 0.999f);
        Assert.Equal(4, axisFacing);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(12)]
    public void Stairs_have_the_requested_step_count(int steps)
    {
        var frame = Frames[0]; // 4 m run along +X, 3 m rise
        var m = Build(PrimitiveShape.Stairs, frame, new PrimitiveOptions { Steps = steps });
        var treads = Enumerable.Range(0, m.Faces.Count).Where(f => m.FaceNormal(f).Y > 0.999f).ToList();
        Assert.Equal(steps, treads.Count);
        var heights = treads.Select(f => m.FaceCenter(f).Y).OrderBy(y => y).ToList();
        for (int i = 0; i < steps; i++) Assert.Equal(3f * (i + 1) / steps, heights[i], 4);
        // Climbs in the drag direction: the lowest tread is at the start of the run.
        var lowest = treads.MinBy(f => m.FaceCenter(f).Y);
        Assert.True(m.FaceCenter(lowest).X < 4f / steps + 1e-4f);
    }

    [Fact]
    public void Steps_follow_the_step_height()
    {
        Assert.Equal(12, Primitive.StepsFor(3, 0.25f));
        Assert.Equal(12, Primitive.StepsFor(-3, 0.25f));
        Assert.Equal(1, Primitive.StepsFor(0.1f, 0.25f));
    }

    [Fact]
    public void Wedge_rises_toward_the_end_of_the_drag_and_turn_swaps_the_run()
    {
        var m = Build(PrimitiveShape.Wedge, Frames[0]);
        Assert.Equal(5, m.Faces.Count);
        int slope = Enumerable.Range(0, m.Faces.Count).Single(f => m.FaceNormal(f) is var n && n.Y > 0.1f && n.Y < 0.99f);
        Assert.True(m.FaceNormal(slope).X < 0); // high end at +X, slope faces back toward the start
        var turned = Build(PrimitiveShape.Wedge, PrimitiveFrame.FromBlock(new(0, 0, 0), new(4, 0, 2), Vector3.UnitY, 3, turn: true));
        int s2 = Enumerable.Range(0, turned.Faces.Count).Single(f => turned.FaceNormal(f) is var n && n.Y > 0.1f && n.Y < 0.99f);
        Assert.True(turned.FaceNormal(s2).Z < 0 && MathF.Abs(turned.FaceNormal(s2).X) < 1e-4f);
    }

    [Fact]
    public void Arch_legs_stand_on_the_floor_with_the_requested_thickness()
    {
        var frame = PrimitiveFrame.FromBlock(new(0, 0, 0), new(4, 0, 1), Vector3.UnitY, 2);
        var m = Build(PrimitiveShape.Arch, frame, new PrimitiveOptions { ArchSegments = 8, ArchDegrees = 180, ArchThickness = 0.5f });
        var feet = Enumerable.Range(0, m.Faces.Count).Where(f => m.FaceNormal(f).Y < -0.999f).ToList();
        Assert.Equal(2, feet.Count);
        foreach (var f in feet)
        {
            Assert.All(m.FacePoints(f), p => Assert.Equal(0f, p.Y, 4));
            var xs = m.FacePoints(f).Select(p => p.X).ToList();
            Assert.Equal(0.5f, xs.Max() - xs.Min(), 3);
        }
        Assert.Equal(2 + 2 * 8 + 2, m.Faces.Count); // front, back, outer and inner segments, two feet
    }

    [Fact]
    public void Sphere_and_torus_have_the_requested_resolution()
    {
        var sphere = Build(PrimitiveShape.Sphere, Frames[0], new PrimitiveOptions { SphereRings = 6, SphereSegments = 10 });
        Assert.Equal(10 * 6, sphere.Faces.Count);
        Assert.Equal(2 + 5 * 10, sphere.Positions.Count);
        var torus = Build(PrimitiveShape.Torus, Frames[0], new PrimitiveOptions { TorusSegments = 12, TorusSides = 6 });
        Assert.Equal(72, torus.Faces.Count);
        Assert.Equal(torus.Edges().Count(), torus.SoftEdges.Count);
    }

    [Fact]
    public void Spiral_stairs_have_one_block_per_step()
    {
        var o = new PrimitiveOptions { Steps = 10, SpiralDegrees = 360 };
        var m = Build(PrimitiveShape.SpiralStairs, Frames[0], o);
        var treads = Enumerable.Range(0, m.Faces.Count).Where(f => m.FaceNormal(f).Y > 0.999f).Select(f => m.FaceCenter(f).Y).Distinct().Count();
        Assert.Equal(10, treads);
        o.SpiralSolid = true;
        var solid = Build(PrimitiveShape.SpiralStairs, Frames[0], o);
        Assert.Equal(10, Enumerable.Range(0, solid.Faces.Count).Count(f => solid.FaceNormal(f).Y < -0.999f && solid.FaceCenter(f).Y < 1e-4f));
    }

    [Fact]
    public void L_shaped_polygon_extrudes_into_a_closed_prism()
    {
        Vector3[] l = [new(0, 0, 0), new(3, 0, 0), new(3, 0, 1), new(1, 0, 1), new(1, 0, 3), new(0, 0, 3)];
        foreach (var loop in new[] { l, l.Reverse().ToArray() })
            foreach (var up in new[] { new Vector3(0, 2, 0), new Vector3(0, -2, 0) })
            {
                var m = Primitive.Prism(loop, up, Mat, TextureMapping.Default)!;
                Assert.Equal(8, m.Faces.Count);
                AssertClosedAndConsistent(m);
                AssertOutward(m);
                Assert.Equal(2 * (9 - 4), Primitive.SignedVolume(m), 3); // L area 5 m², 2 m tall
                // The concave caps triangulate into exactly n - 2 triangles covering the cap area.
                int cap = Enumerable.Range(0, m.Faces.Count).First(f => MathF.Abs(m.FaceNormal(f).Y) > 0.999f);
                var tris = new List<int>();
                Triangulator.Triangulate(m, cap, tris);
                Assert.Equal(3 * 4, tris.Count);
                float area = 0;
                var v = m.Faces[cap].Verts;
                for (int t = 0; t < tris.Count; t += 3)
                    area += Vector3.Cross(m.Positions[v[tris[t + 1]]] - m.Positions[v[tris[t]]], m.Positions[v[tris[t + 2]]] - m.Positions[v[tris[t]]]).Length() * 0.5f;
                Assert.Equal(5f, area, 3);
            }
    }

    [Fact]
    public void Polygon_simplicity_check_rejects_crossings()
    {
        Vector3[] bowtie = [new(0, 0, 0), new(2, 0, 2), new(2, 0, 0), new(0, 0, 2)];
        Vector3[] l = [new(0, 0, 0), new(3, 0, 0), new(3, 0, 1), new(1, 0, 1), new(1, 0, 3), new(0, 0, 3)];
        Vector3[] line = [new(0, 0, 0), new(1, 0, 0), new(2, 0, 0)];
        Assert.False(Primitive.IsSimple(bowtie, Vector3.UnitY));
        Assert.True(Primitive.IsSimple(l, Vector3.UnitY));
        Assert.False(Primitive.IsSimple(line, Vector3.UnitY));
        Assert.Null(Primitive.Prism([new(0, 0, 0), new(1, 0, 0), new(1, 0, 0)], Vector3.UnitY, Mat, TextureMapping.Default));
    }

    [Fact]
    public void Rectangle_is_one_open_quad_facing_the_requested_side()
    {
        var m = Primitive.Rectangle(new(0, 0, 0), new(2, 0, 3), Vector3.UnitY, Mat, TextureMapping.Default)!;
        Assert.Single(m.Faces);
        Assert.Equal(4, m.BoundaryEdges().Count);
        Assert.True(m.FaceNormal(0).Y > 0.999f);
        var down = Primitive.Rectangle(new(0, 0, 0), new(2, 0, 3), -Vector3.UnitY, Mat, TextureMapping.Default)!;
        Assert.True(down.FaceNormal(0).Y < -0.999f);
        var wall = Primitive.Rectangle(new(1, 0, 0), new(1, 2, 2), Vector3.UnitX, Mat, TextureMapping.Default)!;
        Assert.True(wall.FaceNormal(0).X > 0.999f);
        Assert.Null(Primitive.Rectangle(new(0, 0, 0), new(2, 0, 0), Vector3.UnitY, Mat, TextureMapping.Default));
    }

    [Fact]
    public void Frame_follows_the_drag_and_height_sign()
    {
        var f = PrimitiveFrame.FromBlock(new(4, 0, 0), new(0, 0, 1), Vector3.UnitY, -2);
        Assert.Equal(-Vector3.UnitX, f.X);
        Assert.Equal(-Vector3.UnitY, f.Y);
        Assert.Equal(new Vector3(4, 2, 1), f.Size);
        Assert.Equal(new Vector3(0, -2, 0), f.Min);
        Assert.Equal(new Vector3(4, 0, 1), f.Max);
        Assert.Equal(new Vector3(4, 0, 0), f.Origin with { Z = 0 });
    }
}
