using System.Text.Json;
using System.Text.Json.Nodes;
using Chip.Core.Geometry;
using Chip.Core.IO;
using Chip.Core.Scene;
namespace Chip.Core.Tests;

[Collection("Global entity classes")]
public sealed class LogicTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "chip-logic-" + Guid.NewGuid());
    public LogicTests() => Directory.CreateDirectory(_root);
    public void Dispose() { EntityClasses.Reset(); Directory.Delete(_root, true); }
    const string Manifest = """
    {"classes":{"door":{"was":["old_door"],"traits":["lockable"],"props":[
      {"name":"partner","was":["friend"],"type":"entity","class":["door","light"],"trait":"lockable"},
      {"name":"links","type":"array","of":"entity","class":"door"},
      {"name":"sound","type":"asset","kind":"sound"}],
      "inputs":[{"name":"open","was":["unlock"],"params":[{"name":"amount","was":["count"],"type":"int","default":1}]}],
      "outputs":[{"name":"opened","was":["unlocked"],"payload":{"type":"int"},"timeline":true}]},
      "light":{"inputs":[{"name":"open","params":[{"name":"amount","type":"int"}]}]},
      "relay":{"logic":true,"outputs":[{"name":"fire","required":true}]}}}
    """;
    EntityCatalog Catalog => EntityCatalog.Parse(Manifest, null);
    GameProject Project()
    {
        File.WriteAllText(Path.Combine(_root, "entities.json"), Manifest);
        var json = """{"entities":"entities.json","kinds":{"sound":{"root":"sounds","ext":[".json"],"store":"stem","label":"name"}},"flags":{"power":{"type":"bool","default":false,"scope":"game"},"count":{"type":"int","default":0}}}""";
        File.WriteAllText(Path.Combine(_root, "chip.project.json"), json);
        return GameProject.Parse(json, Path.Combine(_root, "chip.project.json"));
    }
    static Entity Door(int id = 1) => new() { Id = id, ClassName = "door" };
    static Connection Wire(ConnectionTarget? target = null) => new() { On = "opened", Do = "open", Target = target ?? new(TargetKind.Entity, 2), Args = new() { ["amount"] = EntityValues.Of(2L) } };
    [Fact]
    public void Kinds_cache_and_invalidate_for_files_labels_and_folders()
    {
        var p = Project(); using var kind = p.Kinds["sound"];
        Assert.Empty(kind.List()); Directory.CreateDirectory(Path.Combine(_root, "sounds", "sub"));
        string file = Path.Combine(_root, "sounds", "sub", "a.json"); File.WriteAllText(file, "{\"name\":\"Alpha\"}");
        Assert.True(SpinWait.SpinUntil(() => kind.List().Count == 1, 3000));
        var list = kind.List(); Assert.Same(list, kind.List()); Assert.Equal("Alpha", list[0].Label);
        Assert.Equal("sub/a", kind.ValueFor(file)); Assert.True(kind.Exists("sub/a")); Assert.Null(kind.ValueFor(Path.Combine(_root, "entities.json")));
        File.WriteAllText(file, "{\"name\":\"Beta\"}"); Assert.True(SpinWait.SpinUntil(() => kind.List()[0].Label == "Beta", 3000));
        File.Delete(file); Assert.True(SpinWait.SpinUntil(() => kind.List().Count == 0, 3000));
        using var folders = new AssetKind { Name = "folders", Root = Path.Combine(_root, "sounds"), ProjectRoot = _root, Folders = true };
        Assert.Equal("sounds/sub", Assert.Single(folders.List()).Value); Assert.True(folders.Exists("sounds/sub"));
    }
    [Fact]
    public void Parses_restrictions_ports_and_payloads()
    {
        var c = Catalog.Find("door")!; var p = c.Prop("partner")!;
        Assert.Equal(new[] { "door", "light" }, p.Classes); Assert.Equal("door", p.Class); Assert.True(p.Accepts(c));
        Assert.True(p.Accepts(Catalog.Find("light")!)); Assert.False(p.Accepts(Catalog.Find("relay")!));
        Assert.True((p with { Classes = [] }).Accepts(c)); Assert.True((p with { Classes = [], Trait = null }).Accepts(Catalog.Find("relay")!));
        Assert.Equal("sound", c.Prop("sound")!.Kind); Assert.Equal("Open", c.Input("open")!.Label);
        Assert.Equal(PropType.Int, c.Output("opened")!.Payload!.Type); Assert.Equal("payload", c.Output("opened")!.Payload!.Name);
        Assert.True(c.Output("opened")!.Timeline); Assert.True(Catalog.Find("relay")!.IsLogic);
    }
    [Fact]
    public void Aliases_are_idempotent_and_preserve_current_values()
    {
        var a = Door(); a.ClassName = "old_door"; a.Properties["friend"] = EntityValues.Ref(2); a.Properties["partner"] = EntityValues.Ref(3);
        var b = Door(2); var c = Wire(); c.On = "unlocked"; c.Do = "unlock"; c.Args["count"] = EntityValues.Of(9L); a.Outputs.Add(c);
        var doc = new Document { Entities = [a, b] };
        Assert.Equal(5, LevelMigration.Apply(doc, Catalog)); Assert.Equal(0, LevelMigration.Apply(doc, Catalog));
        Assert.Equal(3, EntityValues.RefId(a.Properties["partner"])); Assert.False(a.Properties.ContainsKey("friend"));
        Assert.Equal(2, c.Args["amount"].GetInt32()); Assert.False(c.Args.ContainsKey("count")); Assert.Equal("open", c.Do);
    }
    [Theory]
    [InlineData("{\"entity\":2}")]
    [InlineData("{\"tag\":\"doors\"}")]
    [InlineData("{\"special\":\"self\"}")]
    [InlineData("{\"special\":\"caller\"}")]
    [InlineData("{\"special\":\"activator\"}")]
    [InlineData("{\"flag\":\"power\"}")]
    [InlineData("{\"future\":{\"id\":7}}")]
    [InlineData("{\"entity\":\"future\"}")]
    public void Connections_round_trip_including_unknown_targets(string target)
    {
        var json = JsonSerializer.SerializeToElement(JsonNode.Parse("""{"on":"opened","target":TARGET,"do":"open","args":{"amount":{"payload":true}},"delay":0.5,"once":true,"when":{"flag":"count","atLeast":2}}""".Replace("TARGET", target)));
        var e = Door(); e.Outputs.Add(Connection.FromJson(json)); e.Tags = TagSet.Of(["z", "a"]);
        var round = DocumentSerializer.Deserialize(DocumentSerializer.Serialize(new Document { Entities = [e] })).Entities.Single();
        Assert.True(JsonNode.DeepEquals(e.Outputs[0].ToJson(), round.Outputs[0].ToJson())); Assert.Equal(e.Tags, round.Tags);
        var clone = e.Clone(); clone.Outputs[0].Args.Clear(); Assert.NotEmpty(e.Outputs[0].Args);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(target), round.Outputs[0].ToJson()["target"]));
    }
    [Fact]
    public void Outputs_and_tags_undo_and_redo()
    {
        var s = new Session(); s.Reset(new Document { Entities = [Door()] }, null);
        s.Do("Wire", () => { s.Doc.Entities[0].Outputs.Add(Wire()); s.Doc.Entities[0].Tags = TagSet.Of(["doors"]); });
        Assert.True(s.Undo()); Assert.Empty(s.Doc.Entities[0].Outputs); Assert.True(s.Doc.Entities[0].Tags.IsEmpty);
        Assert.True(s.Redo()); Assert.Single(s.Doc.Entities[0].Outputs); Assert.True(s.Doc.Entities[0].Tags.Contains("doors"));
    }
    [Fact]
    public void Queries_resolve_tags_intersections_and_array_references()
    {
        var a = Door(); var b = Door(2); b.Tags = TagSet.Of(["doors"]); a.Outputs.Add(Wire()); a.Properties["links"] = EntityValues.List([EntityValues.Ref(2)]);
        var doc = new Document { Entities = [a, b] }; var tag = new ConnectionTarget(TargetKind.Tag, Name: "doors");
        Assert.Same(b, Assert.Single(LogicOps.TargetsOf(doc, a, tag))); Assert.Single(LogicOps.InputsFor(doc, Catalog, null, a, tag));
        Assert.Equal(2, LogicOps.ReferencesTo(doc, Catalog, 2).Count); Assert.Equal(2, LogicOps.ReferencesFrom(doc, Catalog, 1).Count);
        Assert.Empty(LogicOps.TargetsOf(doc, a, new(TargetKind.Special, Name: "caller")));
        Assert.Equal(new[] { "set", "toggle" }, LogicOps.InputsFor(doc, Catalog, Project(), a, new(TargetKind.Flag, Name: "power")).Select(i => i.Name));
    }
    [Theory]
    [InlineData("class")]
    [InlineData("missing_ref")]
    [InlineData("wrong_class")]
    [InlineData("missing_target")]
    [InlineData("on")]
    [InlineData("do")]
    [InlineData("arg_type")]
    [InlineData("payload")]
    [InlineData("arg_name")]
    [InlineData("flag")]
    [InlineData("guard")]
    [InlineData("add_bool")]
    [InlineData("atleast_bool")]
    [InlineData("toggle_int")]
    [InlineData("asset")]
    [InlineData("unused")]
    [InlineData("empty_tag")]
    [InlineData("required")]
    [InlineData("cycle")]
    public void Checks_each_contract_problem(string issue)
    {
        var a = Door(); var b = Door(2); var c = Wire(); var doc = new Document { Entities = [a, b] }; var project = Project(); string expected = ""; bool warning = false;
        switch (issue)
        {
            case "class": a.ClassName = "absent"; expected = "unknown class"; break;
            case "missing_ref": a.Properties["partner"] = EntityValues.Ref(99); expected = "missing entity"; break;
            case "wrong_class": b.ClassName = "relay"; a.Properties["partner"] = EntityValues.Ref(2); expected = "does not accept"; break;
            case "missing_target": c.Target = new(TargetKind.Entity, 99); expected = "missing entity"; break;
            case "on": c.On = "absent"; expected = "not an output"; break;
            case "do": c.Do = "absent"; expected = "not an input"; break;
            case "arg_type": c.Args["amount"] = EntityValues.Of("nonsense"); expected = "isn't a"; break;
            case "payload": c.Args["amount"] = JsonSerializer.SerializeToElement(new { payload = true }); c.On = "bad"; expected = "payload type"; break;
            case "arg_name": c.Args["absent"] = EntityValues.Of(true); expected = "no parameter"; break;
            case "flag": c.Target = new(TargetKind.Flag, Name: "absent"); expected = "undeclared flag"; break;
            case "guard": c.When = new("absent", EntityValues.Of(true)); expected = "undeclared flag"; break;
            case "add_bool": c.Target = new(TargetKind.Flag, Name: "power"); c.Do = "add"; expected = "not an input"; break;
            case "atleast_bool": c.When = new("power", AtLeast: 1); expected = "atLeast needs"; break;
            case "toggle_int": c.Target = new(TargetKind.Flag, Name: "count"); c.Do = "toggle"; expected = "not an input"; break;
            case "asset": a.Properties["sound"] = EntityValues.Of("missing"); expected = "does not exist"; break;
            case "unused": a.Properties["absent"] = EntityValues.Of(true); expected = "unused property"; warning = true; break;
            case "empty_tag": c.Target = new(TargetKind.Tag, Name: "absent"); expected = "no entity carries"; warning = true; break;
            case "required": a.ClassName = "relay"; expected = "required output"; warning = true; break;
            case "cycle": c.Target = new(TargetKind.Special, Name: "self"); expected = "cycle"; warning = true; break;
        }
        if (issue is not ("class" or "missing_ref" or "wrong_class" or "asset" or "unused" or "required")) a.Outputs.Add(c);
        Assert.Contains(LogicOps.Check(doc, Catalog, project), p => p.Message.Contains(expected) && p.Severity == (warning ? Severity.Warning : Severity.Error));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Check_command_exit_codes_and_json(int code)
    {
        if (code != 3) Project(); var e = Door(); if (code == 1) e.Properties["unused"] = EntityValues.Of(1L); if (code == 2) e.ClassName = "absent";
        DocumentSerializer.Save(new Document { Entities = [e] }, Path.Combine(_root, "level.chip")); var output = new StringWriter();
        Assert.Equal(code, LevelCheck.Run(_root, output, true)); if (code != 3) Assert.NotNull(JsonNode.Parse(output.ToString())!["levels"]);
    }
    [Fact]
    public void Migrate_rewrites_only_changed_levels()
    {
        Project(); var e = Door(); e.ClassName = "old_door"; string file = Path.Combine(_root, "level.chip"); DocumentSerializer.Save(new Document { Entities = [e] }, file);
        var output = new StringWriter(); Assert.Equal(0, LevelMigration.Run(file, output)); Assert.Contains("1 renames", output.ToString());
        string first = File.ReadAllText(file); output.GetStringBuilder().Clear(); Assert.Equal(0, LevelMigration.Run(_root, output)); Assert.Empty(output.ToString()); Assert.Equal(first, File.ReadAllText(file));
    }
    [Fact]
    public void Export_writes_io_tags_flags_and_warns_for_dropped_connections()
    {
        Project(); EntityClasses.Track(_root); var a = Door(); a.Tags = TagSet.Of(["doors"]); a.Outputs.Add(Wire()); var b = Door(2); b.Hidden = true;
        a.Outputs.Add(Wire(new(TargetKind.Special, Name: "self"))); var doc = new Document { Entities = [a, b] }; doc.Export.IncludeHidden = false; var result = GlbExporter.Build(doc, new MaterialLibrary(), null);
        Assert.Single(result.Warnings); var json = Glb.Json(result.Data); var extras = json["nodes"]![0]!["extras"]!;
        var io = Assert.Single(extras["chip_io"]!.AsArray())!; Assert.NotNull(io["args"]); Assert.Equal(0, io["delay"]!.GetValue<float>()); Assert.False(io["once"]!.GetValue<bool>());
        Assert.Equal("doors", extras["chip_tags"]![0]!.GetValue<string>()); Assert.NotNull(json["scenes"]![0]!["extras"]!["chip_flags"]!["power"]);
    }
    [Fact]
    public void Manifest_lints_port_types_alias_collisions_and_unknown_kinds()
    {
        string path = Path.Combine(_root, "entities.json");
        File.WriteAllText(path, """{"classes":{"door":{"was":["door"],"props":[{"name":"sound","type":"asset","kind":"absent"}],"inputs":[{"name":"open","params":[{"name":"amount","type":"unknown"}]}],"outputs":[{"name":"opened","payload":{"type":"enum"}}]}}}""");
        var output = new StringWriter(); Assert.Equal(1, EntityManifestCheck.Run(path, output));
        Assert.Contains("collides", output.ToString()); Assert.Contains("unknown kind", output.ToString()); Assert.Contains("unknown", output.ToString()); Assert.Contains("enum needs options", output.ToString());
        output.GetStringBuilder().Clear(); EntityManifestCheck.Run(path, output, true); var c = JsonNode.Parse(output.ToString())!["classes"]![0]!;
        Assert.NotNull(c["inputs"]); Assert.NotNull(c["outputs"]); Assert.NotNull(c["traits"]);
    }
    [Fact]
    public void Malformed_payload_is_a_manifest_error()
    {
        string path = Path.Combine(_root, "entities.json");
        File.WriteAllText(path, """{"classes":{"door":{"outputs":[{"name":"opened","payload":true}]}}}""");
        Assert.Equal(2, EntityManifestCheck.Run(path, new StringWriter()));
    }
    [Fact]
    public void Old_levels_do_not_gain_logic_keys_when_saved()
    {
        var doc = DocumentSerializer.Deserialize("""{"version":3,"meshes":[],"entities":[{"id":1,"className":"door"}]}""");
        var json = JsonNode.Parse(DocumentSerializer.Serialize(doc))!;
        Assert.Null(json["entities"]![0]!["outputs"]); Assert.Null(json["entities"]![0]!["tags"]);
        var c = Wire(); c.Args.Clear(); c.Delay = 0; c.Once = false; var saved = c.ToJson();
        Assert.Null(saved["args"]); Assert.Null(saved["delay"]); Assert.Null(saved["once"]); Assert.Null(saved["when"]);
    }

    [Fact]
    public void Duplicating_entities_points_their_connections_at_the_copies_and_adding_drops_outside_targets()
    {
        var doc = new Document();
        var a = doc.Add(new Entity { ClassName = "door" });
        var b = doc.Add(new Entity { ClassName = "door" });
        var outside = doc.Add(new Entity { ClassName = "light" });
        a.Outputs.Add(new Connection { On = "opened", Target = new(TargetKind.Entity, b.Id), Do = "open", Args = { ["who"] = EntityValues.Ref(b.Id) } });
        a.Outputs.Add(new Connection { On = "opened", Target = new(TargetKind.Entity, outside.Id), Do = "open" });
        a.Outputs.Add(new Connection { On = "opened", Target = new(TargetKind.Special, Name: "self"), Do = "open" });

        var copies = SceneOps.Duplicate(doc, [a.Id, b.Id]);
        var a2 = doc.FindEntity(copies[0])!;
        Assert.Equal(copies[1], a2.Outputs[0].Target.Entity); // the copied pair stays a pair
        Assert.Equal(copies[1], EntityValues.RefId(a2.Outputs[0].Args["who"]));
        Assert.Equal(outside.Id, a2.Outputs[1].Target.Entity); // what wasn't copied is still the target
        Assert.Equal(b.Id, a.Outputs[0].Target.Entity); // the original is untouched

        // Pasted from elsewhere: ids outside the batch would name something else here, so those connections go.
        var pasted = new Entity { Id = 1, ClassName = "door" };
        pasted.Outputs.Add(new Connection { On = "opened", Target = new(TargetKind.Entity, 2), Do = "open" });
        pasted.Outputs.Add(new Connection { On = "opened", Target = new(TargetKind.Entity, 99), Do = "open" });
        var partner = new Entity { Id = 2, ClassName = "door" };
        SceneOps.AddEntities(doc, [pasted, partner]);
        Assert.Equal(partner.Id, Assert.Single(pasted.Outputs).Target.Entity);
    }
}
