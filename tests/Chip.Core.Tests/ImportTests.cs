using System.Numerics;
using System.Text.Json.Nodes;
using Chip.Core.Geometry;
using Chip.Core.Geometry.Primitives;
using Chip.Core.IO;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Core.Tests;

public class ImportTests
{
    static MaterialLibrary Lib()
    {
        var lib = new MaterialLibrary();
        lib.Add(new MaterialInfo("dev_a", null, 512, 512));
        lib.Add(new MaterialInfo("dev_b", null, 256, 128));
        return lib;
    }

    static PolyMesh Box(Vector3 min, Vector3 max, string material = "dev_a") => MeshOps.CreateBox(min, max, material, TextureMapping.Default);

    static bool Closed(PolyMesh m) => m.Faces.SelectMany(f => f.Edges()).GroupBy(e => e).All(g => g.Count() == 2);

    static ImportResult RoundTrip(Document doc, MaterialLibrary lib, float scale = 1) =>
        GltfImporter.Parse(GlbExporter.Build(doc, lib), null, lib, "dev_a", scale);

    /// <summary>Corner UVs keyed by (rounded position, face normal), so the comparison ignores vertex and face order.</summary>
    static Dictionary<(Vector3, Vector3), Vector2> CornerUvs(PolyMesh m, MaterialLibrary lib)
    {
        var map = new Dictionary<(Vector3, Vector3), Vector2>();
        static Vector3 R(Vector3 v) => new(MathF.Round(v.X, 3), MathF.Round(v.Y, 3), MathF.Round(v.Z, 3));
        for (int f = 0; f < m.Faces.Count; f++)
        {
            var face = m.Faces[f];
            var info = lib.Get(face.Material);
            for (int i = 0; i < face.Verts.Length; i++)
            {
                var p = m.Positions[face.Verts[i]];
                map[(R(p), R(m.FaceNormal(f)))] = face.UV(i, p, info.Width, info.Height);
            }
        }
        return map;
    }

    [Fact]
    public void Glb_round_trip_rebuilds_quads_and_keeps_positions_uvs_materials_and_names()
    {
        var lib = Lib();
        var doc = new Document();
        var box = doc.Add(Box(new Vector3(1, 0, -2), new Vector3(3, 1.5f, 0.5f)));
        box.Name = "crate";
        box.Faces[0].Material = "dev_b";
        box.Faces[1].Map = box.Faces[1].Map with { ShiftU = 37, Rotation = 30 };
        box.Faces[2].UVs = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];

        var r = RoundTrip(doc, lib);
        var m = Assert.Single(r.Meshes);
        Assert.Equal("crate", m.Name);
        Assert.Equal(6, m.Faces.Count);
        Assert.All(m.Faces, f => Assert.Equal(4, f.Verts.Length));
        Assert.Equal(8, m.Positions.Count);
        Assert.True(Closed(m));
        Assert.Equal(box.Positions.OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z), m.Positions.OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z));
        Assert.Equal(box.Faces.Select(f => f.Material).Order(), m.Faces.Select(f => f.Material).Order());
        Assert.All(m.Faces, f => Assert.NotNull(f.UVs));
        var expected = CornerUvs(box, lib);
        var actual = CornerUvs(m, lib);
        Assert.Equal(expected.Count, actual.Count);
        foreach (var (key, uv) in expected) Assert.True(Vector2.Distance(uv, actual[key]) < 1e-4f, $"UV at {key}: {uv} vs {actual[key]}");
        // Faces point the same way after the round trip.
        for (int f = 0; f < m.Faces.Count; f++)
            Assert.Contains(box.Faces.Select((_, i) => box.FaceNormal(i)), n => Vector3.Dot(n, m.FaceNormal(f)) > 0.999f);
    }

    [Fact]
    public void Concave_ngon_caps_come_back_as_one_face_each()
    {
        var lib = Lib();
        var doc = new Document();
        Vector3[] l = [new(0, 0, 0), new(3, 0, 0), new(3, 0, 1), new(1, 0, 1), new(1, 0, 3), new(0, 0, 3)];
        var prism = doc.Add(Primitive.Prism(l, new Vector3(0, 2, 0), "dev_a", TextureMapping.Default)!);
        var m = Assert.Single(RoundTrip(doc, lib).Meshes);
        Assert.Equal(prism.Faces.Count, m.Faces.Count);
        Assert.Equal(2, m.Faces.Count(f => f.Verts.Length == 6));
        Assert.True(Closed(m));
    }

    [Fact]
    public void Collision_modes_survive_the_round_trip_via_suffixes()
    {
        var lib = Lib();
        var doc = new Document { ExportCollision = true };
        doc.Add(Box(Vector3.Zero, Vector3.One)).Collision = CollisionMode.Only;
        doc.Add(Box(Vector3.One * 2, Vector3.One * 3)).Collision = CollisionMode.ConvexOnly;
        doc.Add(Box(Vector3.One * 4, Vector3.One * 5));
        var r = RoundTrip(doc, lib);
        Assert.Equal([CollisionMode.Only, CollisionMode.ConvexOnly, CollisionMode.Default], r.Meshes.Select(m => m.Collision));
        Assert.All(r.Meshes, m => Assert.Equal("block", m.Name));
    }

    [Fact]
    public void None_and_Default_stay_distinct_when_the_document_does_not_collide()
    {
        // Neither mode gets a suffix here, so only the chip_collision extra can tell them apart.
        var doc = new Document { ExportCollision = false };
        doc.Add(Box(Vector3.Zero, Vector3.One)).Collision = CollisionMode.None;
        doc.Add(Box(Vector3.One * 2, Vector3.One * 3));
        var r = RoundTrip(doc, Lib());
        Assert.Equal([CollisionMode.None, CollisionMode.Default], r.Meshes.Select(m => m.Collision));
    }

    [Fact]
    public void Clip_and_trigger_faces_rejoin_their_mesh_with_tool_materials()
    {
        var lib = Lib();
        var doc = new Document();
        var box = doc.Add(Box(Vector3.Zero, Vector3.One));
        box.Faces[0].Material = "clip";
        box.Faces[1].Material = "trigger";
        var m = Assert.Single(RoundTrip(doc, lib).Meshes);
        Assert.Equal(6, m.Faces.Count);
        Assert.Equal(1, m.Faces.Count(f => f.Material == "clip"));
        Assert.Equal(1, m.Faces.Count(f => f.Material == "trigger"));
        Assert.True(Closed(m));
    }

    [Fact]
    public void Entities_come_back_with_class_name_transform_and_properties()
    {
        var lib = Lib();
        var doc = new Document { ExportScale = 100 };
        var e = doc.Add(new Entity { ClassName = "light_spot", Name = "lamp", Position = new Vector3(1, 2, 3), Rotation = new Vector3(-30, 45, 10) });
        e.Properties["energy"] = "2.5";
        var r = RoundTrip(doc, lib, scale: 1 / 100f);
        var back = Assert.Single(r.Entities);
        Assert.Equal("light_spot", back.ClassName);
        Assert.Equal("lamp", back.Name);
        Assert.True(Vector3.Distance(e.Position, back.Position) < 1e-4f);
        Assert.True(Vector3.Distance(e.Rotation, back.Rotation) < 1e-2f, $"{back.Rotation}");
        Assert.Equal("2.5", back.Properties["energy"]);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(10, 20, 30)]
    [InlineData(-45, 170, -60)]
    [InlineData(80, -90, 5)]
    public void Entity_euler_inverts_entity_rotation(float pitch, float yaw, float roll)
    {
        var q = GlbExporter.EntityRotation(new Vector3(pitch, yaw, roll));
        var e = GltfImporter.EntityEuler(q);
        var q2 = GlbExporter.EntityRotation(e);
        Assert.True(MathF.Abs(Quaternion.Dot(q, q2)) > 0.99999f, $"{e}");
        Assert.True(Vector3.Distance(new Vector3(pitch, yaw, roll), e) < 0.01f, $"{e}");
    }

    [Fact]
    public void Export_scale_is_undone_by_the_import_scale()
    {
        var lib = Lib();
        var doc = new Document { ExportScale = 100 };
        var box = doc.Add(Box(new Vector3(-1, 0, 0), new Vector3(1, 2, 0.5f)));
        var m = Assert.Single(RoundTrip(doc, lib, 0.01f).Meshes);
        Assert.Equal(box.Bounds().Min, m.Bounds().Min);
        Assert.True(Vector3.Distance(box.Bounds().Max, m.Bounds().Max) < 1e-5f);
    }

    [Fact]
    public void Soft_edges_are_recovered_from_smoothed_normals()
    {
        var lib = Lib();
        var doc = new Document();
        var box = doc.Add(Box(Vector3.Zero, Vector3.One));
        var top = box.Faces.Select((f, i) => i).Single(i => box.FaceNormal(i).Y > 0.9f);
        var soft = box.Faces[top].Edges().First();
        box.SoftEdges.Add(soft);
        var m = Assert.Single(RoundTrip(doc, lib).Meshes);
        var e = Assert.Single(m.SoftEdges);
        Assert.Equal(new[] { box.Positions[soft.A], box.Positions[soft.B] }.OrderBy(p => p.X).ThenBy(p => p.Z),
            new[] { m.Positions[e.A], m.Positions[e.B] }.OrderBy(p => p.X).ThenBy(p => p.Z));

        foreach (var edge in box.Edges()) box.SoftEdges.Add(edge);
        Assert.Equal(12, Assert.Single(RoundTrip(doc, lib).Meshes).SoftEdges.Count);
    }

    [Fact]
    public void Hand_written_gltf_with_strip_node_transform_and_data_uri_imports()
    {
        // A unit quad in XZ as a triangle strip, facing +Y, moved by a node matrix; material name in other casing.
        float[] pos = [0, 0, 0, 0, 0, 1, 1, 0, 0, 1, 0, 1];
        var bytes = new byte[pos.Length * 4];
        Buffer.BlockCopy(pos, 0, bytes, 0, bytes.Length);
        var json = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0" },
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = bytes.Length, ["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(bytes) }),
            ["bufferViews"] = new JsonArray(new JsonObject { ["buffer"] = 0, ["byteLength"] = bytes.Length }),
            ["accessors"] = new JsonArray(new JsonObject { ["bufferView"] = 0, ["componentType"] = 5126, ["count"] = 4, ["type"] = "VEC3" }),
            ["materials"] = new JsonArray(new JsonObject { ["name"] = "DEV_B" }),
            ["meshes"] = new JsonArray(new JsonObject { ["name"] = "floor", ["primitives"] = new JsonArray(new JsonObject
            {
                ["attributes"] = new JsonObject { ["POSITION"] = 0 }, ["mode"] = 5, ["material"] = 0,
            }) }),
            ["nodes"] = new JsonArray(
                new JsonObject { ["children"] = new JsonArray(1), ["translation"] = new JsonArray(5, 0, 0) },
                new JsonObject { ["mesh"] = 0, ["matrix"] = new JsonArray(2, 0, 0, 0, 0, 1, 0, 0, 0, 0, 2, 0, 0, 1, 0, 1) }),
        };
        var r = GltfImporter.Parse(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()), null, Lib(), "dev_a");
        var m = Assert.Single(r.Meshes);
        Assert.Equal("floor", m.Name);
        var face = Assert.Single(m.Faces);
        Assert.Equal(4, face.Verts.Length);
        Assert.Equal("dev_b", face.Material);
        Assert.Null(face.UVs);
        Assert.Equal(new Vector3(5, 1, 0), m.Bounds().Min);
        Assert.Equal(new Vector3(7, 1, 2), m.Bounds().Max);
        Assert.True(m.FaceNormal(0).Y > 0.99f);
    }

    [Theory]
    [InlineData("wall_3-col", "wall_3", CollisionMode.Default)]
    [InlineData("wall_3-colonly", "wall_3", CollisionMode.Only)]
    [InlineData("rock-convcolonly", "rock", CollisionMode.ConvexOnly)]
    [InlineData("plain", "plain", null)]
    public void Godot_suffixes_are_stripped(string name, string baseName, CollisionMode? mode) =>
        Assert.Equal((baseName, mode), GltfImporter.StripCollisionSuffix(name));

    // ---- n-gon reconstruction ----

    static PolyMesh Grid(int n, Func<int, int, Vector2[]?>? uvs = null, Func<int, int, string>? material = null)
    {
        var b = new ImportBuilder();
        for (int x = 0; x < n; x++)
            for (int z = 0; z < n; z++)
            {
                int a = b.AddVertex(new(x, 0, z)), c = b.AddVertex(new(x, 0, z + 1)), d = b.AddVertex(new(x + 1, 0, z + 1)), e = b.AddVertex(new(x + 1, 0, z));
                var uv = uvs?.Invoke(x, z);
                var mat = material?.Invoke(x, z) ?? "dev_a";
                b.AddPolygon([a, c, d], mat, uv == null ? null : [uv[0], uv[1], uv[2]]);
                b.AddPolygon([a, d, e], mat, uv == null ? null : [uv[0], uv[2], uv[3]]);
            }
        return b.Build("grid", Lib());
    }

    static Vector2[] WorldUv(int x, int z) => [new(x, z), new(x, z + 1), new(x + 1, z + 1), new(x + 1, z)];

    [Fact]
    public void A_triangulated_grid_with_continuous_uvs_merges_into_one_quad()
    {
        var m = Grid(3, WorldUv);
        var face = Assert.Single(m.Faces);
        Assert.Equal(4, face.Verts.Length); // border vertices on straight edges are dropped
        Assert.Equal(4, m.Positions.Count);
        Assert.True(m.FaceNormal(0).Y > 0.99f);
        for (int i = 0; i < 4; i++)
        {
            var p = m.Positions[face.Verts[i]];
            Assert.Equal(new Vector2(p.X, p.Z), face.UVs![i]);
        }
    }

    [Fact]
    public void Uv_seams_and_material_changes_stop_the_merge()
    {
        // Each cell has its own 0..1 UVs: neighbouring cells disagree at shared corners, so only each cell's two triangles merge.
        var seams = Grid(2, (_, _) => [new(0, 0), new(0, 1), new(1, 1), new(1, 0)]);
        Assert.Equal(4, seams.Faces.Count);
        Assert.All(seams.Faces, f => Assert.Equal(4, f.Verts.Length));

        var mats = Grid(2, null, (x, _) => x == 0 ? "dev_a" : "dev_b");
        Assert.Equal(2, mats.Faces.Count);
        Assert.Equal(["dev_a", "dev_b"], mats.Faces.Select(f => f.Material).Order());
    }

    [Fact]
    public void A_ring_around_a_hole_keeps_a_seam_instead_of_a_face_with_a_hole()
    {
        var b = new ImportBuilder();
        for (int x = 0; x < 3; x++)
            for (int z = 0; z < 3; z++)
            {
                if (x == 1 && z == 1) continue;
                b.AddPolygon([b.AddVertex(new(x, 0, z)), b.AddVertex(new(x, 0, z + 1)), b.AddVertex(new(x + 1, 0, z + 1)), b.AddVertex(new(x + 1, 0, z))], "dev_a");
            }
        var m = b.Build("ring", Lib());
        Assert.InRange(m.Faces.Count, 2, 8);
        Assert.All(m.Faces, f => Assert.Equal(f.Verts.Length, f.Verts.Distinct().Count())); // simple loops
        // Total area is still the 8 cells.
        float area = 0;
        for (int f = 0; f < m.Faces.Count; f++)
        {
            var v = m.Faces[f].Verts;
            for (int i = 1; i + 1 < v.Length; i++)
                area += Vector3.Cross(m.Positions[v[i]] - m.Positions[v[0]], m.Positions[v[i + 1]] - m.Positions[v[0]]).Y;
        }
        Assert.Equal(8, MathF.Abs(area) / 2, 3);
    }

    [Fact]
    public void Degenerate_triangles_are_dropped()
    {
        var b = new ImportBuilder();
        int a = b.AddVertex(Vector3.Zero), c = b.AddVertex(Vector3.UnitX), d = b.AddVertex(new Vector3(0.000001f, 0, 0)), e = b.AddVertex(Vector3.UnitZ);
        b.AddPolygon([a, c, d], "dev_a"); // d welds onto a
        b.AddPolygon([a, c, b.AddVertex(new Vector3(2, 0, 0))], "dev_a"); // collinear: zero area
        b.AddPolygon([a, e, c], "dev_a");
        Assert.Equal(1, b.PolygonCount);
        Assert.Single(b.Build("t", Lib()).Faces);
    }
}
