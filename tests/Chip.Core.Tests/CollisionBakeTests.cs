using System.Numerics;
using Chip.Core.Diagnostics;
using Chip.Core.Geometry;
using Chip.Core.Geometry.Primitives;
using Chip.Core.Texturing;

namespace Chip.Core.Tests;

public class CollisionBakeTests
{
    const string Mat = "dev_measurewall01a";
    static readonly CollisionBakeOptions Options = new();

    static PolyMesh Box(Vector3 min, Vector3 max) => MeshOps.CreateBox(min, max, Mat, TextureMapping.Default);

    static CollisionSource All(PolyMesh m, bool allowConvex = true, bool forceConvex = false) =>
        new(m, Enumerable.Range(0, m.Faces.Count).ToArray(), allowConvex, forceConvex);

    static PolyMesh LPrism(float height = 2)
    {
        Vector3[] l = [new(0, 0, 0), new(3, 0, 0), new(3, 0, 1), new(1, 0, 1), new(1, 0, 3), new(0, 0, 3)];
        return Primitive.Prism(l, new Vector3(0, height, 0), Mat, TextureMapping.Default)!;
    }

    static PolyMesh Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        var m = new PolyMesh();
        m.Positions.AddRange([a, b, c, d]);
        m.Faces.Add(new Face([0, 1, 2, 3], Mat, TextureMapping.Default));
        return m;
    }

    /// <summary>Every trimesh triangle in world space, chunk by chunk.</summary>
    static List<(Vector3 A, Vector3 B, Vector3 C)> Triangles(CollisionBakeResult r)
    {
        var list = new List<(Vector3, Vector3, Vector3)>();
        foreach (var c in r.Chunks)
            for (int t = 0; t < c.Triangles.Length; t += 3)
                list.Add((c.Positions[c.Triangles[t]], c.Positions[c.Triangles[t + 1]], c.Positions[c.Triangles[t + 2]]));
        return list;
    }

    static Vector3 Normal((Vector3 A, Vector3 B, Vector3 C) t) => Vector3.Normalize(Vector3.Cross(t.B - t.A, t.C - t.A));
    static float Area((Vector3 A, Vector3 B, Vector3 C) t) => Vector3.Cross(t.B - t.A, t.C - t.A).Length() * 0.5f;
    static float TrimeshArea(CollisionBakeResult r) => Triangles(r).Sum(Area);
    static float SourceArea(PolyMesh m) => Enumerable.Range(0, m.Faces.Count).Sum(f => Topology.FaceArea(m, f));
    static float Volume(IEnumerable<(Vector3 A, Vector3 B, Vector3 C)> tris) => tris.Sum(t => Vector3.Dot(t.A, Vector3.Cross(t.B, t.C)) / 6f);

    static IEnumerable<(Vector3, Vector3, Vector3)> PieceTriangles(ConvexPiece p) =>
        Enumerable.Range(0, p.Triangles.Length / 3).Select(t => (p.Points[p.Triangles[3 * t]], p.Points[p.Triangles[3 * t + 1]], p.Points[p.Triangles[3 * t + 2]]));

    /// <summary>Each directed edge appears once and its reverse appears once: a closed, consistently wound surface.</summary>
    static void AssertClosed(int[] tris)
    {
        var directed = new Dictionary<(int, int), int>();
        for (int t = 0; t < tris.Length; t += 3)
            for (int k = 0; k < 3; k++)
            {
                var e = (tris[t + k], tris[t + (k + 1) % 3]);
                directed[e] = directed.GetValueOrDefault(e) + 1;
            }
        foreach (var ((a, b), count) in directed)
        {
            Assert.Equal(1, count);
            Assert.True(directed.ContainsKey((b, a)), $"edge {a}-{b} is open");
        }
    }

    [Fact]
    public void Box_becomes_one_closed_convex_piece()
    {
        var box = Box(new(0, 0, 0), new(2, 1, 3));
        box.Id = 7;
        var r = CollisionBake.Bake([All(box)], Options);

        var piece = Assert.Single(r.Convex);
        Assert.Empty(r.Chunks);
        Assert.Equal(7, piece.SourceMeshId);
        Assert.Equal(8, piece.Points.Length);
        Assert.Equal(36, piece.Triangles.Length);
        AssertClosed(piece.Triangles);
        Assert.Equal(6f, Volume(PieceTriangles(piece)), 4); // positive: wound outward
        Assert.Equal(new CollisionBakeStats(12, 12, 1, 0, 0, 0), r.Stats);
    }

    [Fact]
    public void Convex_primitives_take_the_fast_path()
    {
        var frame = PrimitiveFrame.FromBox(new(0, 0, 0), new(2, 3, 2), Vector3.UnitY, Vector3.UnitX);
        foreach (var shape in new[] { PrimitiveShape.Wedge, PrimitiveShape.Cylinder })
        {
            var m = Primitive.Build(shape, new PrimitiveOptions(), frame, Mat, TextureMapping.Default);
            var r = CollisionBake.Bake([All(m)], Options);
            var piece = Assert.Single(r.Convex);
            AssertClosed(piece.Triangles);
            Assert.Equal(Primitive.SignedVolume(m), Volume(PieceTriangles(piece)), 3);
        }
    }

    [Fact]
    public void L_shaped_prism_goes_to_the_trimesh()
    {
        var r = CollisionBake.Bake([All(LPrism())], Options);
        Assert.Empty(r.Convex);
        Assert.Single(r.Chunks);
        Assert.Equal(20, r.Stats.InputTriangles);
        Assert.Equal(20, r.Stats.OutputTriangles);
        Assert.Equal(10f, Volume(Triangles(r)), 3); // 5 m² L, 2 m tall, wound outward
        Assert.Equal(SourceArea(LPrism()), TrimeshArea(r), 3);
    }

    [Fact]
    public void Allow_convex_off_or_partial_faces_go_to_the_trimesh()
    {
        var box = Box(new(0, 0, 0), new(1, 1, 1));
        Assert.Empty(CollisionBake.Bake([All(box, allowConvex: false)], Options).Convex);
        var partial = CollisionBake.Bake([new CollisionSource(box, [0, 1, 2, 3, 4])], Options);
        Assert.Empty(partial.Convex);
        Assert.Equal(10, partial.Stats.OutputTriangles);
    }

    [Fact]
    public void Floor_tiles_merge_into_one_top_without_internal_edges()
    {
        var tiles = new List<CollisionSource>();
        for (int x = 0; x < 4; x++)
            for (int z = 0; z < 2; z++)
                tiles.Add(All(Box(new(x, 0, z), new(x + 1, 0.25f, z + 1)), allowConvex: false));
        var r = CollisionBake.Bake(tiles, Options);

        Assert.Empty(r.Convex);
        Assert.Equal(8 * 12, r.Stats.InputTriangles);
        Assert.Equal(20, r.Stats.HiddenFacesRemoved); // 10 shared side walls, both faces each
        Assert.True(r.Stats.MergedFaces >= 8 + 8);
        Assert.True(r.Stats.OutputTriangles <= 12, $"{r.Stats.OutputTriangles} triangles");

        var top = Triangles(r).Where(t => Normal(t).Y > 0.999f).ToList();
        Assert.Equal(2, top.Count);
        foreach (var (a, b, c) in top)
            foreach (var p in new[] { a, b, c })
            {
                Assert.Equal(0.25f, p.Y, 5);
                Assert.True(p.X is 0 or 4 && p.Z is 0 or 2, $"top corner {p} is not an outer corner");
            }
        Assert.Equal(8f, top.Sum(Area), 4);
        Assert.Equal(8 * 3f - 20 * 0.25f, TrimeshArea(r), 3);
        Assert.Equal(2f, Volume(Triangles(r)), 3); // still a closed slab
    }

    [Fact]
    public void Open_floor_quads_merge_by_default()
    {
        var tiles = new List<CollisionSource>();
        for (int x = 0; x < 4; x++)
            for (int z = 0; z < 2; z++)
                tiles.Add(All(Primitive.Rectangle(new(x, 0, z), new(x + 1, 0, z + 1), Vector3.UnitY, Mat, TextureMapping.Default)!));
        var r = CollisionBake.Bake(tiles, Options);
        Assert.Empty(r.Convex);
        Assert.Equal(16, r.Stats.InputTriangles);
        Assert.Equal(2, r.Stats.OutputTriangles);
        Assert.Equal(8, r.Stats.MergedFaces);
        Assert.Equal(4, r.Chunks[0].Positions.Length);
        Assert.All(Triangles(r), t => Assert.True(Normal(t).Y > 0.999f));
    }

    [Fact]
    public void Stacked_boxes_lose_their_touching_faces()
    {
        var r = CollisionBake.Bake([All(Box(new(0, 0, 0), new(1, 1, 1)), false), All(Box(new(0, 1, 0), new(1, 2, 1)), false)], Options);
        Assert.Equal(2, r.Stats.HiddenFacesRemoved);
        Assert.DoesNotContain(Triangles(r), t => MathF.Abs(t.A.Y - 1) < 1e-5f && MathF.Abs(t.B.Y - 1) < 1e-5f && MathF.Abs(t.C.Y - 1) < 1e-5f);
        Assert.Equal(10f, TrimeshArea(r), 4);
        Assert.Equal(12, r.Stats.OutputTriangles); // each side merges into one 1x2 quad
        Assert.Equal(2f, Volume(Triangles(r)), 4);

        // With the fast path the boxes stay convex pieces and nothing is removed.
        var convex = CollisionBake.Bake([All(Box(new(0, 0, 0), new(1, 1, 1))), All(Box(new(0, 1, 0), new(1, 2, 1)))], Options);
        Assert.Equal(2, convex.Convex.Count);
        Assert.Equal(0, convex.Stats.HiddenFacesRemoved);
    }

    [Fact]
    public void Wall_on_floor_loses_only_its_bottom()
    {
        var floor = Box(new(0, 0, 0), new(4, 0.2f, 4));
        var wall = Box(new(1, 0.2f, 1), new(3, 2.2f, 1.2f));
        var r = CollisionBake.Bake([All(floor, false), All(wall, false)], Options);

        Assert.Equal(1, r.Stats.HiddenFacesRemoved);
        var tris = Triangles(r);
        Assert.DoesNotContain(tris, t => Normal(t).Y < -0.999f && MathF.Abs(t.A.Y - 0.2f) < 1e-5f);
        Assert.Equal(16f, tris.Where(t => Normal(t).Y > 0.999f && MathF.Abs(t.A.Y - 0.2f) < 1e-5f).Sum(Area), 3);
        Assert.Equal(SourceArea(floor) + SourceArea(wall) - 2 * 0.2f, TrimeshArea(r), 3);
    }

    [Fact]
    public void Force_convex_wraps_a_concave_mesh_in_its_hull()
    {
        var r = CollisionBake.Bake([All(LPrism(), forceConvex: true)], Options);
        var piece = Assert.Single(r.Convex);
        Assert.Empty(r.Chunks);
        Assert.Equal(10, piece.Points.Length); // the reflex corner drops out, top and bottom
        Assert.DoesNotContain(piece.Points, p => p.X == 1 && p.Z == 1);
        AssertClosed(piece.Triangles);
        Assert.Equal(14f, Volume(PieceTriangles(piece)), 3); // (9 - 2) m² pentagon, 2 m tall
    }

    [Fact]
    public void Force_convex_on_a_flat_quad_falls_back_to_the_trimesh()
    {
        var quad = Quad(new(0, 0, 0), new(0, 0, 1), new(1, 0, 1), new(1, 0, 0));
        var r = CollisionBake.Bake([All(quad, forceConvex: true)], Options);
        Assert.Empty(r.Convex);
        Assert.Equal(2, r.Stats.OutputTriangles);
    }

    [Fact]
    public void Chunking_splits_a_long_strip_by_grid_cell()
    {
        var boxes = Enumerable.Range(0, 10).Select(i => All(Box(new(i * 10, 0, 0), new(i * 10 + 1, 1, 1)), false));
        var r = CollisionBake.Bake(boxes, Options with { ChunkSize = 32 });

        Assert.Equal(3, r.Chunks.Count);
        Assert.Equal(3, r.Stats.Chunks);
        Assert.Equal(120, r.Chunks.Sum(c => c.Triangles.Length / 3));
        for (int i = 0; i < r.Chunks.Count; i++)
        {
            var c = r.Chunks[i];
            Assert.Equal(c.Positions.Length, c.Triangles.Distinct().Count()); // compact vertex list
            for (int t = 0; t < c.Triangles.Length; t += 3)
            {
                float cx = (c.Positions[c.Triangles[t]].X + c.Positions[c.Triangles[t + 1]].X + c.Positions[c.Triangles[t + 2]].X) / 3;
                Assert.Equal(i, (int)MathF.Floor(cx / 32));
            }
        }
    }

    [Fact]
    public void Nearby_vertices_weld_across_meshes()
    {
        var left = Quad(new(0, 0, 0), new(0, 0, 1), new(1, 0, 1), new(1, 0, 0));
        var right = Quad(new(1.0005f, 0, 0), new(1.0005f, 0, 1), new(2, 0, 1), new(2, 0, 0));

        var welded = CollisionBake.Bake([All(left), All(right)], Options);
        Assert.Equal(2, welded.Stats.OutputTriangles); // welded, then merged into one quad
        Assert.Equal(4, welded.Chunks.Single().Positions.Length);

        var apart = CollisionBake.Bake([All(left), All(right)], Options with { WeldDistance = 1e-6f });
        Assert.Equal(4, apart.Stats.OutputTriangles);
        Assert.Equal(8, apart.Chunks.Single().Positions.Length);
    }

    [Fact]
    public void Repeat_bakes_are_identical()
    {
        var doc = StressLevel.Generate(80);
        var sources = doc.Meshes.Select((m, i) => All(m, allowConvex: i % 3 != 0, forceConvex: i % 7 == 0)).ToList();
        var a = CollisionBake.Bake(sources, Options with { ChunkSize = 8 });
        var b = CollisionBake.Bake(sources, Options with { ChunkSize = 8 });

        Assert.Equal(a.Stats, b.Stats);
        Assert.True(a.Stats.Chunks > 1 && a.Stats.ConvexPieces > 1);
        Assert.Equal(a.Convex.Count, b.Convex.Count);
        for (int i = 0; i < a.Convex.Count; i++)
        {
            Assert.Equal(a.Convex[i].SourceMeshId, b.Convex[i].SourceMeshId);
            Assert.Equal(a.Convex[i].Points, b.Convex[i].Points);
            Assert.Equal(a.Convex[i].Triangles, b.Convex[i].Triangles);
        }
        Assert.Equal(a.Chunks.Count, b.Chunks.Count);
        for (int i = 0; i < a.Chunks.Count; i++)
        {
            Assert.Equal(a.Chunks[i].Positions, b.Chunks[i].Positions);
            Assert.Equal(a.Chunks[i].Triangles, b.Chunks[i].Triangles);
        }
    }

    [Fact]
    public void Stress_meshes_keep_their_surface_area()
    {
        // The stress meshes stand apart, so the trimesh must keep exactly the area of what it was given.
        var doc = StressLevel.Generate(120);
        var sources = doc.Meshes.Select(m => All(m, allowConvex: false)).ToList();
        var r = CollisionBake.Bake(sources, Options);
        float input = doc.Meshes.Sum(SourceArea);
        Assert.Equal(0, r.Stats.HiddenFacesRemoved);
        Assert.Equal(1f, TrimeshArea(r) / input, 3);
        Assert.True(r.Stats.OutputTriangles <= r.Stats.InputTriangles);
    }

    [Fact]
    public void Empty_and_degenerate_input_bakes_to_nothing()
    {
        var empty = CollisionBake.Bake([], Options);
        Assert.Empty(empty.Convex);
        Assert.Empty(empty.Chunks);
        Assert.Equal(new CollisionBakeStats(0, 0, 0, 0, 0, 0), empty.Stats);

        var box = Box(new(0, 0, 0), new(1, 1, 1));
        var none = CollisionBake.Bake([new CollisionSource(box, []), new CollisionSource(new PolyMesh(), [0, 5]), new CollisionSource(box, [-1, 99])], Options);
        Assert.Equal(new CollisionBakeStats(0, 0, 0, 0, 0, 0), none.Stats);

        var open = MeshOps.CreateBox(new(0, 0, 0), new(1, 1, 1), Mat, TextureMapping.Default);
        open.Faces.RemoveAt(3); // no lid
        var single = Quad(new(5, 0, 0), new(5, 0, 1), new(6, 0, 1), new(6, 0, 0));
        var r = CollisionBake.Bake([All(open), All(single)], Options);
        Assert.Empty(r.Convex);
        Assert.Equal(12, r.Stats.OutputTriangles);
    }
}
