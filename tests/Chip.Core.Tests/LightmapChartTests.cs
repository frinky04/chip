using System.Numerics;
using System.Text.Json.Nodes;
using Chip.Core.Geometry;
using Chip.Core.IO;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Core.Tests;

/// <summary>Lightmap charts: <c>TEXCOORD_1</c> and <c>_LM_CHART</c>, read back from the file as a game would.</summary>
[Collection("Global entity classes")]
public class LightmapChartTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("chip-charts-").FullName;

    public LightmapChartTests() => Project("""{ "lightmapCharts": true }""");

    public void Dispose()
    {
        EntityClasses.Reset();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    void Project(string json)
    {
        File.WriteAllText(Path.Combine(_dir, GameProject.FileName), json);
        EntityClasses.Track(_dir);
    }

    static PolyMesh Box(Vector3 min, Vector3 max, string material = "dev_a") => MeshOps.CreateBox(min, max, material, TextureMapping.Default);

    static Document Level(params PolyMesh[] meshes)
    {
        var doc = new Document();
        doc.Export.BakeCollision = false;
        foreach (var m in meshes) doc.Add(m);
        return doc;
    }

    static byte[] Export(Document doc) => GlbExporter.Build(doc, new MaterialLibrary());

    /// <summary>Two faces sharing the edge from (1,0,0) to (1,1,0); the second leaves the plane at its far side by <paramref name="lift"/>.</summary>
    static PolyMesh TwoQuads(float lift = 0, string left = "dev_a", string right = "dev_a")
    {
        var mesh = new PolyMesh { Name = "wall" };
        mesh.Positions.AddRange([new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0), new(2, 0, lift), new(2, 1, lift)]);
        mesh.Faces.Add(new Face([0, 1, 2, 3], left, TextureMapping.Default));
        mesh.Faces.Add(new Face([1, 4, 5, 2], right, TextureMapping.Default));
        return mesh;
    }

    /// <summary>One written corner of a primitive.</summary>
    readonly record struct Corner(int Primitive, Vector3 Position, Vector2 Uv, int Chart);

    static float[] Floats(byte[] glb, int accessor, int components)
    {
        var json = Glb.Json(glb);
        var acc = json["accessors"]![accessor]!;
        var view = json["bufferViews"]![acc["bufferView"]!.GetValue<int>()]!;
        Assert.Equal(5126, acc["componentType"]!.GetValue<int>());
        int start = 20 + (int)BitConverter.ToUInt32(glb, 12) + 8 + view["byteOffset"]!.GetValue<int>();
        return Enumerable.Range(0, acc["count"]!.GetValue<int>() * components).Select(i => BitConverter.ToSingle(glb, start + i * 4)).ToArray();
    }

    static List<Corner> Corners(byte[] glb, int mesh)
    {
        var result = new List<Corner>();
        var primitives = Glb.Json(glb)["meshes"]![mesh]!["primitives"]!.AsArray();
        for (int p = 0; p < primitives.Count; p++)
        {
            var attrs = primitives[p]!["attributes"]!;
            var pos = Glb.Vec3(glb, attrs["POSITION"]!.GetValue<int>());
            var uv = Floats(glb, attrs["TEXCOORD_1"]!.GetValue<int>(), 2);
            var id = Floats(glb, attrs["_LM_CHART"]!.GetValue<int>(), 1);
            Assert.Equal(pos.Length * 2, uv.Length);
            Assert.Equal(pos.Length, id.Length);
            for (int i = 0; i < pos.Length; i++) result.Add(new Corner(p, pos[i], new Vector2(uv[i * 2], uv[i * 2 + 1]), (int)id[i]));
        }
        return result;
    }

    static List<(Corner A, Corner B, Corner C)> Triangles(byte[] glb, int mesh)
    {
        var result = new List<(Corner, Corner, Corner)>();
        var json = Glb.Json(glb);
        var corners = Corners(glb, mesh);
        var primitives = json["meshes"]![mesh]!["primitives"]!.AsArray();
        for (int p = 0; p < primitives.Count; p++)
        {
            var own = corners.Where(c => c.Primitive == p).ToList();
            var acc = json["accessors"]![primitives[p]!["indices"]!.GetValue<int>()]!;
            var view = json["bufferViews"]![acc["bufferView"]!.GetValue<int>()]!;
            int start = 20 + (int)BitConverter.ToUInt32(glb, 12) + 8 + view["byteOffset"]!.GetValue<int>();
            for (int i = 0; i + 2 < acc["count"]!.GetValue<int>(); i += 3)
                result.Add((own[(int)BitConverter.ToUInt32(glb, start + i * 4)], own[(int)BitConverter.ToUInt32(glb, start + (i + 1) * 4)], own[(int)BitConverter.ToUInt32(glb, start + (i + 2) * 4)]));
        }
        return result;
    }

    static float Extent(IEnumerable<Corner> chart, Func<Vector2, float> axis) => chart.Max(c => axis(c.Uv)) - chart.Min(c => axis(c.Uv));

    static (float Small, float Large) Size(IEnumerable<Corner> chart)
    {
        float u = Extent(chart, c => c.X), v = Extent(chart, c => c.Y);
        return (Math.Min(u, v), Math.Max(u, v));
    }

    static List<(float Small, float Large)> Sizes(List<Corner> corners) =>
        corners.GroupBy(c => c.Chart).OrderBy(g => g.Key).Select(g => Size(g)).ToList();

    [Fact]
    public void Off_by_default_and_when_switched_off()
    {
        foreach (var project in new[] { "{}", """{ "lightmapCharts": false }""" })
        {
            Project(project);
            var doc = Level(Box(Vector3.Zero, Vector3.One));
            foreach (var mesh in Glb.Json(Export(doc))["meshes"]!.AsArray())
                foreach (var p in mesh!["primitives"]!.AsArray())
                {
                    Assert.Null(p!["attributes"]!["TEXCOORD_1"]);
                    Assert.Null(p["attributes"]!["_LM_CHART"]);
                }
        }
    }

    [Fact]
    public void A_box_has_six_charts_at_the_origin_with_uv_area_equal_to_world_area()
    {
        var glb = Export(Level(Box(new Vector3(3, 1, -2), new Vector3(5, 4, 3))));
        var corners = Corners(glb, 0);
        Assert.Equal([0, 1, 2, 3, 4, 5], corners.Select(c => c.Chart).Distinct().Order());
        foreach (var chart in corners.GroupBy(c => c.Chart))
        {
            Assert.Equal(0f, chart.Min(c => c.Uv.X)); // exactly
            Assert.Equal(0f, chart.Min(c => c.Uv.Y));
        }
        var triangles = Triangles(glb, 0);
        Assert.Equal(12, triangles.Count);
        foreach (var (a, b, c) in triangles)
        {
            float world = Vector3.Cross(b.Position - a.Position, c.Position - a.Position).Length() / 2;
            float uv = ((b.Uv.X - a.Uv.X) * (c.Uv.Y - a.Uv.Y) - (b.Uv.Y - a.Uv.Y) * (c.Uv.X - a.Uv.X)) / 2;
            Assert.Equal(world, uv, 4); // the sign says it isn't mirrored: v = normal × u
        }
    }

    [Fact]
    public void Coplanar_faces_in_different_materials_share_a_chart_across_primitives()
    {
        var glb = Export(Level(TwoQuads(left: "dev_a", right: "dev_b")));
        Assert.Equal(2, Glb.Json(glb)["meshes"]![0]!["primitives"]!.AsArray().Count);
        var corners = Corners(glb, 0);
        Assert.All(corners, c => Assert.Equal(0, c.Chart));
        Assert.Equal((1f, 2f), Size(corners));
    }

    [Theory]
    [InlineData(0.0005f, 1)] // within 1 mm, in document units
    [InlineData(0.003f, 2)]
    public void Faces_join_only_within_a_millimetre_of_each_others_plane(float lift, int charts)
    {
        var glb = Export(Level(TwoQuads(lift)));
        Assert.Equal(charts, Corners(glb, 0).Select(c => c.Chart).Distinct().Count());
    }

    [Fact]
    public void The_coplanar_test_ignores_the_export_scale()
    {
        var doc = Level(TwoQuads(0.0005f));
        doc.ExportScale = 100;
        Assert.Single(Corners(Export(doc), 0).Select(c => c.Chart).Distinct());
    }

    [Fact]
    public void A_soft_edge_does_not_join_faces_that_are_not_coplanar()
    {
        var mesh = TwoQuads(lift: 0.5f);
        mesh.SoftEdges.Add(new Edge(1, 2));
        Assert.Equal(2, Corners(Export(Level(mesh)), 0).Select(c => c.Chart).Distinct().Count());
    }

    [Fact]
    public void A_gentle_curve_does_not_chain_into_one_flat_chart()
    {
        // A ring of narrow strips: every neighbouring pair passes the coplanar test, yet the ring is no plane.
        const int sides = 600;
        const float radius = 3;
        var mesh = new PolyMesh { Name = "ring" };
        for (int i = 0; i < sides; i++)
        {
            float a = MathF.Tau * i / sides;
            mesh.Positions.Add(new Vector3(MathF.Cos(a) * radius, 0, MathF.Sin(a) * radius));
            mesh.Positions.Add(new Vector3(MathF.Cos(a) * radius, 1, MathF.Sin(a) * radius));
        }
        for (int i = 0; i < sides; i++)
        {
            int j = (i + 1) % sides;
            mesh.Faces.Add(new Face([i * 2, j * 2, j * 2 + 1, i * 2 + 1], "dev_a", TextureMapping.Default));
        }
        var corners = Corners(Export(Level(mesh)), 0);
        var charts = corners.GroupBy(c => c.Chart).ToList();
        Assert.True(charts.Count > 1);
        // Chained into one chart it would flatten to the ring's 6 m diameter; flat pieces stay a small part of it.
        Assert.All(charts, chart => Assert.True(Extent(chart, c => c.X) < 1.5f && Extent(chart, c => c.Y) <= 1.5f, $"chart {chart.Key}: {Size(chart)}"));
    }

    [Fact]
    public void A_box_rotated_45_degrees_packs_as_tightly_as_a_straight_one()
    {
        var box = Box(Vector3.Zero, new Vector3(4, 3, 1));
        var turn = Matrix4x4.CreateRotationY(MathF.PI / 4);
        for (int i = 0; i < box.Positions.Count; i++) box.Positions[i] = Vector3.Transform(box.Positions[i], turn);
        var sizes = Sizes(Corners(Export(Level(box)), 0)).Order().ToList();
        var want = new (float, float)[] { (1, 3), (1, 3), (1, 4), (1, 4), (3, 4), (3, 4) };
        Assert.Equal(6, sizes.Count);
        for (int i = 0; i < 6; i++)
        {
            Assert.Equal(want[i].Item1, sizes[i].Small, 3);
            Assert.Equal(want[i].Item2, sizes[i].Large, 3);
        }
    }

    [Fact]
    public void The_first_edge_in_face_order_wins_a_tie()
    {
        // A 10 × 5 rectangle, slanted on the floor: all four edges fit it equally, so the first (length 10) becomes u.
        var along = new Vector3(-6, 0, 8);
        var mesh = new PolyMesh { Name = "tile" };
        mesh.Positions.AddRange([new(0, 0, 0), along, along + new Vector3(4, 0, 3), new(4, 0, 3)]);
        mesh.Faces.Add(new Face([0, 1, 2, 3], "dev_a", TextureMapping.Default));
        var corners = Corners(Export(Level(mesh)), 0);
        Assert.Equal(10, Extent(corners, c => c.X), 3);
        Assert.Equal(5, Extent(corners, c => c.Y), 3);

        // Started from the short edge, the same rectangle lies the other way round.
        mesh.Faces[0] = new Face([1, 2, 3, 0], "dev_a", TextureMapping.Default);
        corners = Corners(Export(Level(mesh)), 0);
        Assert.Equal(5, Extent(corners, c => c.X), 3);
        Assert.Equal(10, Extent(corners, c => c.Y), 3);
    }

    [Fact]
    public void Linked_copies_share_a_glTF_mesh_and_so_their_chart_ids()
    {
        var doc = new Document();
        var body = doc.Add(Box(new Vector3(-1, 0, -0.5f), new Vector3(1, 1, 0.5f)));
        var session = new Session();
        session.Reset(doc, null);
        session.Do("Make", [body.Id], () =>
        {
            var a = InstanceOps.Make(session.Doc, [body.Id], "crate")!;
            InstanceOps.Place(session.Doc, session.Doc.FindDefinition(a.Instance)!, new ObjectOrigin(new Vector3(10, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2)));
        });
        var glb = Export(session.Doc);
        var nodes = Glb.Json(glb)["nodes"]!.AsArray().Where(n => n!["extras"]?["chip_part"] != null).ToList();
        Assert.Equal(2, nodes.Count);
        Assert.Single(nodes.Select(n => n!["mesh"]!.GetValue<int>()).Distinct());
        Assert.Equal(6, Corners(glb, nodes[0]!["mesh"]!.GetValue<int>()).Select(c => c.Chart).Distinct().Count());
    }

    [Fact]
    public void A_mesh_with_its_own_origin_and_a_mesh_entity_lay_out_at_their_real_size()
    {
        var doc = Level(Box(new Vector3(5, 0, 5), new Vector3(7, 3, 6)));
        doc.Meshes[0].Origin = new ObjectOrigin(new Vector3(6, 0, 5), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f));
        var door = doc.Add(Box(new Vector3(20, 0, 0), new Vector3(22, 3, 1)));
        var entity = MeshEntityOps.Tie(doc, [door.Id], "door")!;
        entity.Rotation = new Vector3(0, 33, 0);

        var glb = Export(doc);
        foreach (int mesh in new[] { 0, 1 })
            Assert.Equal([(1f, 2f), (1f, 2f), (1f, 3f), (1f, 3f), (2f, 3f), (2f, 3f)], Sizes(Corners(glb, mesh)).Select(s => (MathF.Round(s.Small, 3), MathF.Round(s.Large, 3))).Order());
    }

    [Fact]
    public void Export_scale_scales_the_charts()
    {
        var plain = Sizes(Corners(Export(Level(Box(Vector3.Zero, new Vector3(4, 3, 1)))), 0));
        var doc = Level(Box(Vector3.Zero, new Vector3(4, 3, 1)));
        doc.ExportScale = 100;
        var scaled = Sizes(Corners(Export(doc), 0));
        Assert.Equal(plain.Count, scaled.Count);
        for (int i = 0; i < plain.Count; i++)
        {
            Assert.Equal(plain[i].Small * 100, scaled[i].Small, 2);
            Assert.Equal(plain[i].Large * 100, scaled[i].Large, 2);
        }
    }

    [Fact]
    public void Tessellation_keeps_every_cell_of_a_wall_in_one_chart()
    {
        var doc = Level(Box(new Vector3(0.1f, 0, 0), new Vector3(8.3f, 3.7f, 0.25f)));
        doc.Export.Tessellate = true;
        doc.Export.TessellateSpacing = 0.5f;
        var glb = Export(doc);
        Assert.True(Triangles(glb, 0).Count > 12 * 5);
        var corners = Corners(glb, 0);
        Assert.Equal(6, corners.Select(c => c.Chart).Distinct().Count());
        var sizes = Sizes(corners).Order().ToList();
        Assert.Equal(8.2f, sizes[^1].Large, 3);
        Assert.Equal(3.7f, sizes[^1].Small, 3);
    }

    [Fact]
    public void Tessellation_keeps_a_narrow_chamfer_far_from_the_origin_in_one_chart()
    {
        // Float rounding bends the normals of cells this narrow this far out, though the source face is one flat strip.
        var start = new Vector3(1000, 500, 1000);
        var run = Vector3.Normalize(new Vector3(1, 0, 1)) * 8;
        var rise = Vector3.Normalize(new Vector3(1, 1, -1)) * 0.004f;
        var mesh = new PolyMesh { Name = "chamfer" };
        mesh.Positions.AddRange([start, start + run, start + run + rise, start + rise]);
        mesh.Faces.Add(new Face([0, 1, 2, 3], "dev_a", TextureMapping.Default));
        var doc = Level(mesh);
        doc.Export.Tessellate = true;
        doc.Export.TessellateSpacing = 0.25f;
        var glb = Export(doc);
        Assert.True(Triangles(glb, 0).Count > 8);
        Assert.Single(Corners(glb, 0).Select(c => c.Chart).Distinct());
    }

    [Fact]
    public void Coplanar_faces_stay_one_chart_through_tessellation()
    {
        var doc = Level(TwoQuads(left: "dev_a", right: "dev_b"));
        doc.Export.Tessellate = true;
        doc.Export.TessellateSpacing = 0.25f;
        Assert.Single(Corners(Export(doc), 0).Select(c => c.Chart).Distinct());
    }

    [Fact]
    public void Exporting_twice_gives_identical_bytes()
    {
        Document Make(bool tessellate)
        {
            var doc = Level(TwoQuads(left: "dev_a", right: "dev_b"), Box(Vector3.One, Vector3.One * 3));
            doc.Export.Tessellate = tessellate;
            return doc;
        }
        foreach (bool tessellate in new[] { false, true })
            Assert.Equal(Export(Make(tessellate)), Export(Make(tessellate)));
    }

    [Fact]
    public void Only_primitives_with_a_material_carry_charts_and_every_drawn_kind_does()
    {
        var clip = Box(new Vector3(0, 5, 0), new Vector3(1, 6, 1), "clip");
        var collider = Box(new Vector3(0, 8, 0), new Vector3(1, 9, 1));
        collider.Collision = CollisionMode.Only;
        var sky = Box(new Vector3(5, 0, 0), new Vector3(6, 1, 1), "sky");
        var doc = Level(clip, collider, sky);
        var glb = Export(doc);
        var json = Glb.Json(glb);
        foreach (var mesh in json["meshes"]!.AsArray())
            foreach (var p in mesh!["primitives"]!.AsArray())
            {
                bool drawn = p!["material"] != null;
                Assert.Equal(drawn, p["attributes"]!["TEXCOORD_1"] != null);
                Assert.Equal(drawn, p["attributes"]!["_LM_CHART"] != null);
            }
        Assert.Contains(json["meshes"]!.AsArray(), m => m!["primitives"]![0]!["attributes"]!["_LM_CHART"] != null);
        Assert.Contains(json["materials"]!.AsArray(), m => m!["name"]!.GetValue<string>() == "sky");
    }

    [Fact]
    public void Each_glTF_mesh_counts_its_charts_from_zero()
    {
        var wall = TwoQuads(left: "dev_a", right: "video");
        Project("""{ "lightmapCharts": true, "surfaces": { "video": { "collide": "none" } } }""");
        var glb = Export(Level(wall));
        Assert.Equal(2, Glb.Json(glb)["meshes"]!.AsArray().Count); // a surface drawing without colliding is its own glTF mesh
        Assert.All(Corners(glb, 0), c => Assert.Equal(0, c.Chart));
        Assert.All(Corners(glb, 1), c => Assert.Equal(0, c.Chart));
    }

    [Fact]
    public void The_check_reports_the_setting_and_a_project_without_it_loads_off()
    {
        Assert.True(GameProject.Parse("""{ "lightmapCharts": true }""", Path.Combine(_dir, GameProject.FileName)).LightmapCharts);
        Assert.False(GameProject.Parse("{}", Path.Combine(_dir, GameProject.FileName)).LightmapCharts);
        Assert.False(GameProject.Godot(_dir).LightmapCharts);
        Assert.Throws<InvalidDataException>(() => GameProject.Parse("""{ "lightmapCharts": 1 }""", Path.Combine(_dir, GameProject.FileName)));
        var unknown = Assert.Throws<InvalidDataException>(() => GameProject.Parse("""{ "lightmapChart": true }""", Path.Combine(_dir, GameProject.FileName)));
        Assert.Contains("lightmapCharts", unknown.Message);

        File.WriteAllText(Path.Combine(_dir, "e.json"), """{ "classes": { "spawn": {} } }""");
        Project("""{ "lightmapCharts": true, "entities": "e.json" }""");
        var text = new StringWriter();
        EntityManifestCheck.Run(_dir, text);
        Assert.Contains("lightmap charts on", text.ToString());
        var json = new StringWriter();
        EntityManifestCheck.Run(_dir, json, json: true);
        Assert.True(JsonNode.Parse(json.ToString())!["lightmapCharts"]!.GetValue<bool>());
    }
}
