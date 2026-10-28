using System.Numerics;
using System.Text.Json.Nodes;
using Chip.Core.Geometry;
using Chip.Core.IO;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Core.Tests;

/// <summary>Mesh entities: meshes tied to an entity move, copy, delete, save and export as one.</summary>
public class MeshEntityTests
{
    static PolyMesh Box(Vector3 min, Vector3 max, string material = "dev_a") => MeshOps.CreateBox(min, max, material, TextureMapping.Default);

    static (Document Doc, Entity Door, PolyMesh A, PolyMesh B) Door()
    {
        var doc = new Document();
        var a = doc.Add(Box(new Vector3(2, 0, 0), new Vector3(3, 2, 0.1f)));
        var b = doc.Add(Box(new Vector3(3, 0, 0), new Vector3(4, 2, 0.1f)));
        var door = MeshEntityOps.Tie(doc, [a.Id, b.Id], "door")!;
        return (doc, door, a, b);
    }

    [Fact]
    public void Tying_meshes_makes_an_entity_whose_origin_is_their_bottom_centre()
    {
        var (doc, door, a, b) = Door();
        Assert.Equal([a.Id, b.Id], door.Meshes);
        Assert.Equal(new Vector3(3, 0, 0.05f), door.Position);
        Assert.Same(door, MeshEntityOps.OwnerOf(doc, b.Id));
        // Tying one of them to something else takes it out of the door.
        var lift = MeshEntityOps.Tie(doc, [b.Id], "lift")!;
        Assert.Equal([a.Id], door.Meshes);
        Assert.Equal([b.Id], lift.Meshes);
        // ...and an entity left with nothing goes.
        MeshEntityOps.Tie(doc, [a.Id], "lift");
        Assert.DoesNotContain(door, doc.Entities);
    }

    [Fact]
    public void Selecting_any_part_selects_the_whole_entity()
    {
        var (doc, door, a, b) = Door();
        var ids = new HashSet<int> { a.Id };
        MeshEntityOps.Expand(doc, ids);
        Assert.Equal(new HashSet<int> { a.Id, b.Id, door.Id }, ids);
        ids = [door.Id];
        MeshEntityOps.Expand(doc, ids);
        Assert.Equal(3, ids.Count);
    }

    [Fact]
    public void Deleting_the_entity_takes_its_meshes_and_deleting_the_meshes_takes_the_entity()
    {
        var (doc, door, _, _) = Door();
        SceneOps.Delete(doc, [door.Id]);
        Assert.Empty(doc.Meshes);

        (doc, door, var a, var b) = Door();
        SceneOps.Delete(doc, [a.Id]);
        Assert.Equal([b.Id], door.Meshes);
        SceneOps.Delete(doc, [b.Id]);
        Assert.Empty(doc.Entities);
    }

    [Fact]
    public void Untying_keeps_the_meshes()
    {
        var (doc, door, _, _) = Door();
        MeshEntityOps.Untie(doc, door);
        Assert.Empty(doc.Entities);
        Assert.Equal(2, doc.Meshes.Count);
    }

    [Fact]
    public void A_duplicate_owns_copies_of_the_meshes()
    {
        var (doc, door, a, b) = Door();
        var copies = SceneOps.Duplicate(doc, [door.Id]);
        Assert.Equal(3, copies.Count);
        var copy = doc.Entities.Single(e => e != door);
        Assert.Equal(2, copy.Meshes.Count);
        Assert.DoesNotContain(a.Id, copy.Meshes);
        Assert.All(copy.Meshes, id => Assert.NotNull(doc.Find(id)));
        Assert.Equal([a.Id, b.Id], door.Meshes);
    }

    [Fact]
    public void Mesh_entities_survive_save_and_load()
    {
        var (doc, door, a, b) = Door();
        var loaded = DocumentSerializer.Deserialize(DocumentSerializer.Serialize(doc));
        Assert.Equal([a.Id, b.Id], loaded.FindEntity(door.Id)!.Meshes);
        // A hand edit naming a missing mesh, or a mesh another entity has, drops that id.
        var json = JsonNode.Parse(DocumentSerializer.Serialize(doc))!;
        json["entities"]![0]!["meshes"] = new JsonArray(a.Id, 99);
        Assert.Equal([a.Id], DocumentSerializer.Deserialize(json.ToJsonString()).FindEntity(door.Id)!.Meshes);
    }

    [Fact]
    public void The_meshes_export_as_children_of_the_entity_in_its_space()
    {
        var (doc, door, a, _) = Door();
        door.Rotation = new Vector3(0, 90, 0);
        doc.ExportScale = 2;
        var floor = doc.Add(Box(new Vector3(-5, -1, -5), new Vector3(5, 0, 5)));
        var json = Glb.Json(GlbExporter.Build(doc, new MaterialLibrary()));
        var nodes = json["nodes"]!.AsArray();
        int entityIndex = nodes.Select((n, i) => (n, i)).Single(x => x.n!["extras"]?["chip_class"] != null).i;
        var entity = nodes[entityIndex]!;
        var kids = entity["children"]!.AsArray().Select(k => k!.GetValue<int>()).ToList();
        Assert.Equal(2, kids.Count);
        var roots = json["scenes"]![0]!["nodes"]!.AsArray().Select(r => r!.GetValue<int>()).ToList();
        Assert.Contains(entityIndex, roots);
        Assert.DoesNotContain(kids[0], roots);

        // Its own collider, not part of the baked world, and no Godot suffix under the entity.
        var child = nodes[kids[0]]!;
        Assert.Equal($"block_{a.Id}", child["name"]!.GetValue<string>());
        Assert.Equal("trimesh", child["extras"]!["chip_collider"]!.GetValue<string>());
        Assert.Contains(nodes, n => n!["name"]!.GetValue<string>().StartsWith("chip_world_collision"));
        Assert.Contains(nodes, n => n!["name"]!.GetValue<string>() == $"block_{floor.Id}");

        // Vertices are relative to the origin: entity transform × local vertex = the world vertex (× export scale).
        var mesh = json["meshes"]![child["mesh"]!.GetValue<int>()]!;
        var local = Glb.Vec3(GlbExporter.Build(doc, new MaterialLibrary()), mesh["primitives"]![0]!["attributes"]!["POSITION"]!.GetValue<int>());
        var t = entity["translation"]!.AsArray().Select(x => x!.GetValue<float>()).ToArray();
        var r = entity["rotation"]!.AsArray().Select(x => x!.GetValue<float>()).ToArray();
        var world = local.Select(p => Vector3.Transform(p, new Quaternion(r[0], r[1], r[2], r[3])) + new Vector3(t[0], t[1], t[2])).ToList();
        var expected = a.Positions.Select(p => p * 2).ToList();
        Assert.All(world, p => Assert.Contains(expected, q => Vector3.Distance(p, q) < 1e-4f));
    }

    [Fact]
    public void An_exported_mesh_entity_imports_tied_again()
    {
        var (doc, door, _, _) = Door();
        var lib = new MaterialLibrary();
        var result = GltfImporter.Parse(GlbExporter.Build(doc, lib), null, lib, "dev_a");
        var target = new Document();
        result.AddTo(target);
        var e = Assert.Single(target.Entities);
        Assert.Equal("door", e.ClassName);
        Assert.Equal(2, e.Meshes.Count);
        Assert.All(e.Meshes, id => Assert.NotNull(target.Find(id)));
        // Back in world space where they started.
        var min = target.Meshes.SelectMany(m => m.Positions).Aggregate(new Vector3(float.MaxValue), Vector3.Min);
        Assert.True(Vector3.Distance(new Vector3(2, 0, 0), min) < 1e-3f);
    }
}

/// <summary>A game's own surfaces from chip.project.json, and how they export (needs the global project).</summary>
[Collection("Global entity classes")]
public class SurfaceTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("chip-surfaces-").FullName;

    public void Dispose()
    {
        EntityClasses.Reset();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    void Project(string surfaces)
    {
        File.WriteAllText(Path.Combine(_dir, "chip.project.json"), $$"""{ "engine": "Toy", "surfaces": {{surfaces}} }""");
        EntityClasses.Track(_dir);
        Assert.Equal("Toy", EntityClasses.Engine); // read (it has no manifest, which is fine here)
    }

    const string GameSurfaces = """
        {
          "monster_clip": { "render": false, "collide": "always", "look": { "color": "#a040ff" } },
          "water": { "collide": "none", "volume": true, "look": { "color": [0.1, 0.4, 1], "opacity": 0.4 } },
          "screen": { "doc": "Swapped for a video", "look": { "image": "screen.png" } },
          "clip": { "look": { "color": "#ff0000" } }
        }
        """;

    [Fact]
    public void The_project_adds_surfaces_and_changes_the_built_ins()
    {
        Project(GameSurfaces);
        var monster = Surfaces.Of("monster_clip")!;
        Assert.Equal((false, SurfaceCollide.Always, false), (monster.Render, monster.Collide, monster.Volume));
        Assert.Equal(SurfacePattern.Stripes, monster.Look.Pattern); // a tool surface's default look
        var water = Surfaces.Of("water")!;
        Assert.Equal((true, SurfaceCollide.None, true, 0.4f), (water.Render, water.Collide, water.Volume, water.Look.Opacity));
        Assert.Equal(Path.Combine(_dir, "screen.png"), Surfaces.Of("screen")!.Look.Image);
        // The built-in keeps its behaviour and takes the new colour, and "playerclip" still finds it.
        var clip = Surfaces.Of("playerclip")!;
        Assert.Equal((SurfaceCollide.Always, new Vector3(1, 0, 0)), (clip.Collide, clip.Look.Color));
        Assert.Equal(["monster_clip", "water", "screen", "clip", "nodraw", "trigger", "skip"], Surfaces.All.Select(s => s.Name));
    }

    [Fact]
    public void A_surface_with_an_unknown_field_makes_the_project_unreadable()
    {
        File.WriteAllText(Path.Combine(_dir, "chip.project.json"), """{ "surfaces": { "lava": { "colide": "always" } } }""");
        EntityClasses.Track(_dir);
        Assert.Contains("unknown field \"colide\"", EntityClasses.Problem);
    }

    [Fact]
    public void Each_surface_exports_as_what_it_does()
    {
        Project(GameSurfaces);
        var doc = new Document();
        doc.Export.BakeCollision = false;
        var m = doc.Add(MeshOps.CreateBox(Vector3.Zero, Vector3.One, "dev_a", TextureMapping.Default));
        m.Faces[0].Material = "monster_clip";
        m.Faces[1].Material = "water";
        m.Faces[2].Material = "screen";
        var json = Glb.Json(GlbExporter.Build(doc, new MaterialLibrary()));
        var nodes = json["nodes"]!.AsArray().Select(n => n!).ToList();
        JsonNode Node(string surface, bool volume = false) => nodes.Single(n =>
            n["extras"]?["chip_surface"]?.GetValue<string>() == surface && (n["extras"]?["chip_volume"] != null) == volume);

        var monster = Node("monster_clip")["extras"]!;
        Assert.Equal(("trimesh", false), (monster["chip_collider"]!.GetValue<string>(), monster["chip_render"]!.GetValue<bool>()));
        Assert.Null(monster["chip_tool"]);
        var waterVisual = Node("water");
        Assert.Null(waterVisual["extras"]!["chip_collider"]); // renders, never solid
        Assert.NotNull(json["meshes"]![waterVisual["mesh"]!.GetValue<int>()]!["primitives"]![0]!["material"]);
        var waterVolume = Node("water", volume: true)["extras"]!;
        Assert.Null(waterVolume["chip_collider"]);
        Assert.False(waterVolume["chip_render"]!.GetValue<bool>());
        // The screen renders with the level (and collides with it); its material says which surface it is.
        var screen = json["materials"]!.AsArray().Single(x => x!["name"]!.GetValue<string>() == "screen")!;
        Assert.Equal("screen", screen["extras"]!["chip_surface"]!.GetValue<string>());
        Assert.Equal($"block_{m.Id}", nodes[0]["name"]!.GetValue<string>());
    }
}
