using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Chip.Core.Geometry;
using Chip.Core.IO;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Core.Tests;

/// <summary>Reads a GLB back for assertions: the JSON chunk and float accessors from the BIN chunk.</summary>
public static class Glb
{
    public static JsonNode Json(byte[] glb) => JsonNode.Parse(Encoding.UTF8.GetString(glb, 20, (int)BitConverter.ToUInt32(glb, 12)))!;

    public static Vector3[] Vec3(byte[] glb, int accessor)
    {
        var json = Json(glb);
        int jsonLen = (int)BitConverter.ToUInt32(glb, 12);
        int binStart = 20 + jsonLen + 8;
        var acc = json["accessors"]![accessor]!;
        var view = json["bufferViews"]![acc["bufferView"]!.GetValue<int>()]!;
        int offset = binStart + view["byteOffset"]!.GetValue<int>();
        return Enumerable.Range(0, acc["count"]!.GetValue<int>())
            .Select(i => new Vector3(BitConverter.ToSingle(glb, offset + i * 12), BitConverter.ToSingle(glb, offset + i * 12 + 4), BitConverter.ToSingle(glb, offset + i * 12 + 8)))
            .ToArray();
    }

    public static List<string> NodeNames(JsonNode json) => json["nodes"]!.AsArray().Select(n => n!["name"]!.GetValue<string>()).ToList();
}

public class ExportTests
{
    static PolyMesh Box(Vector3 min, Vector3 max, string material = "dev_a") => MeshOps.CreateBox(min, max, material, TextureMapping.Default);

    static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chip-export-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static byte[] Png(int w, int h) => MaterialLibrary.EncodePng(w, h, Enumerable.Repeat((byte)200, w * h * 4).ToArray());

    [Fact]
    public void A_material_with_no_picture_warns_once_by_name()
    {
        var dir = TempDir();
        File.WriteAllBytes(Path.Combine(dir, "brick.png"), Png(4, 4));
        var doc = new Document();
        doc.Add(Box(Vector3.Zero, Vector3.One, "brick"));
        doc.Add(Box(Vector3.One, Vector3.One * 2, "lost_paint"));
        doc.Add(Box(Vector3.One * 3, Vector3.One * 4, "lost_paint"));

        var r = GlbExporter.Build(doc, MaterialLibrary.LoadFolder(dir), null);

        Assert.Contains("lost_paint", Assert.Single(r.Warnings));
        var lost = Glb.Json(r.Data)["materials"]!.AsArray().Single(m => m!["name"]!.GetValue<string>() == "lost_paint")!;
        Assert.NotNull(lost["pbrMetallicRoughness"]!["baseColorFactor"]);
    }

    [Fact]
    public void A_material_whose_image_went_missing_warns_with_its_path()
    {
        var doc = new Document();
        doc.Add(Box(Vector3.Zero, Vector3.One, "brick"));
        var lib = new MaterialLibrary();
        lib.Add(new MaterialInfo("brick", Path.Combine(TempDir(), "brick.png"), 4, 4));
        var warning = Assert.Single(GlbExporter.Build(doc, lib, null).Warnings);
        Assert.Contains("brick.png", warning);
    }

    [Fact]
    public void The_sky_has_no_picture_on_purpose_and_stays_quiet()
    {
        var doc = new Document();
        doc.Add(Box(Vector3.Zero, Vector3.One, "sky"));
        Assert.Empty(GlbExporter.Build(doc, new MaterialLibrary(), null).Warnings);
    }

    [Fact]
    public void Entities_export_as_empty_nodes_with_scaled_translation_rotation_and_extras()
    {
        var doc = new Document { ExportScale = 2 };
        doc.Export.BakeCollision = false; // the per-mesh collider path (baking is covered in CollisionBakeExportTests)
        doc.Add(Box(Vector3.Zero, Vector3.One));
        var light = doc.Add(new Entity { ClassName = "light_point", Position = new Vector3(1, 2, 3), Rotation = new Vector3(0, 90, 0) });
        light.Properties["light_color"] = EntityValues.Of("255 128 0"); // text a v2 level could hold: exported typed
        light.Properties["omni_range"] = EntityValues.Of("8");
        light.Properties["note"] = EntityValues.Of("kept"); // not a light_point property: goes out as it is
        var spawn = doc.Add(new Entity { ClassName = "info_player_start", Name = "start" });

        var json = Glb.Json(GlbExporter.Build(doc, new MaterialLibrary()));
        var nodes = json["nodes"]!.AsArray();
        Assert.Equal(3, nodes.Count);
        Assert.Equal(3, json["scenes"]![0]!["nodes"]!.AsArray().Count);
        var n = nodes[1]!;
        Assert.Equal($"light_point_{light.Id}", n["name"]!.GetValue<string>());
        Assert.Null(n["mesh"]);
        Assert.Equal([2f, 4f, 6f], n["translation"]!.AsArray().Select(x => x!.GetValue<float>()));
        var q = n["rotation"]!.AsArray().Select(x => x!.GetValue<float>()).ToArray();
        Assert.Equal(0, q[0], 5); Assert.Equal(MathF.Sqrt(0.5f), q[1], 5); Assert.Equal(0, q[2], 5); Assert.Equal(MathF.Sqrt(0.5f), q[3], 5);
        Assert.Equal("light_point", n["extras"]!["chip_class"]!.GetValue<string>());
        Assert.Equal(light.Id, n["extras"]!["chip_id"]!.GetValue<int>());
        var props = n["extras"]!["properties"]!;
        var color = props["light_color"]!.AsArray().Select(x => x!.GetValue<double>()).ToArray();
        Assert.Equal([1, 128 / 255.0, 0, 1], color, (a, b) => Math.Abs(a - b) < 1e-6);
        Assert.Equal(8, props["omni_range"]!.GetValue<double>());
        Assert.Equal("kept", props["note"]!.GetValue<string>());
        // The class's scene, so the addon can instance it.
        Assert.Equal("res://addons/chip_import/entities/light_point.tscn", n["extras"]!["chip_scene"]!.GetValue<string>());
        Assert.StartsWith("uid://", n["extras"]!["chip_uid"]!.GetValue<string>());

        var s = nodes[2]!;
        Assert.Equal("start", s["name"]!.GetValue<string>());
        Assert.Null(s["translation"]); // origin and identity rotation are left out
        Assert.Null(s["rotation"]);
        Assert.Equal("start", s["extras"]!["chip_name"]!.GetValue<string>());
        Assert.Equal(spawn.Id, s["extras"]!["chip_id"]!.GetValue<int>());
    }

    [Fact]
    public void Yaw_rotates_minus_z_forward_towards_minus_x()
    {
        // Yaw 90 turns the -Z forward to -X (right-handed, Y up), matching Godot's node basis.
        var q = GlbExporter.EntityRotation(new Vector3(0, 90, 0));
        var f = Vector3.Transform(-Vector3.UnitZ, q);
        Assert.Equal(-1, f.X, 5);
        Assert.Equal(0, f.Z, 5);
    }

    [Fact]
    public void An_entity_only_document_writes_no_empty_arrays()
    {
        var doc = new Document();
        doc.Add(new Entity { ClassName = "info_player_start" });
        var glb = GlbExporter.Build(doc, new MaterialLibrary());
        var json = Glb.Json(glb).AsObject();
        Assert.Single(json["nodes"]!.AsArray());
        foreach (var key in new[] { "meshes", "materials", "accessors", "bufferViews", "buffers" }) Assert.False(json.ContainsKey(key), key);
        Assert.Equal((uint)glb.Length, BitConverter.ToUInt32(glb, 8));
    }

    [Theory]
    [InlineData(CollisionMode.Default, true, "-col")]
    [InlineData(CollisionMode.Default, false, "")]
    [InlineData(CollisionMode.None, true, "")]
    [InlineData(CollisionMode.Only, false, "-colonly")]
    [InlineData(CollisionMode.ConvexOnly, true, "-convcolonly")]
    public void Collision_mode_picks_the_godot_suffix(CollisionMode mode, bool documentCollision, string suffix)
    {
        var doc = new Document { ExportCollision = documentCollision };
        doc.Export.BakeCollision = false; // the per-mesh collider path (baking is covered in CollisionBakeExportTests)
        var box = doc.Add(Box(Vector3.Zero, Vector3.One));
        box.Collision = mode;
        var json = Glb.Json(GlbExporter.Build(doc, new MaterialLibrary()));
        Assert.Equal($"block_{box.Id}{suffix}", Glb.NodeNames(json).Single());
        Assert.Equal("block", json["nodes"]![0]!["extras"]!["chip_mesh"]!.GetValue<string>());
        bool collisionOnly = mode is CollisionMode.Only or CollisionMode.ConvexOnly;
        Assert.All(json["meshes"]![0]!["primitives"]!.AsArray(), p => Assert.Equal(collisionOnly, p!["material"] == null));
        Assert.Equal(collisionOnly, json["materials"] == null);
    }

    [Fact]
    public void Clip_nodes_are_tagged_so_imports_can_regroup_them()
    {
        var doc = new Document();
        var box = doc.Add(Box(Vector3.Zero, Vector3.One));
        box.Faces[0].Material = "clip";
        var json = Glb.Json(GlbExporter.Build(doc, new MaterialLibrary()));
        var clip = json["nodes"]![1]!;
        Assert.EndsWith("_clip-colonly", clip["name"]!.GetValue<string>());
        Assert.Equal("clip", clip["extras"]!["chip_tool"]!.GetValue<string>());
        Assert.Equal(box.Id, clip["extras"]!["chip_id"]!.GetValue<int>());
    }

    [Fact]
    public void Smoothing_off_exports_flat_normals_even_across_soft_edges()
    {
        var doc = new Document();
        var box = doc.Add(Box(Vector3.Zero, Vector3.One));
        foreach (var e in box.Edges()) box.SoftEdges.Add(e);

        var smooth = GlbExporter.Build(doc, new MaterialLibrary());
        int normalAcc = Glb.Json(smooth)["meshes"]![0]!["primitives"]![0]!["attributes"]!["NORMAL"]!.GetValue<int>();
        Assert.Contains(Glb.Vec3(smooth, normalAcc), n => MathF.Abs(n.X) > 0.1f && MathF.Abs(n.Y) > 0.1f);

        doc.Export.Smoothing = false;
        var flat = GlbExporter.Build(doc, new MaterialLibrary());
        Assert.All(Glb.Vec3(flat, normalAcc), n => Assert.Equal(1, MathF.Abs(n.X) + MathF.Abs(n.Y) + MathF.Abs(n.Z), 4));
    }

    [Fact]
    public void Hidden_meshes_and_entities_are_skipped_only_when_asked()
    {
        var doc = new Document();
        doc.Export.BakeCollision = false; // the per-mesh collider path (baking is covered in CollisionBakeExportTests)
        doc.Add(Box(Vector3.Zero, Vector3.One));
        doc.Add(Box(Vector3.One, Vector3.One * 2)).Hidden = true;
        doc.Add(new Entity { ClassName = "info_target" }).Hidden = true;
        Assert.Equal(3, Glb.Json(GlbExporter.Build(doc, new MaterialLibrary()))["nodes"]!.AsArray().Count);
        doc.Export.IncludeHidden = false;
        var r = GlbExporter.Build(doc, new MaterialLibrary(), null);
        Assert.Single(Glb.Json(r.Data)["nodes"]!.AsArray());
        Assert.Equal((1, 0), (r.Meshes, r.Entities));
    }

    [Fact]
    public void Remapped_materials_carry_their_engine_path_in_extras()
    {
        var doc = new Document();
        var box = doc.Add(Box(Vector3.Zero, Vector3.One, "brick"));
        box.Faces[0].Material = "concrete";
        doc.Export.MaterialRemap["BRICK"] = "res://materials/brick.tres";
        var json = Glb.Json(GlbExporter.Build(doc, new MaterialLibrary()));
        var materials = json["materials"]!.AsArray();
        var brick = materials.Single(m => m!["name"]!.GetValue<string>() == "brick")!;
        Assert.Equal("res://materials/brick.tres", brick["extras"]!["chip_path"]!.GetValue<string>());
        Assert.Null(materials.Single(m => m!["name"]!.GetValue<string>() == "concrete")!["extras"]);
        var remap = json["nodes"]![0]!["extras"]!["chip_remap"]!.AsObject();
        Assert.Equal("res://materials/brick.tres", Assert.Single(remap).Value!.GetValue<string>());
    }

    [Fact]
    public void External_textures_reference_library_files_and_copy_what_gltf_cannot_point_at()
    {
        var dir = TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "textures"));
        Directory.CreateDirectory(Path.Combine(dir, "out"));
        var png = Path.Combine(dir, "textures", "brick wall.png");
        File.WriteAllBytes(png, Png(4, 4));
        var tga = Path.Combine(dir, "textures", "stone.tga");
        var tgaBytes = new byte[18 + 4 * 4 * 4];
        tgaBytes[2] = 2; tgaBytes[12] = 4; tgaBytes[14] = 4; tgaBytes[16] = 32; tgaBytes[17] = 0x28;
        File.WriteAllBytes(tga, tgaBytes);
        var lib = new MaterialLibrary();
        lib.Add(new MaterialInfo("brick wall", png, 4, 4));
        lib.Add(new MaterialInfo("stone", tga, 4, 4));

        var doc = new Document();
        var box = doc.Add(Box(Vector3.Zero, Vector3.One, "brick wall"));
        box.Faces[0].Material = "stone";
        doc.Export.EmbedTextures = false;
        var glbPath = Path.Combine(dir, "out", "level.glb");
        var result = GlbExporter.Export(doc, lib, glbPath);

        var images = Glb.Json(result.Data)["images"]!.AsArray();
        var uris = images.Select(i => i!["uri"]!.GetValue<string>()).ToList();
        Assert.Contains("../textures/brick%20wall.png", uris);
        Assert.Contains("level_textures/stone.png", uris);
        Assert.All(images, i => Assert.Null(i!["bufferView"]));
        Assert.True(File.Exists(Path.Combine(dir, "out", "level_textures", "stone.png")));

        doc.Export.EmbedTextures = true;
        var embedded = Glb.Json(GlbExporter.Build(doc, lib, glbPath).Data)["images"]!.AsArray();
        Assert.All(embedded, i => { Assert.Null(i!["uri"]); Assert.NotNull(i["bufferView"]); });
    }

    [Fact]
    public void Export_settings_round_trip_through_the_chip_file_and_stay_out_when_default()
    {
        var doc = new Document();
        doc.Add(Box(Vector3.Zero, Vector3.One));
        Assert.DoesNotContain("\"export\"", DocumentSerializer.Serialize(doc));

        doc.Export.Smoothing = false;
        doc.Export.IncludeHidden = false;
        doc.Export.EmbedTextures = false;
        doc.Export.ExportOnSave = true;
        doc.Export.MaterialRemap["brick"] = "res://m/brick.tres";
        doc.Meshes[0].Collision = CollisionMode.ConvexOnly;
        var back = DocumentSerializer.Deserialize(DocumentSerializer.Serialize(doc));
        Assert.False(back.Export.Smoothing);
        Assert.False(back.Export.IncludeHidden);
        Assert.False(back.Export.EmbedTextures);
        Assert.True(back.Export.ExportOnSave);
        Assert.Equal("res://m/brick.tres", back.Export.Remap("BRICK"));
        Assert.Equal(CollisionMode.ConvexOnly, back.Meshes[0].Collision);

        var clone = doc.Clone();
        clone.Export.MaterialRemap.Clear();
        Assert.Single(doc.Export.MaterialRemap); // the undo snapshot doesn't share the table
    }

    [Fact]
    public void A_partial_export_block_keeps_defaults_for_missing_fields()
    {
        var json = DocumentSerializer.Serialize(new Document()).Replace("\"meshes\"", "\"export\": { \"exportOnSave\": true },\n  \"meshes\"");
        var doc = DocumentSerializer.Deserialize(json);
        Assert.True(doc.Export.ExportOnSave);
        Assert.True(doc.Export.Smoothing);
        Assert.True(doc.Export.EmbedTextures);
        Assert.True(doc.Export.IncludeHidden);
    }
}
