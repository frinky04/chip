using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.IO;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Core.Tests;

/// <summary>Tessellation for per-vertex lighting: render meshes get denser, collision never does.</summary>
public class TessellateExportTests
{
    static Document Level()
    {
        var doc = new Document();
        doc.Add(MeshOps.CreateBox(new Vector3(0, -0.25f, 0), new Vector3(8, 0, 8), "wall", TextureMapping.Default)); // floor
        doc.Add(MeshOps.CreateBox(new Vector3(0, 0, 0), new Vector3(8, 3, 0.25f), "wall", TextureMapping.Default)); // wall
        return doc;
    }

    static int RenderTriangles(byte[] glb)
    {
        var json = Glb.Json(glb);
        int n = 0;
        foreach (var node in json["nodes"]!.AsArray())
        {
            if (node!["extras"]?["chip_baked"] != null || node["mesh"] == null) continue;
            foreach (var p in json["meshes"]![node["mesh"]!.GetValue<int>()]!["primitives"]!.AsArray())
                n += json["accessors"]![p!["indices"]!.GetValue<int>()]!["count"]!.GetValue<int>() / 3;
        }
        return n;
    }

    [Fact]
    public void Tessellation_densifies_render_meshes_and_leaves_baked_collision_alone()
    {
        var lib = new MaterialLibrary();
        var plain = GlbExporter.Build(Level(), lib, null);
        var doc = Level();
        doc.Export.Tessellate = true;
        doc.Export.TessellateSpacing = 0.5f;
        var dense = GlbExporter.Build(doc, lib, null);

        Assert.Equal(24, RenderTriangles(plain.Data));
        Assert.True(RenderTriangles(dense.Data) > 24 * 10, $"{RenderTriangles(dense.Data)} render triangles");
        Assert.Equal((24, RenderTriangles(dense.Data)), dense.Tessellated);
        Assert.Equal(plain.Collision, dense.Collision); // colliders come from the original faces
    }

    [Fact]
    public void Without_baking_a_tessellated_mesh_collides_through_its_original_faces()
    {
        var doc = Level();
        doc.Export.BakeCollision = false;
        doc.Export.Tessellate = true;
        var json = Glb.Json(GlbExporter.Build(doc, new MaterialLibrary()));
        var names = Glb.NodeNames(json).ToList();
        var floor = doc.Meshes[0];
        Assert.Contains($"block_{floor.Id}", names);               // render node, no -col
        Assert.Contains($"block_{floor.Id}_col-colonly", names);   // collider: the original 12 triangles
        Assert.DoesNotContain(names, n => n.EndsWith("-col"));
        var colNode = json["nodes"]!.AsArray().First(n => n!["name"]!.GetValue<string>() == $"block_{floor.Id}_col-colonly")!;
        var prim = json["meshes"]![colNode["mesh"]!.GetValue<int>()]!["primitives"]![0]!;
        Assert.Equal(36, json["accessors"]![prim["indices"]!.GetValue<int>()]!["count"]!.GetValue<int>());
    }

    [Fact]
    public void Collision_only_meshes_are_never_tessellated()
    {
        var doc = Level();
        doc.Meshes[1].Collision = CollisionMode.Only;
        doc.Export.Tessellate = true;
        var r = GlbExporter.Build(doc, new MaterialLibrary(), null);
        Assert.Equal(12, r.Tessellated!.Value.Before); // only the floor's render faces
    }

    [Fact]
    public void Tessellated_exports_reimport_as_the_original_shapes()
    {
        var doc = Level();
        doc.Export.Tessellate = true;
        var lib = new MaterialLibrary();
        var imported = GltfImporter.Parse(GlbExporter.Build(doc, lib), null, lib, "wall", 1);
        Assert.Equal(2, imported.Meshes.Count);
        // The importer rebuilds coplanar triangles into n-gons, so each box comes back with 6 faces.
        Assert.All(imported.Meshes, m => Assert.Equal(6, m.Faces.Count));
    }
}
