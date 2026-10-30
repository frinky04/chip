using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.IO;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Core.Tests;

[Collection("Global entity classes")]
public class WorldTests
{
    const string Manifest = """
        {"classes":{"level":{"world":true,"props":[
          {"name":"sky","type":"asset","was":["old_sky"]},
          {"name":"brightness","type":"float"}, {"name":"fog","type":"bool"}]}}}
        """;

    [Fact]
    public void World_round_trip_migration_undo_and_typed_export()
    {
        var catalog = EntityCatalog.Parse(Manifest, null);
        var doc = new Document();
        doc.World["old_sky"] = EntityValues.Of("overcast");
        doc.World["brightness"] = EntityValues.Of("0.7");
        doc.World["fog"] = EntityValues.Of("true");
        doc = DocumentSerializer.Deserialize(DocumentSerializer.Serialize(doc));
        Assert.Equal(1, LevelMigration.Apply(doc, catalog));
        Assert.Equal(0, LevelMigration.Apply(doc, catalog));
        var session = new Session();
        session.Reset(doc, null);
        session.Do("Change sky", () => session.Doc.World["sky"] = EntityValues.Of("night"));
        session.Undo();
        Assert.Equal("overcast", session.Doc.World["sky"].GetString());
        session.Redo();
        Assert.Equal("night", session.Doc.World["sky"].GetString());
        EntityClasses.Use(catalog);
        try
        {
            var json = Glb.Json(GlbExporter.Build(session.Doc, new MaterialLibrary()));
            var world = json["scenes"]![0]!["extras"]!["chip_world"]!;
            Assert.Equal("level", world["chip_class"]!.GetValue<string>());
            Assert.Equal(0.7, world["properties"]!["brightness"]!.GetValue<double>());
            Assert.True(world["properties"]!["fog"]!.GetValue<bool>());
            Assert.Equal("night", world["properties"]!["sky"]!.GetValue<string>());
        }
        finally { EntityClasses.Reset(); }
        Assert.Empty(DocumentSerializer.Deserialize("{\"version\":3,\"meshes\":[]}").World);
    }

    [Theory]
    [InlineData("{\"a\":{\"world\":true},\"b\":{\"world\":true}}", "at most one world")]
    [InlineData("{\"a\":{\"world\":true,\"mesh\":true}}", "cannot be mesh")]
    [InlineData("{\"a\":{\"world\":true,\"inputs\":[{\"name\":\"start\"}]}}", "cannot declare inputs or outputs")]
    public void Manifest_check_rejects_invalid_world_classes(string classes, string message)
    {
        string file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(file, "{\"classes\":" + classes + "}");
            var output = new StringWriter();
            Assert.Equal(2, EntityManifestCheck.Run(file, output));
            Assert.Contains(message, output.ToString());
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Sky_exports_rendered_faces_and_surface_material_without_an_image()
    {
        var doc = new Document();
        doc.Export.BakeCollision = false;
        doc.Add(MeshOps.CreateBox(Vector3.Zero, Vector3.One, "sky", TextureMapping.Default));
        var json = Glb.Json(GlbExporter.Build(doc, new MaterialLibrary()));
        var material = json["materials"]![0]!;
        Assert.Equal("sky", material["name"]!.GetValue<string>());
        Assert.Equal("sky", material["extras"]!["chip_surface"]!.GetValue<string>());
        Assert.Null(json["images"]);
        Assert.Null(material["pbrMetallicRoughness"]!["baseColorTexture"]);
        Assert.Contains(json["nodes"]!.AsArray(), n => n!["mesh"] != null && n["extras"]?["chip_collider"]?.GetValue<string>() == "trimesh");
    }
}
