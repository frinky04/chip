using System.Numerics;
using System.Text.Json.Nodes;
using Chip.Core.Dressing;
using Chip.Core.Geometry;
using Chip.Core.Scene;
using static Chip.Core.Tests.ClipTests;

namespace Chip.Core.Tests;

/// <summary>
/// Editing rules from the editor: the canonical writer, level patches over styles, recipes, the "where" presets, undoable
/// file writes, rename and delete carrying per-mesh data along, Dress with, and "why isn't this dressed?".
/// </summary>
public class DressingEditingTests
{
    const string Level = "levels/room.dressing.json";
    const string Style = "styles/industrial.dressing.json";

    const string StyleText = """
        {
          // Shared look
          "chipDressing": 1,
          "name": "Industrial",
          "vars": { "trim": "metal_trim", "baseHeight": 0.12 },
          "rules": {
            "baseboard": { "kind": "sweep", "faces": { "tags": ["wall"] }, "profile": { "rect": [0.015, "$baseHeight"] }, "material": "$trim" },
            "debris": { "kind": "scatter", "faces": { "facing": "up" }, "props": ["props/rubble.glb"], "perM2": 0.3 },
          }
        }
        """;

    const string LevelText = """
        {
          "chipDressing": 1,
          "imports": ["../styles/industrial.dressing.json"],
          "rules": {
            "debris": { "perM2": 0.8 },
            "pipes": { "kind": "sweep", "faces": { "tags": ["ceiling"] }, "edges": "concave", "profile": { "round": 0.1 } }
          }
        }
        """;

    static MemoryFiles Files() => new() { [Style] = StyleText, [Level] = LevelText };

    static RuleSet Load(MemoryFiles files) => RuleSet.Load(files, Level);

    /// <summary>Applies a workspace's changes to the files, as the editor does (minus undo).</summary>
    static void Apply(RulesWorkspace ws, MemoryFiles files)
    {
        foreach (var (path, text) in ws.Changes()) files[path] = text;
    }

    // ---- Writer ----

    [Fact]
    public void Rewriting_a_file_keeps_the_rules_and_puts_keys_in_canonical_order()
    {
        var files = Files();
        var before = Load(files);
        var doc = RulesDocument.Parse(Level, files[Level]!, out var error)!;
        Assert.Null(error);
        var text = doc.Write();
        Assert.True(text.IndexOf("\"chipDressing\"") < text.IndexOf("\"imports\"") && text.IndexOf("\"imports\"") < text.IndexOf("\"rules\""));
        Assert.Contains("\"faces\": { \"tags\": [\"ceiling\"] }", text); // short values on one line
        Assert.True(text.IndexOf("\"kind\": \"sweep\"") < text.IndexOf("\"faces\": { \"tags\": [\"ceiling\"] }"));

        files[Level] = text;
        var after = Load(files);
        Assert.Empty(after.Diagnostics);
        Assert.Equal(before.Rules.Select(r => (r.Id, r.Fingerprint)), after.Rules.Select(r => (r.Id, r.Fingerprint)));

        var style = RulesDocument.Parse(Style, StyleText, out _)!;
        Assert.DoesNotContain("//", style.Write()); // comments go (notes stay)
        Assert.Equal(style.Write(), RulesDocument.Parse(Style, style.Write(), out _)!.Write()); // stable
    }

    [Fact]
    public void Long_values_wrap_one_item_per_line()
    {
        var doc = RulesDocument.New(Level, null);
        var points = new JsonArray(Enumerable.Range(0, 12).Select(i => (JsonNode)new JsonArray(i * 0.01, i * 0.02)).ToArray());
        doc.SetRule("odd", new JsonObject { ["kind"] = "sweep", ["faces"] = new JsonObject { ["tags"] = new JsonArray("wall") }, ["profile"] = new JsonObject { ["points"] = points } });
        var text = doc.Write();
        Assert.Contains("\n          [0.01, 0.02],\n", text);
        Assert.Empty(RuleSet.Load(new MemoryFiles { [Level] = text }, Level).Diagnostics);
    }

    // ---- Layers ----

    [Fact]
    public void The_workspace_lists_imported_and_local_rules_with_what_the_level_changes()
    {
        var ws = new RulesWorkspace(Files(), Level);
        Assert.Equal(["baseboard", "debris", "pipes"], ws.Rules.Select(r => r.Id));
        var debris = ws.Find("debris")!;
        Assert.False(debris.Local);
        Assert.Equal(Style, debris.DefinedIn);
        Assert.Equal(["perM2"], debris.LevelKeys);
        Assert.Equal(0.8, ParamJson.Number(debris.Raw["perM2"]));
        Assert.Equal(0.3, ParamJson.Number(debris.Base["perM2"]));
        Assert.True(ws.Find("pipes")!.Local);
        Assert.Equal("metal_trim", ParamJson.Text(ws.Resolve(ws.Find("baseboard")!.Raw["material"])));
        Assert.Equal("trim", RulesWorkspace.VarName(ws.Find("baseboard")!.Raw["material"]));
    }

    [Fact]
    public void Changing_a_style_rule_writes_a_patch_of_only_what_differs_and_reset_removes_it()
    {
        var files = Files();
        var ws = new RulesWorkspace(files, Level);
        Assert.True(ws.Set("baseboard", "material", JsonValue.Create("wood")));
        Assert.True(ws.Set("debris", "perM2", ParamJson.Num(0.3))); // back to the style's value: the patch key goes
        Apply(ws, files);
        Assert.Equal(StyleText, files[Style]); // the style is untouched

        ws = new RulesWorkspace(files, Level);
        Assert.Equal(["material"], ws.Find("baseboard")!.LevelKeys);
        Assert.Empty(ws.Find("debris")!.LevelKeys);
        Assert.Null(ws.Main!.Rule("debris")); // an emptied patch is removed
        var rules = Load(files);
        Assert.Empty(rules.Diagnostics);
        Assert.Equal("wood", rules.Rules.First(r => r.Id == "baseboard").Text("material"));

        ws.Reset("baseboard");
        Apply(ws, files);
        Assert.Equal("metal_trim", Load(files).Rules.First(r => r.Id == "baseboard").Text("material"));
    }

    [Fact]
    public void Style_mode_edits_the_style_file_and_switching_off_patches_the_level()
    {
        var files = Files();
        var ws = new RulesWorkspace(files, Level);
        ws.Set("baseboard", "material", JsonValue.Create("steel"), style: true);
        ws.SetEnabled("debris", false);
        Apply(ws, files);
        Assert.Contains("steel", files[Style]);
        var rules = Load(files);
        Assert.Empty(rules.Diagnostics);
        Assert.False(rules.Rules.First(r => r.Id == "debris").Enabled);
        Assert.Equal("steel", rules.Rules.First(r => r.Id == "baseboard").Text("material"));

        ws = new RulesWorkspace(files, Level);
        ws.SetEnabled("debris", true);
        Apply(ws, files);
        Assert.True(Load(files).Rules.First(r => r.Id == "debris").Enabled);
        Assert.Equal(["perM2"], new RulesWorkspace(files, Level).Find("debris")!.LevelKeys); // no leftover enabled key
    }

    [Fact]
    public void Style_rules_only_rename_and_delete_in_style_mode()
    {
        var files = Files();
        var ws = new RulesWorkspace(files, Level);
        Assert.False(ws.Delete("debris"));
        Assert.False(ws.Rename("debris", "rubble"));
        Assert.True(ws.Rename("debris", "rubble", style: true)); // the level's patch follows
        Assert.True(ws.Rename("pipes", "ceiling-pipes"));
        Assert.False(ws.Rename("baseboard", "Bad Name"));
        Apply(ws, files);
        var rules = Load(files);
        Assert.Empty(rules.Diagnostics);
        Assert.Equal(["baseboard", "rubble", "ceiling-pipes"], rules.Rules.Select(r => r.Id));
        Assert.Equal(0.8f, rules.Rules.First(r => r.Id == "rubble").Number("perM2"));

        ws = new RulesWorkspace(files, Level);
        Assert.True(ws.Delete("rubble", style: true));
        Assert.True(ws.Delete("ceiling-pipes"));
        Apply(ws, files);
        Assert.Equal(["baseboard"], Load(files).Rules.Select(r => r.Id));
    }

    [Fact]
    public void Duplicating_a_style_rule_defines_a_local_copy_with_prop_paths_rebased()
    {
        var files = Files();
        var ws = new RulesWorkspace(files, Level);
        Assert.True(ws.Duplicate("debris", ws.UniqueId("debris")));
        Apply(ws, files);
        ws = new RulesWorkspace(files, Level);
        var copy = ws.Find("debris-2")!;
        Assert.True(copy.Local);
        Assert.Equal(0.8, ParamJson.Number(copy.Raw["perM2"]));
        Assert.Equal("../styles/props/rubble.glb", ParamJson.Names(copy.Raw["props"]).Single());
    }

    [Fact]
    public void Save_as_style_moves_the_levels_rules_into_a_new_imported_file()
    {
        var files = new MemoryFiles { [Level] = DressingSchema.Starter("room") };
        var before = Load(files);
        var ws = new RulesWorkspace(files, Level);
        Assert.True(ws.SaveAsStyle("styles/house.dressing.json", "House"));
        Apply(ws, files);
        Assert.Contains("\"name\": \"House\"", files["styles/house.dressing.json"]);
        Assert.Equal(["../styles/house.dressing.json"], RulesDocument.Parse(Level, files[Level]!, out _)!.Imports);
        var after = Load(files);
        Assert.Empty(after.Diagnostics);
        Assert.Equal(before.Rules.Select(r => (r.Id, r.Enabled, r.Fingerprint)), after.Rules.Select(r => (r.Id, r.Enabled, r.Fingerprint)));
        Assert.False(new RulesWorkspace(files, Level).SaveAsStyle("styles/house.dressing.json", "Again")); // never overwrites
    }

    [Fact]
    public void A_missing_file_is_created_by_the_first_edit_and_a_broken_one_cant_be_edited()
    {
        var files = new MemoryFiles();
        var ws = new RulesWorkspace(files, Level, "room");
        Assert.False(ws.Exists);
        ws.Add("baseboard", Recipes.Find("baseboard")!.Rule());
        Apply(ws, files);
        Assert.Empty(Load(files).Diagnostics);
        Assert.Contains("room dressing", files[Level]);

        files[Level] = "{ \"rules\": ";
        ws = new RulesWorkspace(files, Level);
        Assert.False(ws.CanEdit);
        Assert.Contains("isn't valid JSON", ws.MainError);
        Assert.False(ws.Set("baseboard", "material", JsonValue.Create("x")));
    }

    // ---- Recipes and presets ----

    [Fact]
    public void Every_recipe_loads_clean_and_selects_by_its_tag()
    {
        var doc = RulesDocument.New(Level, null);
        foreach (var r in Recipes.All) doc.SetRule(r.Id, r.Rule());
        var files = new MemoryFiles { [Level] = doc.Write() };
        var rules = Load(files);
        Assert.Empty(rules.Diagnostics);
        Assert.Equal(Recipes.All.Select(r => r.Id), rules.Rules.Select(r => r.Id));
        foreach (var r in Recipes.All)
            Assert.Equal((r.Tag, r.MeshTag), ParamJson.PrimaryTag(r.Rule(), n => n));

        // The starter is made of recipes too, and dresses the room.
        Assert.Equal(4, new Dresser(new MemoryFiles { [Level] = DressingSchema.Starter("room") }).Dress(DressingTests.Room(), Level).Stats[0].Outputs);
    }

    [Fact]
    public void New_profile_shapes_load_and_have_the_right_size()
    {
        foreach (var (json, size) in new[] { ("{ \"wedge\": [0.08, 0.05] }", new Vector2(0.08f, 0.05f)), ("{ \"quarter\": 0.04 }", new Vector2(0.04f)), ("{ \"cove\": 0.06 }", new Vector2(0.06f)) })
        {
            var files = new MemoryFiles { [Level] = $$"""{ "chipDressing": 1, "rules": { "t": { "kind": "sweep", "faces": { "tags": ["wall"] }, "profile": {{json}} } } }""" };
            var rules = Load(files);
            Assert.Empty(rules.Diagnostics);
            var p = rules.Rules[0].Get<Profile>("profile");
            Assert.Equal(size.X, p.Points.Max(q => q.X), 4);
            Assert.Equal(size.Y, p.Points.Max(q => q.Y), 4);
            Assert.Equal(0, p.Points.Min(q => MathF.Min(q.X, q.Y)), 4);
            Assert.True(new Dresser(files).Dress(DressingTests.Room(), Level).Stats[0].Outputs == 4, json);
        }
    }

    [Fact]
    public void Where_presets_read_and_write_edges_and_onto()
    {
        Assert.Equal("floor", EdgePlaces.Of(null, null)!.Id);
        Assert.Equal("floor", EdgePlaces.Of(JsonValue.Create("contact"), new JsonObject { ["facing"] = "up" })!.Id);
        Assert.Equal("ceiling", EdgePlaces.Of(null, new JsonObject { ["facing"] = "down" })!.Id);
        Assert.Equal("inside", EdgePlaces.Of(JsonValue.Create("concave"), new JsonObject { ["facing"] = "down" })!.Id);
        Assert.Null(EdgePlaces.Of(null, new JsonObject { ["tags"] = new JsonArray("plinth") })); // custom
        foreach (var place in EdgePlaces.All)
        {
            var (edges, onto) = EdgePlaces.Values(place);
            Assert.Same(place, EdgePlaces.Of(edges, onto));
        }
    }

    [Fact]
    public void Defaults_as_json_match_what_the_loader_uses()
    {
        var files = new MemoryFiles();
        foreach (var kind in RuleKind.All)
            foreach (var spec in kind.Params.Where(p => p.Default != null))
            {
                var json = ParamJson.Default(spec);
                Assert.NotNull(json);
                var recipe = Recipes.All.First(r => r.Kind == kind).Rule();
                recipe[spec.Name] = json;
                files[Level] = new JsonObject { ["chipDressing"] = 1, ["rules"] = new JsonObject { ["r"] = recipe } }.ToJsonString();
                Assert.Empty(Load(files).Diagnostics);
            }
    }

    // ---- Undo, rename, dress with, explain ----

    [Fact]
    public void Files_written_in_a_step_undo_and_redo_with_it()
    {
        var files = Files();
        var session = new Session { Files = files };
        session.Reset(DressingTests.Room(), "levels/room.chip");
        var changed = new List<string>();
        session.FilesChanged += paths => changed.AddRange(paths);
        var ws = new RulesWorkspace(files, Level);
        ws.Set("pipes", "edges", JsonValue.Create("convex"));
        session.Do("Dressing: pipes edges", [], () => { foreach (var (p, t) in ws.Changes()) session.WriteFile(p, t); });
        Assert.Equal([Level], changed);
        Assert.Contains("convex", files[Level]);
        Assert.True(session.Undo());
        Assert.Equal(LevelText, files[Level]);
        Assert.True(session.Redo());
        Assert.Contains("convex", files[Level]);

        // A failed step puts the files back.
        Assert.Throws<InvalidOperationException>(() => session.Do("Broken", () => { session.WriteFile(Level, "{}"); throw new InvalidOperationException(); }));
        Assert.Contains("convex", files[Level]);
        Assert.Throws<InvalidOperationException>(() => session.WriteFile(Level, "{}")); // only inside a step
    }

    [Fact]
    public void Renaming_a_rule_moves_overrides_switches_and_skip_tags()
    {
        var doc = DressingTests.Room();
        var west = doc.Meshes.First(m => m.Name == "west");
        west.Dressing = west.Dressing.WithParam("baseboard", "material", JsonValue.Create("x")).WithSkip("other", true);
        west.Faces[0].Tags = west.Faces[0].Tags.Add("skip:baseboard");
        Assert.Equal([west.Id], DressingOps.Mentioning(doc, "baseboard"));
        Assert.Equal(1, DressingOps.RenameRule(doc, "baseboard", "trim"));
        Assert.NotNull(west.Dressing.OverridesFor("trim"));
        Assert.Null(west.Dressing.OverridesFor("baseboard"));
        Assert.True(west.Faces[0].Tags.Contains("skip:trim"));
        Assert.True(west.Dressing.Skips("other"));
        Assert.Equal(1, DressingOps.RenameRule(doc, "trim", null)); // deleting drops them
        Assert.False(west.Dressing.Mentions("trim"));
        Assert.False(west.Faces[0].Tags.Contains("skip:trim"));
    }

    [Fact]
    public void Dress_with_tags_selected_faces_or_the_matching_faces_of_selected_meshes()
    {
        var doc = new Document();
        var box = doc.Add(Box(Vector3.Zero, new Vector3(1, 2, 1)));
        var sel = new Selection();
        SelectionOps.Replace(sel, SelectMode.Object, [ElementRef.Object(box.Id)], null);
        var upright = new FaceSelector(["wall"], [], [], "upright");
        Assert.Equal((0, 4), DressingOps.DressCoverage(doc, sel, "wall", false, upright));
        Assert.Equal(4, DressingOps.Dress(doc, sel, "wall", false, upright, on: true)); // the four sides, not top and bottom
        Assert.Equal(4, box.Faces.Count(f => f.Tags.Contains("wall")));
        Assert.Equal(1, DressingOps.Dress(doc, sel, "railing", true, FaceSelector.Any, on: true));
        Assert.True(box.Tags.Contains("railing"));

        SelectionOps.Replace(sel, SelectMode.Face, [ElementRef.Face(box.Id, 0)], null);
        Assert.Equal(1, DressingOps.Dress(doc, sel, "frame", false, FaceSelector.Any, on: true));
        Assert.Equal(1, box.Faces.Count(f => f.Tags.Contains("frame")));
    }

    [Fact]
    public void Explain_says_why_a_face_isnt_dressed_and_targets_lists_what_a_rule_takes()
    {
        var doc = DressingTests.Room();
        var floor = doc.Meshes.First(m => m.Name == "floor");
        var files = new MemoryFiles { [Level] = """
            { "chipDressing": 1, "rules": {
              "baseboard": { "kind": "sweep", "faces": { "tags": ["wall"], "facing": "upright" } },
              "rail": { "kind": "railing", "meshes": { "tags": ["railing"] } },
              "off": { "kind": "sweep", "enabled": false, "faces": { "tags": ["wall"] } } } }
            """ };
        var level = new Dresser(files).Dress(doc, Level);
        int top = Enumerable.Range(0, floor.Faces.Count).First(f => floor.FaceNormal(f).Y > 0.9f);
        floor.Faces[top].Tags = TagSet.Of("walls");
        var why = level.Explain(floor, top).ToDictionary(e => e.Rule.Id, e => e.Reason);
        Assert.Equal("needs the tag wall (it has walls)", why["baseboard"]);
        Assert.Equal("needs the mesh tag railing", why["rail"]);
        Assert.Equal("switched off in the rules", why["off"]);

        floor.Faces[top].Tags = TagSet.Of("wall");
        Assert.Equal("needs faces facing upright (it faces up)", level.Explain(floor, top).First().Reason);

        var west = doc.Meshes.First(m => m.Name == "west");
        Assert.Null(level.Explain(west, -1).First().Reason); // some face of the mesh is taken
        Assert.Equal(4, new Dresser(files).Dress(DressingTests.Room(), Level).Targets("baseboard").Count);
    }
}
