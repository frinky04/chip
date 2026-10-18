using System.Numerics;
using System.Text.Json.Nodes;
using Chip.Core.Geometry;
using Chip.Core.IO;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Core.Tests;

/// <summary>The exporter's baked world collision: what goes in, what comes out, and that re-import ignores it.</summary>
public class CollisionBakeExportTests
{
    static PolyMesh Box(Vector3 a, Vector3 b, string material = "wall") => MeshOps.CreateBox(a, b, material, TextureMapping.Default);

    static (JsonNode Json, ExportResult Result) Export(Document doc)
    {
        var r = GlbExporter.Build(doc, new MaterialLibrary(), null);
        return (Glb.Json(r.Data), r);
    }

    static List<JsonNode> Nodes(JsonNode json) => json["nodes"]!.AsArray().Select(n => n!).ToList();
    static bool IsBaked(JsonNode n) => n["extras"]?["chip_baked"]?.GetValue<bool>() == true;

    /// <summary>A 4 x 2 floor of 1 m tiles with a wall standing on it.</summary>
    static Document Room()
    {
        var doc = new Document();
        for (int x = 0; x < 4; x++)
            for (int z = 0; z < 2; z++)
                doc.Add(Box(new Vector3(x, -0.25f, z), new Vector3(x + 1, 0, z + 1)));
        doc.Add(Box(new Vector3(0, 0, 0), new Vector3(4, 2, 0.25f)));
        return doc;
    }

    [Fact]
    public void Default_meshes_render_without_suffix_and_collide_through_one_merged_bake()
    {
        var (json, result) = Export(Room());
        var nodes = Nodes(json);
        var render = nodes.Where(n => !IsBaked(n)).ToList();
        var baked = nodes.Where(IsBaked).ToList();

        Assert.Equal(9, render.Count);
        Assert.All(render, n => Assert.DoesNotContain("col", n["name"]!.GetValue<string>()));
        Assert.NotEmpty(baked);
        Assert.All(baked, n => Assert.EndsWith("-colonly", n["name"]!.GetValue<string>()));
        Assert.All(baked, n => Assert.Null(json["meshes"]![n["mesh"]!.GetValue<int>()]!["primitives"]![0]!["material"]));

        var stats = result.Collision!;
        Assert.True(stats.OutputTriangles < stats.InputTriangles / 2, $"{stats.InputTriangles} -> {stats.OutputTriangles}");
        Assert.True(stats.HiddenFacesRemoved > 0);
        Assert.Equal(0, stats.ConvexPieces);
    }

    [Fact]
    public void Convex_hulls_option_turns_convex_meshes_into_hulls()
    {
        var doc = Room();
        doc.Export.CollisionHulls = true;
        var (json, result) = Export(doc);
        var hulls = Nodes(json).Where(n => n["name"]!.GetValue<string>().EndsWith("-convcolonly")).ToList();
        Assert.Equal(9, hulls.Count);
        Assert.All(hulls, n => Assert.True(IsBaked(n)));
        Assert.Equal(9, result.Collision!.ConvexPieces);
    }

    [Fact]
    public void Explicit_collision_meshes_keep_their_own_nodes_and_non_colliding_ones_stay_out()
    {
        var doc = new Document();
        var solid = doc.Add(Box(Vector3.Zero, Vector3.One));
        var only = doc.Add(Box(new Vector3(3, 0, 0), new Vector3(4, 1, 1)));
        only.Collision = CollisionMode.Only;
        var none = doc.Add(Box(new Vector3(6, 0, 0), new Vector3(7, 1, 1)));
        none.Collision = CollisionMode.None;

        var (json, result) = Export(doc);
        var names = Nodes(json).Select(n => n["name"]!.GetValue<string>()).ToList();
        Assert.Contains($"block_{only.Id}-colonly", names);
        Assert.Contains($"block_{none.Id}", names);
        Assert.Contains($"block_{solid.Id}", names);
        Assert.Equal(12, result.Collision!.InputTriangles); // only the Default box is baked
    }

    [Fact]
    public void Nodraw_faces_collide_through_the_bake_instead_of_a_clip_node()
    {
        var doc = new Document();
        var box = doc.Add(Box(Vector3.Zero, Vector3.One));
        box.Faces[2].Material = "nodraw";
        var (json, result) = Export(doc);
        var names = Nodes(json).Select(n => n["name"]!.GetValue<string>()).ToList();
        Assert.DoesNotContain(names, n => n.Contains("_clip"));
        Assert.Equal(12, result.Collision!.InputTriangles); // all six faces, nodraw included
    }

    [Fact]
    public void A_document_that_does_not_collide_bakes_nothing()
    {
        var doc = Room();
        doc.ExportCollision = false;
        var (json, result) = Export(doc);
        Assert.DoesNotContain(Nodes(json), IsBaked);
        Assert.Null(result.Collision);
    }

    [Fact]
    public void Reimporting_skips_the_baked_collision()
    {
        var doc = Room();
        var lib = new MaterialLibrary();
        var imported = GltfImporter.Parse(GlbExporter.Build(doc, lib), null, lib, "wall", 1);
        Assert.Equal(doc.Meshes.Count, imported.Meshes.Count);
        Assert.All(imported.Meshes, m => Assert.Equal(CollisionMode.Default, m.Collision));
    }
}
