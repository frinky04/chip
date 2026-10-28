using System.Numerics;
using Chip.Core.Scene;
using Chip.Editor.Features.Logic;

namespace Chip.Editor.Tests;

/// <summary>The rows behind the Entity IO tables and the Wiring window: what's outgoing, what's incoming, their text and problems.</summary>
[Collection("Editor global entity classes")]
public sealed class LogicIoTableTests : IDisposable
{
    const string Manifest = """
    { "version": 1, "classes": {
      "button": { "outputs": [ { "name": "pressed", "payload": { "type": "entity" } } ] },
      "light": { "inputs": [ { "name": "turn_off" }, { "name": "dim", "params": [ { "name": "to", "type": "float", "default": 0.5 } ] } ] },
      "door": { "inputs": [ { "name": "open" }, { "name": "look_at", "params": [ { "name": "who", "type": "entity" } ] } ] }
    } }
    """;

    public LogicIoTableTests() => EntityClasses.Use(EntityCatalog.Parse(Manifest, null));
    public void Dispose() => EntityClasses.Reset();

    static (Framework.EditorContext Ctx, Entity Button, Entity Lamp, Entity Door) Level()
    {
        var doc = new Document();
        var button = doc.Add(EntityClasses.Create("button", Vector3.Zero));
        var lamp = doc.Add(EntityClasses.Create("light", new Vector3(2, 0, 0)));
        var door = doc.Add(EntityClasses.Create("door", new Vector3(4, 0, 0)));
        lamp.Tags = lamp.Tags.Add("hall");
        button.Name = "button";
        lamp.Name = "lamp";
        door.Name = "door";
        button.Outputs.Add(new Connection { On = "pressed", Target = new(TargetKind.Tag, Name: "hall"), Do = "dim", Args = { ["to"] = EntityValues.Of(0.2) }, Delay = 0.5f });
        button.Outputs.Add(new Connection { On = "pressed", Target = new(TargetKind.Entity, door.Id), Do = "look_at", Args = { ["who"] = Payload() }, Once = true });
        button.Outputs.Add(new Connection { On = "pressed", Target = new(TargetKind.Entity, door.Id), Do = "explode" });
        return (RegistryTests.Headless(doc), button, lamp, door);
    }

    static System.Text.Json.JsonElement Payload() => System.Text.Json.JsonSerializer.SerializeToElement(new Dictionary<string, bool> { ["payload"] = true });

    [Fact]
    public void Outgoing_lists_the_entitys_connections_in_order_and_incoming_follows_ids_and_tags()
    {
        var (ctx, button, lamp, door) = Level();
        Assert.Equal([0, 1, 2], IoRows.Outgoing(ctx, button.Id).Select(r => r.Index));
        Assert.Equal([0], IoRows.Incoming(ctx, lamp.Id).Select(r => r.Index)); // through its tag
        Assert.Equal([1, 2], IoRows.Incoming(ctx, door.Id).Select(r => r.Index));
        Assert.Empty(IoRows.Incoming(ctx, button.Id));
        Assert.Equal(3, IoRows.All(ctx).Count);
    }

    [Fact]
    public void Rows_read_like_a_table()
    {
        var (ctx, button, _, _) = Level();
        var rows = IoRows.Outgoing(ctx, button.Id);
        Assert.Equal(("button", "Pressed", "#hall", "Dim", "0.2", "0.5 s", ""), (rows[0].SourceText, rows[0].Output, rows[0].Target, rows[0].Input, rows[0].Value, rows[0].Delay, rows[0].Limits));
        Assert.Equal(("door", "Look at", "← value", "once"), (rows[1].Target, rows[1].Input, rows[1].Value, rows[1].Limits));
    }

    [Fact]
    public void A_connection_that_cant_work_is_marked_with_why()
    {
        var (ctx, button, _, _) = Level();
        var rows = IoRows.Outgoing(ctx, button.Id);
        Assert.False(rows[0].HasError);
        Assert.False(rows[1].HasError);
        Assert.True(rows[2].HasError);
        Assert.Equal("explode is not an input of the target", rows[2].ProblemText);
    }

    [Fact]
    public void Filtering_matches_any_column()
    {
        var (ctx, _, _, _) = Level();
        Assert.Equal(1, IoRows.All(ctx).Count(r => r.Matches("hall")));
        Assert.Equal(2, IoRows.All(ctx).Count(r => r.Matches("DOOR")));
        Assert.Equal(3, IoRows.All(ctx).Count(r => r.Matches("")));
    }

    [Fact]
    public void Rows_follow_edits_and_undo()
    {
        var (ctx, button, lamp, _) = Level();
        Features.Scene.EntitySection.Edit(ctx, button.Id, "Remove", x => { x.Outputs.RemoveAt(0); return null; });
        Assert.Empty(IoRows.Incoming(ctx, lamp.Id));
        Assert.Equal(2, IoRows.Outgoing(ctx, button.Id).Count);
        ctx.Commands.Execute("edit.undo");
        Assert.Single(IoRows.Incoming(ctx, lamp.Id));
    }

    [Fact]
    public void Jumping_to_a_connection_selects_its_source_and_hands_the_index_over_once()
    {
        var (ctx, button, lamp, _) = Level();
        ctx.Sel.Add(ElementRef.Object(lamp.Id));
        IoFocus.Show(ctx, button, 1);
        Assert.Equal([ElementRef.Object(button.Id)], ctx.Sel.Items);
        Assert.Null(IoFocus.Take(lamp.Id));
        Assert.Equal(1, IoFocus.Take(button.Id));
        Assert.Null(IoFocus.Take(button.Id));
    }

    [Fact]
    public void The_wiring_command_and_the_io_section_register()
    {
        var (ctx, _, _, _) = Level();
        Assert.Equal("Ctrl+Shift+I", ctx.Commands.Find("logic.wiring")!.HotkeyText);
        Assert.Contains(ctx.Sections, s => s.Id == "io");
    }
}
