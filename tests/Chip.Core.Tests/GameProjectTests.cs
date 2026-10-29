using System.Numerics;
using System.Text.Json.Nodes;
using Chip.Core.Geometry;
using Chip.Core.IO;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Core.Tests;

/// <summary>Game projects other than Godot: <c>chip.project.json</c>, its manifest, and <c>--entities-check</c>.</summary>
public class GameProjectTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("chip-project-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    const string Manifest = """
        { "version": 1, "classes": {
            "spawn": { "props": [ { "name": "team", "type": "int", "default": 1 } ] },
            "door": { "props": [
              { "name": "partner", "type": "entity" },
              { "name": "sound", "type": "asset", "default": "sounds/creak.wav" },
              { "name": "script", "type": "unsupported", "engine": "LuaRef" } ] } } }
        """;

    string Write(string relative, string text)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void A_project_file_names_the_engine_the_manifest_and_its_conventions()
    {
        Write("chip.project.json", """{ "version": 1, "engine": "Toy", "entities": "data/entities.json", "assetPrefix": "assets/" }""");
        Write("data/entities.json", Manifest);
        var t = new EntityClassTracker();
        Assert.True(t.Track(Path.Combine(_dir, "levels", "a.chip")));
        Assert.Null(t.Problem);
        var p = t.Project!;
        Assert.Equal(("Toy", false, "assets/", false), (p.Engine, p.CollisionSuffixes, p.AssetPrefix, p.IsGodot));
        Assert.Equal(Path.Combine(_dir, "data", "entities.json"), p.Manifest);
        var door = t.Current.Find("door")!;
        Assert.Equal(PropType.Node, door.Prop("partner")!.Type);
        Assert.Equal(PropType.Resource, door.Prop("sound")!.Type);
        Assert.Equal("LuaRef", door.Prop("script")!.EngineType);
    }

    [Fact]
    public void A_project_file_beside_project_godot_keeps_godots_conventions_unless_it_says_otherwise()
    {
        Write("project.godot", "");
        Write("chip.project.json", """{ "entities": "tools/classes.json" }""");
        var p = GameProject.Find(_dir)!;
        Assert.Equal(("Godot", true, "res://", true), (p.Engine, p.CollisionSuffixes, p.AssetPrefix, p.IsGodot));
        Assert.Equal(Path.Combine(_dir, "tools", "classes.json"), p.Manifest);
    }

    [Fact]
    public void A_project_file_that_cant_be_read_says_why_and_offers_the_built_ins()
    {
        Write("chip.project.json", """{ "engine": "Toy", "entites": "x.json" }""");
        var t = new EntityClassTracker();
        Assert.True(t.Track(_dir));
        Assert.Same(EntityCatalog.BuiltIn, t.Current);
        Assert.Contains("unknown field \"entites\"", t.Problem);
    }

    [Fact]
    public void A_custom_project_without_a_manifest_says_which_file_to_write()
    {
        Write("chip.project.json", "{}");
        var t = new EntityClassTracker();
        t.Track(_dir);
        Assert.Contains(Path.Combine(".chip", "entities.json"), t.Problem);
        Assert.DoesNotContain("plugin", t.Problem);
    }

    [Fact]
    public void Pointing_the_project_file_at_another_manifest_reloads_the_classes()
    {
        var file = Write("chip.project.json", """{ "entities": "a.json" }""");
        Write("a.json", Manifest);
        Write("b.json", Manifest.Replace("\"spawn\"", "\"player_start\""));
        var t = new EntityClassTracker();
        t.Track(_dir);
        Assert.NotNull(t.Current.Find("spawn"));
        File.WriteAllText(file, """{ "entities": "b.json" }""");
        Assert.True(t.Track(_dir));
        Assert.NotNull(t.Current.Find("player_start"));
    }

    [Theory]
    [InlineData("""{ "kinds": [] }""", "\"kinds\" should be an object")]
    [InlineData("""{ "kinds": { "sound": 3 } }""", "kind sound should be an object")]
    [InlineData("""{ "kinds": { "sound": { "root": 42 } } }""", "kind sound: \"root\" should be text")]
    [InlineData("""{ "kinds": { "sound": { "root": "s", "ext": ".wav" } } }""", "\"ext\" should be a list of text")]
    [InlineData("""{ "kinds": { "sound": { "root": "s", "folders": "yes" } } }""", "\"folders\" should be true or false")]
    [InlineData("""{ "flags": { "power": { "type": 1 } } }""", "flag power: \"type\" should be text")]
    [InlineData("""{ "flags": { "power": { "scope": "game" } } }""", "\"type\" should be \"bool\" or \"int\"")]
    [InlineData("""{ "flags": { "power": { "type": "int", "default": true } } }""", "should be a whole number")]
    public void A_malformed_kind_or_flag_is_reported_not_thrown(string projectFile, string says)
    {
        Write("chip.project.json", projectFile);
        var p = GameProject.Find(_dir, out var problem);
        Assert.NotNull(p);
        Assert.Contains(says, problem);
        Assert.Empty(p!.Kinds);
    }

    [Fact]
    public void A_hand_written_entity_needs_only_its_id_and_class()
    {
        var doc = DocumentSerializer.Deserialize("""
            { "version": 3, "exportScale": 1, "exportCollision": true, "nextId": 1, "meshes": [],
              "entities": [ { "id": 4, "className": "spawn" } ] }
            """);
        var e = Assert.Single(doc.Entities);
        Assert.Equal(("", Vector3.Zero, Vector3.Zero, 5), (e.Name, e.Position, e.Rotation, doc.NextId));
        Assert.Equal("spawn_4", Glb.NodeNames(Glb.Json(GlbExporter.Build(doc, new MaterialLibrary())))[0]);
    }

    // ---- --entities-check ----

    [Fact]
    public void The_check_lists_classes_and_exits_clean_for_a_good_manifest()
    {
        Write("chip.project.json", """{ "engine": "Toy", "entities": "entities.json" }""");
        Write("entities.json", Manifest);
        var o = new StringWriter();
        Assert.Equal(0, EntityManifestCheck.Run(_dir, o));
        Assert.Contains("door", o.ToString());
        Assert.Contains("partner:entity", o.ToString());
        Assert.Contains("(Toy;", o.ToString());
    }

    [Fact]
    public void The_check_warns_about_what_the_editor_would_quietly_drop()
    {
        var manifest = Write("entities.json", """
            { "classes": { "crate": { "lable": "Crate", "props": [
                { "name": "hp", "type": "integer", "default": 3 },
                { "name": "kind", "type": "enum", "default": "wood" },
                { "name": "mass", "type": "float", "default": "heavy" },
                { "name": "mass", "type": "float" } ] } } }
            """);
        var o = new StringWriter();
        Assert.Equal(1, EntityManifestCheck.Run(manifest, o));
        var text = o.ToString();
        Assert.Contains("unknown key \"lable\"", text);
        Assert.Contains("unknown type \"integer\"", text);
        Assert.Contains("an enum needs \"options\"", text);
        Assert.Contains("default \"heavy\" isn't a float", text);
        Assert.Contains("declared twice", text);
    }

    static string IconFont()
    {
        for (var dir = AppContext.BaseDirectory; dir != null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "Chip.slnx"))) return Path.Combine(dir, "assets", "fonts", "tabler-icons.ttf");
        throw new DirectoryNotFoundException("no Chip.slnx above the test output");
    }

    [Fact]
    public void Icon_names_are_the_tabler_fonts_own()
    {
        EntityIcons.FontPath = IconFont();
        Assert.Equal(0xEF4E, EntityIcons.Codepoint("door"));
        Assert.Equal(0xEA89, EntityIcons.Codepoint("device-desktop"));
        Assert.Null(EntityIcons.Codepoint("not-an-icon"));
        Assert.True(EntityIcons.Names.Count > 5000);
        Assert.Null(EntityIcons.Problem("door"));
        Assert.Contains("can't draw yet", EntityIcons.Problem("number-34-small")); // past U+FFFF
    }

    [Fact]
    public void The_check_warns_about_icons_and_colours_it_cant_draw()
    {
        EntityIcons.FontPath = IconFont();
        var manifest = Write("entities.json", """
            { "classes": {
                "door": { "icon": "door", "color": "#3e8eed" },
                "crate": { "icon": "crate-box", "color": "brown" } } }
            """);
        var o = new StringWriter();
        Assert.Equal(1, EntityManifestCheck.Run(manifest, o));
        var text = o.ToString();
        Assert.DoesNotContain("door:", text);
        Assert.Contains("crate: icon \"crate-box\" isn't a Tabler icon", text);
        Assert.Contains("crate: color should be \"#rrggbb\"", text);
    }

    [Fact]
    public void The_check_fails_on_broken_json_and_on_a_missing_project()
    {
        var bad = Write("entities.json", "{ \"classes\": ");
        Assert.Equal(2, EntityManifestCheck.Run(bad, new StringWriter()));
        Assert.Equal(3, EntityManifestCheck.Run(Path.Combine(_dir, "nowhere"), new StringWriter()));
    }

    [Fact]
    public void The_check_reports_as_json()
    {
        var manifest = Write("entities.json", Manifest);
        var o = new StringWriter();
        EntityManifestCheck.Run(manifest, o, json: true);
        var json = JsonNode.Parse(o.ToString())!;
        Assert.Equal(0, json["errors"]!.GetValue<int>());
        Assert.Contains(json["classes"]!.AsArray(), c => c!["name"]!.GetValue<string>() == "spawn");
    }
}

/// <summary>Export in a project that isn't Godot's: colliders described by extras only (needs the global classes).</summary>
[Collection("Global entity classes")]
public class CustomEngineExport : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("chip-project-").FullName;

    public void Dispose()
    {
        EntityClasses.Reset();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Colliders_carry_extras_and_names_carry_no_godot_suffixes()
    {
        File.WriteAllText(Path.Combine(_dir, "chip.project.json"), """{ "engine": "Toy" }""");
        Directory.CreateDirectory(Path.Combine(_dir, ".chip"));
        File.WriteAllText(Path.Combine(_dir, GameProject.DefaultManifest), """{ "classes": { "spawn": {} } }""");
        EntityClasses.Track(Path.Combine(_dir, "level.glb"));
        Assert.Equal("Toy", EntityClasses.Engine);

        var doc = new Document();
        doc.Export.BakeCollision = false;
        var floor = doc.Add(MeshOps.CreateBox(Vector3.Zero, new Vector3(4, 0.5f, 4), "dev_a", TextureMapping.Default));
        var hull = doc.Add(MeshOps.CreateBox(new Vector3(0, 1, 0), Vector3.One * 2, "dev_a", TextureMapping.Default));
        hull.Collision = CollisionMode.ConvexOnly;
        var zone = doc.Add(MeshOps.CreateBox(new Vector3(5, 0, 0), new Vector3(6, 1, 1), "trigger", TextureMapping.Default));
        var nodes = Glb.Json(GlbExporter.Build(doc, new MaterialLibrary()))["nodes"]!.AsArray();

        Assert.DoesNotContain(nodes, n => n!["name"]!.GetValue<string>().Contains("-col"));
        JsonNode Node(string name) => nodes.Single(n => n!["name"]!.GetValue<string>() == name)!;
        var render = Node($"{floor.Name}_{floor.Id}")["extras"]!;
        Assert.Equal("trimesh", render["chip_collider"]!.GetValue<string>());
        Assert.Null(render["chip_render"]);
        var convex = Node($"{hull.Name}_{hull.Id}")["extras"]!;
        Assert.Equal(("convex", false), (convex["chip_collider"]!.GetValue<string>(), convex["chip_render"]!.GetValue<bool>()));
        var trigger = Node($"{zone.Name}_{zone.Id}_trigger")["extras"]!;
        Assert.Equal(("trigger", false), (trigger["chip_tool"]!.GetValue<string>(), trigger["chip_render"]!.GetValue<bool>()));
        Assert.Null(trigger["chip_collider"]); // a volume, never solid
    }

    [Fact]
    public void Suffix_free_files_import_back_with_their_collision()
    {
        File.WriteAllText(Path.Combine(_dir, "chip.project.json"), "{}");
        EntityClasses.Track(_dir);
        var doc = new Document();
        var box = doc.Add(MeshOps.CreateBox(Vector3.Zero, Vector3.One, "dev_a", TextureMapping.Default));
        box.Collision = CollisionMode.Only;
        var lib = new MaterialLibrary();
        var data = GlbExporter.Build(doc, lib);
        Assert.DoesNotContain("-colonly", Glb.NodeNames(Glb.Json(data))[0]);
        var imported = GltfImporter.Parse(data, null, lib, "dev_a");
        Assert.Equal(CollisionMode.Only, Assert.Single(imported.Meshes).Collision);
    }
}
