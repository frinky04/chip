using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Chip.Core.Geometry;
using Chip.Core.IO;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Core.Tests;

public class MeshTagExportTests
{
    static PolyMesh Box() => MeshOps.CreateBox(Vector3.Zero, Vector3.One, "dev_a", TextureMapping.Default);

    [Theory]
    [InlineData(CollisionMode.Default, false, false)]
    [InlineData(CollisionMode.Default, true, false)]
    [InlineData(CollisionMode.Default, false, true)]
    [InlineData(CollisionMode.Default, true, true)]
    [InlineData(CollisionMode.Only, false, false)]
    [InlineData(CollisionMode.ConvexOnly, false, false)]
    [InlineData(CollisionMode.None, false, false)]
    public void Mesh_tags_export_on_source_nodes_and_round_trip(CollisionMode collision, bool bake, bool tessellate)
    {
        var doc = new Document();
        doc.Export.BakeCollision = bake;
        doc.Export.Tessellate = tessellate;
        doc.Export.TessellateSpacing = 0.5f;
        var box = doc.Add(Box());
        box.Collision = collision;
        box.Tags = TagSet.Of("interactable", "destructible", "interactable");
        box.Faces[0].Tags = TagSet.Of("face-only");
        box.Faces[0].Material = "clip";
        box.Faces[1].Material = "trigger";
        var lib = new MaterialLibrary();
        var glb = GlbExporter.Build(doc, lib);
        var nodes = Glb.Json(glb)["nodes"]!.AsArray();
        Assert.Contains(nodes, n => n!["extras"]?["chip_mesh"] != null);
        Assert.Contains(nodes, n => n!["extras"]?["chip_volume"]?.GetValue<bool>() == true);
        foreach (var node in nodes)
        {
            var extras = node!["extras"]!;
            if (extras["chip_baked"]?.GetValue<bool>() == true) Assert.Null(extras["chip_tags"]);
            else
            {
                Assert.Equal(box.Id, extras["chip_id"]!.GetValue<int>());
                Assert.Equal(new[] { "destructible", "interactable" }, extras["chip_tags"]!.AsArray().Select(t => t!.GetValue<string>()));
            }
        }
        var imported = Assert.Single(GltfImporter.Parse(glb, null, lib, "dev_a").Meshes);
        Assert.Equal(box.Tags, imported.Tags);
        Assert.All(imported.Faces, f => Assert.True(f.Tags.IsEmpty));
    }

    [Theory]
    [InlineData("clip")]
    [InlineData("trigger")]
    public void Tags_survive_when_the_mesh_only_exports_surface_nodes(string surface)
    {
        var doc = new Document();
        var box = doc.Add(Box());
        box.Tags = TagSet.Of("special");
        foreach (var face in box.Faces) face.Material = surface;
        var lib = new MaterialLibrary();
        var glb = GlbExporter.Build(doc, lib);
        Assert.All(Glb.Json(glb)["nodes"]!.AsArray(), n =>
            Assert.Equal("special", Assert.Single(n!["extras"]!["chip_tags"]!.AsArray())!.GetValue<string>()));
        Assert.Equal(box.Tags, Assert.Single(GltfImporter.Parse(glb, null, lib, "dev_a").Meshes).Tags);
    }

    [Fact]
    public void Mesh_entity_children_keep_tags_and_untagged_meshes_omit_the_key()
    {
        var doc = new Document();
        var box = doc.Add(Box());
        box.Tags = TagSet.Of("destructible");
        var entity = doc.Add(new Entity { ClassName = "door", Meshes = [box.Id] });
        var untagged = doc.Add(Box());
        untagged.Faces[0].Material = "clip";
        var lib = new MaterialLibrary();
        var glb = GlbExporter.Build(doc, lib);
        var nodes = Glb.Json(glb)["nodes"]!.AsArray();
        var parent = Assert.Single(nodes, n => n!["extras"]?["chip_class"] != null)!;
        var child = nodes[Assert.Single(parent["children"]!.AsArray())!.GetValue<int>()]!;
        Assert.Equal("destructible", child["extras"]!["chip_tags"]![0]!.GetValue<string>());
        Assert.All(nodes.Where(n => n!["extras"]?["chip_id"]?.GetValue<int>() == untagged.Id && n["mesh"] != null),
            n => Assert.Null(n!["extras"]!["chip_tags"]));
        var result = GltfImporter.Parse(glb, null, lib, "dev_a");
        Assert.Equal(box.Tags, Assert.Single(result.EntityMeshes[Assert.Single(result.Entities)]).Tags);
        Assert.Equal(entity.ClassName, result.Entities[0].ClassName);
    }

    [Theory]
    [InlineData("[\"valid\", 42, null, \"Bad Tag\", \"valid\"]", 3)]
    [InlineData("\"invalid\"", 1)]
    public void Import_warns_about_invalid_tags_without_losing_geometry(string tags, int warningCount)
    {
        var doc = new Document { ExportCollision = false };
        doc.Add(Box());
        var lib = new MaterialLibrary();
        var glb = GlbExporter.Build(doc, lib);
        var json = Glb.Json(glb);
        json["nodes"]![0]!["extras"]!["chip_tags"] = JsonNode.Parse(tags);
        int binStart = 20 + (int)BitConverter.ToUInt32(glb, 12) + 8;
        json["buffers"]![0]!["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(glb[binStart..]);
        var result = GltfImporter.Parse(Encoding.UTF8.GetBytes(json.ToJsonString()), null, lib, "dev_a");
        var mesh = Assert.Single(result.Meshes);
        Assert.Equal(6, mesh.Faces.Count);
        Assert.Equal(warningCount, result.Warnings.Count);
        Assert.Equal(tags.StartsWith('[') ? TagSet.Of("valid") : TagSet.Empty, mesh.Tags);
    }
}
