using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Chip.Core.Geometry;
using Chip.Core.IO;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Core.Tests;

/// <summary>
/// Entity classes from a Godot project's manifest: reading it, typed values, finding the project, migrating v2 levels,
/// and what export does with the values. Tests that swap the global classes run alone (<see cref="GlobalClasses"/>).
/// </summary>
public class EntityCatalogTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("chip-classes-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>The shape the Godot addon writes (see godot/addons/chip_import/chip_manifest.gd).</summary>
    public const string Manifest = """
        {
          "version": 1,
          "classes": {
            "door": {
              "scene": "res://entities/door.tscn", "uid": "uid://dlm1osw5eeb10", "label": "Door", "base": "Node3D",
              "doc": "A door.",
              "props": [
                { "name": "mode", "type": "enum", "options": [{ "label": "swing", "value": "swing" }, { "label": "slide", "value": "slide" }], "default": "swing", "doc": "How it opens." },
                { "name": "open_angle", "type": "float", "range": [0.0, 180.0, 1.0], "or_greater": true, "suffix": "°", "default": 100.0 },
                { "name": "locked", "type": "bool", "default": false },
                { "name": "tint", "type": "color", "default": [0.8, 0.5, 0.2, 1.0] },
                { "name": "keys_needed", "type": "int", "default": 1 },
                { "name": "partner", "type": "node", "class": "Node3D", "default": null },
                { "name": "skin", "type": "resource", "class": "Material", "default": null },
                { "name": "volume", "type": "float", "range": [0.0, 1.0, 0.0], "default": 0.8, "group": "Sound" },
                { "name": "sound_offset", "type": "vector3", "default": [0.0, 1.0, 0.0], "group": "Sound" },
                { "name": "tags", "type": "array", "of": "string", "default": ["wood"], "group": "Lists" },
                { "name": "cells", "type": "array", "of": "vector2", "integer": true, "default": [], "group": "Lists" },
                { "name": "linked", "type": "array", "of": "node", "class": "Node3D", "default": [], "group": "Lists" },
                { "name": "grid", "type": "array", "of": "array", "group": "Lists" },
                { "name": "notes", "type": "unsupported", "godot": "Dictionary", "group": "Lists" }
              ],
              "bounds": [[-0.5, 0.0, -0.05], [0.5, 2.0, 0.05]],
              "preview": "previews/door.glb"
            },
            "enemies/grunt": {
              "scene": "res://entities/enemies/grunt.tscn", "label": "Grunt", "base": "CharacterBody3D",
              "props": [
                { "name": "mood", "type": "enum", "options": [{ "label": "Calm", "value": 0 }, { "label": "Alert", "value": 5 }], "default": 0 },
                { "name": "health", "type": "int", "default": "lots" }
              ]
            }
          }
        }
        """;

    static EntityCatalog Parse(string? path = null) => EntityCatalog.Parse(Manifest, path);

    [Fact]
    public void Built_in_classes_are_the_addons_scenes_plus_reference_models()
    {
        var b = EntityCatalog.BuiltIn;
        Assert.Equal(["info_player_start", "info_target", "light_point", "light_spot", ReferenceModels.ClassName], b.Classes.Select(c => c.Name));
        var light = b.Find("light_point")!;
        Assert.Equal("res://addons/chip_import/entities/light_point.tscn", light.Scene);
        Assert.Equal("OmniLight3D", light.Base);
        Assert.Equal(PropType.Color, light.Prop("light_color")!.Type);
        Assert.Equal(new Vector3(0.3f, 1.8f, 0.3f), b.Find("info_player_start")!.BoxMax);
        Assert.True(b.Find(ReferenceModels.ClassName)!.EditorOnly);
    }

    [Fact]
    public void A_manifest_gives_typed_properties_in_order_with_their_hints()
    {
        var catalog = Parse(Path.Combine(_dir, ".chip", "entities.json"));
        var door = catalog.Find("door")!;
        Assert.Equal("uid://dlm1osw5eeb10", door.Uid);
        Assert.Equal("A door.", door.Doc);
        Assert.Equal(["mode", "open_angle", "locked", "tint", "keys_needed", "partner", "skin", "volume", "sound_offset", "tags", "cells", "linked", "grid", "notes"], door.Props.Select(p => p.Name));
        var mode = door.Prop("mode")!;
        Assert.Equal(PropType.Enum, mode.Type);
        Assert.Equal(["swing", "slide"], mode.Options.Select(o => o.Label));
        Assert.Equal("How it opens.", mode.Doc);
        var angle = door.Prop("open_angle")!;
        Assert.Equal((0, 180, 1), angle.Range);
        Assert.True(angle.OrGreater);
        Assert.Equal("°", angle.Suffix);
        Assert.Equal("Open angle", angle.Label);
        Assert.Equal("Material", door.Prop("skin")!.Class);
        Assert.Equal("Sound", door.Prop("volume")!.Group);
        Assert.Equal("Dictionary", door.Prop("notes")!.EngineType);
        var tags = door.Prop("tags")!;
        Assert.Equal((PropType.Array, PropType.String), (tags.Type, tags.ItemType));
        Assert.Equal("""["wood"]""", tags.Default.GetRawText());
        Assert.True(door.Prop("cells")!.Item.Integer); // item hints come from the list
        Assert.Equal("Node3D", door.Prop("linked")!.Item.Class);
        Assert.Equal(PropType.Unsupported, door.Prop("grid")!.Type); // lists of lists aren't editable
        Assert.Equal("list of text", EntityValues.TypeName(tags));
        Assert.Equal(new Vector3(0.5f, 2, 0.05f), door.BoxMax);
        Assert.Equal(Path.Combine(_dir, ".chip", "previews", "door.glb"), door.Preview);
        Assert.Equal("Grunt", catalog.Find("enemies/grunt")!.Label);
        Assert.NotNull(catalog.Find(ReferenceModels.ClassName)); // every catalog has reference models
    }

    [Fact]
    public void A_default_the_type_cant_read_becomes_none_instead_of_poisoning_entities()
    {
        var health = Parse().Find("enemies/grunt")!.Prop("health")!;
        Assert.True(EntityValues.IsNull(health.Default));
    }

    [Theory]
    [InlineData("A door.", "A door.")]
    [InlineData("A pedestal with a button. Pressed, it does its action:\n- HEALTH: hurts\n- SPAWN: spawns", "A pedestal with a button. Pressed, it does its action…")]
    [InlineData("First paragraph.\nSecond.", "First paragraph.")]
    [InlineData("A pedestal with a button on top, for trying things out in a test level, which you press to run its action and see what happens", "A pedestal with a button on top, for trying things out in a test level, which…")]
    public void Long_class_docs_are_summarised_to_their_first_line(string doc, string summary)
    {
        var cls = new EntityClass { Name = "x", Label = "X", Doc = doc };
        Assert.Equal(summary, EntityClass.Summarize(doc, 80));
        Assert.True(cls.Summary.Length <= 161);
    }

    [Theory]
    [InlineData("""{ "classes": [] }""", "classes")]
    [InlineData("""{ "version": 99, "classes": {} }""", "newer")]
    [InlineData("""{ "classes": { "x": { "props": [{ "type": "int" }] } } }""", "without a name")]
    [InlineData("not json", "JSON")]
    public void Broken_manifests_say_what_is_wrong(string json, string expected)
    {
        var e = Assert.Throws<InvalidDataException>(() => EntityCatalog.Parse(json, null));
        Assert.Contains(expected, e.Message);
    }

    // ---- Values ----

    static PropSpec Spec(string cls, string prop) => Parse().Find(cls)!.Prop(prop)!;

    static string Coerced(PropSpec p, JsonElement v) => EntityValues.Coerce(p, v, out var error)?.GetRawText() ?? $"error: {error}";

    [Fact]
    public void Values_are_read_tolerantly_and_written_typed()
    {
        Assert.Equal("120", Coerced(Spec("door", "open_angle"), EntityValues.Of("120")));
        Assert.Equal("120.5", Coerced(Spec("door", "open_angle"), EntityValues.Of(120.5)));
        Assert.Equal("3", Coerced(Spec("door", "keys_needed"), EntityValues.Of(2.6)));
        Assert.Equal("true", Coerced(Spec("door", "locked"), EntityValues.Of("1")));
        Assert.Equal("false", Coerced(Spec("door", "locked"), EntityValues.Of("off")));
        Assert.Equal("\"slide\"", Coerced(Spec("door", "mode"), EntityValues.Of("slide")));
        Assert.Equal("5", Coerced(Spec("enemies/grunt", "mood"), EntityValues.Of("Alert"))); // by label
        Assert.Equal("5", Coerced(Spec("enemies/grunt", "mood"), EntityValues.Of(5.0)));      // by value
        Assert.Equal("[0,2,0]", Coerced(Spec("door", "sound_offset"), EntityValues.Of("0 2 0")));
        Assert.Equal("""{"entity":7}""", Coerced(Spec("door", "partner"), EntityValues.Ref(7)));
        Assert.Equal("null", Coerced(Spec("door", "partner"), EntityValues.Null));
        Assert.Equal("\"res://m.tres\"", Coerced(Spec("door", "skin"), EntityValues.Of("res://m.tres")));
        Assert.StartsWith("error: open_angle is wide", Coerced(Spec("door", "open_angle"), EntityValues.Of("wide")));
        Assert.StartsWith("error: mode is fly, which isn't one of swing, slide", Coerced(Spec("door", "mode"), EntityValues.Of("fly")));
    }

    [Fact]
    public void List_items_are_read_as_the_item_type()
    {
        static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;
        Assert.Equal("""["oak","3"]""", Coerced(Spec("door", "tags"), J("""["oak", 3]""")));
        Assert.Equal("""["oak","heavy"]""", Coerced(Spec("door", "tags"), EntityValues.Of("oak, heavy"))); // text kept as a list before lists
        Assert.Equal("[]", Coerced(Spec("door", "tags"), EntityValues.Null));
        Assert.Equal("[[1,2],[3,4]]", Coerced(Spec("door", "cells"), J("[[1.4, 2], \"3 4\"]")));
        Assert.Equal("[[1,2]]", Coerced(Spec("door", "cells"), EntityValues.Array(1, 2))); // one value: a list of one
        Assert.Equal("""[{"entity":7},null]""", Coerced(Spec("door", "linked"), J("""[{ "entity": 7 }, null]"""))); // unpicked rows stay
        Assert.Equal("error: cells is x, which isn't a 2D vector (item 2)", Coerced(Spec("door", "cells"), J("[[1, 2], \"x\"]")));
    }

    [Fact]
    public void A_list_equal_to_the_default_follows_the_default()
    {
        var e = new Entity { ClassName = "door" };
        Assert.Null(EntityOps.SetProperty(e, Spec("door", "tags"), EntityValues.List([EntityValues.Of("oak")])));
        Assert.Equal("""["oak"]""", e.Properties["tags"].GetRawText());
        Assert.Null(EntityOps.SetProperty(e, Spec("door", "tags"), EntityValues.Of("wood")));
        Assert.False(e.Properties.ContainsKey("tags"));
    }

    [Theory]
    [InlineData("[1, 0.5, 0]", 1, 0.5f, 0, 1)]
    [InlineData("[1, 0.5, 0, 0.25]", 1, 0.5f, 0, 0.25f)]
    [InlineData("\"#ff8000\"", 1, 128 / 255f, 0, 1)]
    [InlineData("\"ff800080\"", 1, 128 / 255f, 0, 128 / 255f)]
    [InlineData("\"255 128 0\"", 1, 128 / 255f, 0, 1)]
    [InlineData("\"1 0.5 0\"", 1, 0.5f, 0, 1)]
    [InlineData("\"255 128 0 200\"", 1, 128 / 255f, 0, 1)] // Hammer's _light: brightness ignored
    public void Colours_read_from_every_form_levels_have_used(string json, float r, float g, float b, float a)
    {
        var c = EntityValues.Color(JsonDocument.Parse(json).RootElement)!.Value;
        Assert.Equal(new Vector4(r, g, b, a), c);
    }

    [Fact]
    public void Floats_from_the_ui_are_stored_as_the_decimal_they_show()
    {
        Assert.Equal("0.8", EntityValues.Of(0.8f).GetRawText());
        Assert.Equal("[0.8,0.1,1,1]", EntityValues.Color(new Vector4(0.8f, 0.1f, 1, 1)).GetRawText());
    }

    // ---- Projects ----

    string Project(string? manifest)
    {
        File.WriteAllText(Path.Combine(_dir, "project.godot"), "config_version=5\n");
        Directory.CreateDirectory(Path.Combine(_dir, "levels"));
        if (manifest != null)
        {
            Directory.CreateDirectory(Path.Combine(_dir, ".chip"));
            File.WriteAllText(Path.Combine(_dir, EntityClassTracker.ManifestPath), manifest);
        }
        return Path.Combine(_dir, "levels", "level01.chip");
    }

    [Fact]
    public void The_project_is_found_from_the_level_and_its_manifest_loaded()
    {
        var level = Project(Manifest);
        var t = new EntityClassTracker();
        Assert.True(t.Track(null, level));
        Assert.Equal(_dir, t.ProjectRoot);
        Assert.Null(t.Problem);
        Assert.NotNull(t.Current.Find("door"));
        Assert.False(t.Track(null, level)); // unchanged: no reload
    }

    [Fact]
    public void An_edited_manifest_reloads_and_a_broken_one_keeps_the_last_good_classes()
    {
        var level = Project(Manifest);
        var t = new EntityClassTracker();
        t.Track(level);
        int revision = t.Revision;

        File.WriteAllText(Path.Combine(_dir, EntityClassTracker.ManifestPath), Manifest.Replace("\"door\"", "\"gate\""));
        Assert.True(t.Track(level));
        Assert.NotNull(t.Current.Find("gate"));
        Assert.True(t.Revision > revision);

        File.WriteAllText(Path.Combine(_dir, EntityClassTracker.ManifestPath), "{ \"classes\": ");
        Assert.True(t.Track(level));
        Assert.NotNull(t.Current.Find("gate"));
        Assert.Contains("Can't read the entity classes", t.Problem);
    }

    [Fact]
    public void A_project_without_a_manifest_offers_the_built_in_classes_and_says_how_to_get_its_own()
    {
        var level = Project(null);
        var t = new EntityClassTracker();
        Assert.True(t.Track(level));
        Assert.Same(EntityCatalog.BuiltIn, t.Current);
        Assert.Contains("Enable the Chip import plugin", t.Problem);
    }

    [Fact]
    public void A_level_outside_any_project_uses_the_built_in_classes_quietly()
    {
        var t = new EntityClassTracker();
        Assert.False(t.Track(Path.Combine(_dir, "loose.chip"))); // nothing changed from the start
        Assert.Same(EntityCatalog.BuiltIn, t.Current);
        Assert.Null(t.Problem);
    }

    // ---- Levels ----

    [Fact]
    public void Version_2_lights_move_to_godot_property_names_and_drop_values_equal_to_the_default()
    {
        const string v2 = """
            { "version": 2, "exportScale": 1, "exportCollision": true, "nextId": 4, "meshes": [],
              "entities": [
                { "id": 1, "className": "light_point", "name": "", "position": [0,0,0], "rotation": [0,0,0], "hidden": false,
                  "properties": { "color": "255 255 255", "energy": "2", "range": "10" } },
                { "id": 2, "className": "light_spot", "name": "", "position": [0,0,0], "rotation": [0,0,0], "hidden": false,
                  "properties": { "color": "255 128 0", "range": "10", "angle": "30", "shadows": "1" } },
                { "id": 3, "className": "prop_marker", "name": "", "position": [0,0,0], "rotation": [0,0,0], "hidden": false,
                  "properties": { "model": "crate", "range": "4" } }
              ] }
            """;
        var doc = DocumentSerializer.Deserialize(v2);
        var point = doc.FindEntity(1)!.Properties;
        Assert.Equal(["light_energy", "omni_range"], point.Keys.Order());
        Assert.Equal(2, point["light_energy"].GetDouble());
        Assert.Equal(10, point["omni_range"].GetDouble());
        var spot = doc.FindEntity(2)!.Properties;
        Assert.Equal(["light_color", "shadow_enabled", "spot_angle", "spot_range"], spot.Keys.Order());
        Assert.Equal(JsonValueKind.Array, spot["light_color"].ValueKind);
        Assert.True(spot["shadow_enabled"].GetBoolean());
        Assert.Equal(30, spot["spot_angle"].GetDouble());
        // Classes the project doesn't know keep their values as they were.
        Assert.Equal("4", doc.FindEntity(3)!.Properties["range"].GetString());

        var again = DocumentSerializer.Deserialize(DocumentSerializer.Serialize(doc));
        Assert.True(EntityValues.SameProperties(spot, again.FindEntity(2)!.Properties)); // v3 loads as saved
    }

    [Fact]
    public void Imported_entities_keep_references_to_each_other_under_their_new_ids()
    {
        var a = new Entity { Id = 10, ClassName = "door" };
        var b = new Entity { Id = 11, ClassName = "door" };
        a.Properties["partner"] = EntityValues.Ref(11);
        b.Properties["partner"] = EntityValues.Ref(10);
        b.Properties["target"] = EntityValues.Ref(99); // not in the file
        a.Properties["linked"] = EntityValues.List([EntityValues.Ref(11), EntityValues.Ref(99), EntityValues.Of("x")]);
        var doc = new Document();
        doc.Add(MeshOps.CreateBox(Vector3.Zero, Vector3.One, "m", TextureMapping.Default));
        SceneOps.AddEntities(doc, [a, b]);
        Assert.NotEqual(10, a.Id);
        Assert.Equal(b.Id, EntityValues.RefId(a.Properties["partner"]));
        Assert.Equal(a.Id, EntityValues.RefId(b.Properties["partner"]));
        Assert.False(b.Properties.ContainsKey("target"));
        Assert.Equal($$"""[{"entity":{{b.Id}}},"x"]""", a.Properties["linked"].GetRawText()); // items follow; the outsider leaves
    }

    [Fact]
    public void Duplicated_entities_refer_to_each_others_copies()
    {
        var doc = new Document();
        var a = doc.Add(new Entity { ClassName = "door" });
        var b = doc.Add(new Entity { ClassName = "door" });
        var outside = doc.Add(new Entity { ClassName = "info_target" });
        a.Properties["partner"] = EntityValues.Ref(b.Id);
        a.Properties["target"] = EntityValues.Ref(outside.Id);
        a.Properties["linked"] = EntityValues.List([EntityValues.Ref(b.Id), EntityValues.Ref(outside.Id)]);
        var copies = SceneOps.Duplicate(doc, [a.Id, b.Id]);
        var a2 = doc.FindEntity(copies[0])!;
        Assert.Equal(copies[1], EntityValues.RefId(a2.Properties["partner"]));
        Assert.Equal(outside.Id, EntityValues.RefId(a2.Properties["target"]));
        Assert.Equal($$"""[{"entity":{{copies[1]}}},{"entity":{{outside.Id}}}]""", a2.Properties["linked"].GetRawText());
        Assert.Equal(b.Id, EntityValues.RefId(a.Properties["partner"])); // the original is untouched
    }
}

/// <summary>Tests that swap the global entity classes, run after the parallel ones so nothing else sees the swap.</summary>
[CollectionDefinition("Global entity classes", DisableParallelization = true)]
public class GlobalClassesCollection;

[Collection("Global entity classes")]
public class GlobalClasses : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("chip-classes-").FullName;

    public GlobalClasses()
    {
        Directory.CreateDirectory(Path.Combine(_dir, ".chip"));
        File.WriteAllText(Path.Combine(_dir, "project.godot"), "");
        var manifest = Path.Combine(_dir, EntityClassTracker.ManifestPath);
        File.WriteAllText(manifest, EntityCatalogTests.Manifest);
        EntityClasses.Use(EntityCatalog.Parse(EntityCatalogTests.Manifest, manifest));
    }

    public void Dispose()
    {
        EntityClasses.Reset();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Export_writes_the_scene_and_typed_values_and_leaves_out_ones_godot_cant_use()
    {
        var doc = new Document();
        doc.Export.IncludeHidden = false;
        var light = doc.Add(new Entity { ClassName = "info_target" }); // not in this project
        var door = doc.Add(new Entity { ClassName = "door", Name = "front" });
        var hidden = doc.Add(new Entity { ClassName = "door", Hidden = true });
        door.Properties["open_angle"] = EntityValues.Of("120");     // v2-style text: goes out as a number
        door.Properties["mode"] = EntityValues.Of("fly");           // not an option: left out, with a warning
        door.Properties["partner"] = EntityValues.Ref(hidden.Id);   // not exported: left out, with a warning
        door.Properties["sound_offset"] = EntityValues.Array(0, 2, 0);
        door.Properties["tags"] = EntityValues.Of("oak, heavy");    // text from before lists: goes out as a list
        door.Properties["linked"] = EntityValues.List([EntityValues.Ref(light.Id), EntityValues.Ref(hidden.Id)]); // the hidden one is left out
        light.Properties["note"] = EntityValues.Of("x");

        var result = GlbExporter.Build(doc, new MaterialLibrary(), null);
        var nodes = Glb.Json(result.Data)["nodes"]!.AsArray();
        var d = nodes.First(n => n!["name"]!.GetValue<string>() == "front")!["extras"]!;
        Assert.Equal("door", d["chip_class"]!.GetValue<string>());
        Assert.Equal("uid://dlm1osw5eeb10", d["chip_uid"]!.GetValue<string>());
        Assert.Equal("res://entities/door.tscn", d["chip_scene"]!.GetValue<string>());
        var props = d["properties"]!.AsObject();
        Assert.Equal(["linked", "open_angle", "sound_offset", "tags"], props.Select(kv => kv.Key));
        Assert.Equal(120, props["open_angle"]!.GetValue<double>());
        Assert.Equal("""["oak","heavy"]""", props["tags"]!.ToJsonString());
        Assert.Equal($$"""[{"entity":{{light.Id}}}]""", props["linked"]!.ToJsonString());
        Assert.Equal(3, result.Warnings.Count);
        Assert.Contains(result.Warnings, w => w.Contains("mode is fly"));
        Assert.Contains(result.Warnings, w => w.Contains("partner points at an entity that isn't exported"));
        Assert.Contains(result.Warnings, w => w.Contains("1 of linked's entities isn't exported"));

        var unknown = nodes.First(n => n!["extras"]?["chip_class"]?.GetValue<string>() == "info_target")!["extras"]!;
        Assert.Null(unknown["chip_scene"]); // the addon makes it a Marker3D carrying its values
        Assert.Equal("x", unknown["properties"]!["note"]!.GetValue<string>());
    }

    [Fact]
    public void A_class_preview_draws_the_entity_and_sets_its_box()
    {
        var preview = Path.Combine(_dir, ".chip", "previews", "door.glb");
        Directory.CreateDirectory(Path.GetDirectoryName(preview)!);
        var model = new Document();
        model.Add(MeshOps.CreateBox(new Vector3(-0.5f, 0, -0.05f), new Vector3(0.5f, 2, 0.05f), "m", TextureMapping.Default));
        File.WriteAllBytes(preview, GlbExporter.Build(model, new MaterialLibrary()));
        ReferenceModels.ClearCache();

        var door = new Entity { ClassName = "door", Position = new Vector3(3, 0, 0) };
        var (loaded, error) = ReferenceModels.Get(door);
        Assert.Null(error);
        Assert.NotNull(loaded);
        var (min, max) = EntityOps.Box(door);
        Assert.Equal(2, max.Y - min.Y, 3);
        Assert.Null(ReferenceModels.Get(new Entity { ClassName = "enemies/grunt" }).Model); // no preview: a box
    }
}
