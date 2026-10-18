using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using Chip.Editor.Features.Scene;

namespace Chip.Editor.Tests;

/// <summary>Entity tool, entity inspector edits, groups, lock and the built-in edit commands on entities, on a headless context.</summary>
public class EntityCommandTests
{
    static Document Level(out PolyMesh box, out Entity light)
    {
        var doc = new Document();
        box = doc.Add(MeshOps.CreateBox(Vector3.Zero, new Vector3(2, 2, 2), "dev_a", TextureMapping.Default));
        light = doc.Add(EntityClasses.Create("light_point", new Vector3(1, 4, 1)));
        return doc;
    }

    static void Select(Framework.EditorContext ctx, params int[] ids)
    {
        ctx.Sel.Clear();
        foreach (var id in ids) ctx.Sel.Add(ElementRef.Object(id));
    }

    [Fact]
    public void Scene_commands_and_the_entity_tool_register_with_their_hotkeys()
    {
        var ctx = RegistryTests.Headless();
        Assert.Equal("Shift+E", ctx.Commands.Find("tool.entity")!.HotkeyText);
        Assert.Equal("Ctrl+Shift+O", ctx.Commands.Find("scene.outliner")!.HotkeyText);
        Assert.Equal("Ctrl+G", ctx.Commands.Find("scene.group")!.HotkeyText);
        Assert.Equal("Ctrl+Alt+G", ctx.Commands.Find("scene.ungroup")!.HotkeyText);
        Assert.Equal("Ctrl+L", ctx.Commands.Find("scene.lock")!.HotkeyText);
        Assert.Equal("Ctrl+Shift+L", ctx.Commands.Find("scene.unlockAll")!.HotkeyText);
        Assert.Contains(ctx.Sections, s => s.Id == "entity");
    }

    [Fact]
    public void Entity_tool_places_and_selects_in_one_undo_step()
    {
        var ctx = RegistryTests.Headless(Level(out var box, out _));
        ctx.SetMode(SelectMode.Face);
        var tool = ctx.Tools.OfType<EntityTool>().Single();
        tool.ClassName = "info_player_start";
        tool.Yaw = 90;
        tool.Place(ctx, new Vector3(0, 2, 0));

        var e = ctx.Doc.Entities.Last();
        Assert.Equal("info_player_start", e.ClassName);
        Assert.Equal(new Vector3(0, 90, 0), e.Rotation);
        Assert.Equal(SelectMode.Object, ctx.Sel.Mode);
        Assert.Equal([ElementRef.Object(e.Id)], ctx.Sel.Items);
        Assert.Equal("Place Player start", ctx.Session.UndoLabel);
        ctx.Commands.Execute("edit.undo");
        Assert.Single(ctx.Doc.Entities);
    }

    [Fact]
    public void Inspector_edits_are_one_undo_step_each_and_bad_keys_change_nothing()
    {
        var ctx = RegistryTests.Headless(Level(out _, out var light));
        int id = light.Id;
        Assert.True(EntitySection.Edit(ctx, id, "Edit Property", e => EntityOps.SetProperty(e, "range", "25")));
        Assert.True(EntitySection.Edit(ctx, id, "Rename Property", e => EntityOps.RenameProperty(e, "range", "radius")));
        var live = ctx.Doc.FindEntity(id)!;
        Assert.Equal("25", live.Properties["radius"]);
        Assert.False(live.Properties.ContainsKey("range"));

        int version = ctx.Session.Version;
        Assert.False(EntitySection.Edit(ctx, id, "Rename Property", e => EntityOps.RenameProperty(e, "radius", "color")));
        Assert.True(ctx.StatusIsError);
        Assert.Equal("Rename Property", ctx.Session.UndoLabel); // the refused edit left no step
        Assert.Equal("25", ctx.Doc.FindEntity(id)!.Properties["radius"]);

        ctx.Commands.Execute("edit.undo");
        Assert.Equal("25", ctx.Doc.FindEntity(id)!.Properties["range"]);
        ctx.Commands.Execute("edit.undo");
        Assert.Equal("10", ctx.Doc.FindEntity(id)!.Properties["range"]);
    }

    [Fact]
    public void Changing_class_adds_the_new_defaults_and_keeps_values()
    {
        var ctx = RegistryTests.Headless(Level(out _, out var light));
        EntitySection.Edit(ctx, light.Id, "Change Entity Class", e => { EntityOps.SetClass(e, "light_spot"); return null; });
        var live = ctx.Doc.FindEntity(light.Id)!;
        Assert.Equal("light_spot", live.ClassName);
        Assert.Equal("45", live.Properties["angle"]);
        Assert.Equal("10", live.Properties["range"]);
    }

    [Fact]
    public void Delete_duplicate_hide_and_nudge_handle_entities()
    {
        var ctx = RegistryTests.Headless(Level(out var box, out var light));
        Select(ctx, light.Id);
        Assert.Equal(new Vector3(1, 4, 1), ctx.SelectionPivot()); // the gizmo sits on the entity's origin

        ctx.Commands.Execute("edit.duplicate");
        Assert.Equal(2, ctx.Doc.Entities.Count);
        var copy = ctx.Doc.Entities[1];
        Assert.Equal([ElementRef.Object(copy.Id)], ctx.Sel.Items);

        ctx.Commands.Execute("edit.nudgeUp");
        Assert.Equal(4 + ctx.Grid, ctx.Doc.FindEntity(copy.Id)!.Position.Y, 4);

        ctx.Commands.Execute("edit.delete");
        Assert.Single(ctx.Doc.Entities);

        Select(ctx, light.Id, box.Id);
        ctx.Commands.Execute("edit.hide");
        Assert.True(ctx.Doc.FindEntity(light.Id)!.Hidden);
        Assert.True(ctx.Commands.Find("edit.unhideAll")!.Enabled);
        ctx.Commands.Execute("edit.unhideAll");
        Assert.False(ctx.Doc.FindEntity(light.Id)!.Hidden);
    }

    [Fact]
    public void Group_lock_and_unlock_commands()
    {
        var ctx = RegistryTests.Headless(Level(out var box, out var light));
        Select(ctx, box.Id, light.Id);
        Assert.True(ctx.Commands.Execute("scene.group"));
        var g = Assert.Single(ctx.Doc.Groups);
        Assert.Equal(new[] { box.Id, light.Id }.Order(), g.Members.Order());

        Assert.True(ctx.Commands.Execute("scene.lock"));
        Assert.True(ctx.Doc.Find(box.Id)!.Locked && ctx.Doc.FindEntity(light.Id)!.Locked);
        Assert.True(ctx.Sel.IsEmpty);
        Assert.True(ctx.Commands.Execute("scene.unlockAll"));
        Assert.False(SceneOps.AnyLocked(ctx.Doc));

        Select(ctx, box.Id, light.Id);
        Assert.True(ctx.Commands.Execute("scene.ungroup"));
        Assert.Empty(ctx.Doc.Groups);
        ctx.Commands.Execute("edit.undo");
        ctx.Commands.Execute("edit.undo");
        ctx.Commands.Execute("edit.undo");
        Assert.Single(ctx.Doc.Groups); // undo walks back through unlock, lock and ungroup
    }

    [Fact]
    public void Selecting_a_grouped_object_selects_its_group_once_per_change()
    {
        var doc = Level(out var box, out var light);
        var other = doc.Add(MeshOps.CreateBox(new Vector3(5, 0, 0), new Vector3(6, 1, 1), "dev_a", TextureMapping.Default));
        GroupOps.Create(doc, [box.Id, light.Id]);
        var ctx = RegistryTests.Headless(doc);
        var groups = GroupSelection.Of(ctx);
        groups.Update(ctx); // first frame: takes the current (empty) selection as seen

        ctx.Sel.Add(ElementRef.Object(box.Id)); // a click
        groups.Update(ctx);
        Assert.Equal(new[] { box.Id, light.Id }.Order(), ctx.Sel.MeshIds().Order());

        ctx.Sel.Items.Remove(ElementRef.Object(light.Id)); // Ctrl-click takes one member back out
        groups.Update(ctx);
        Assert.Equal([box.Id], ctx.Sel.MeshIds());

        groups.Enabled = false;
        ctx.Sel.Clear();
        groups.Update(ctx);
        ctx.Sel.Add(ElementRef.Object(light.Id));
        groups.Update(ctx);
        Assert.Equal([light.Id], ctx.Sel.MeshIds());
        Assert.DoesNotContain(other.Id, ctx.Sel.MeshIds());
    }

    [Fact]
    public void Switching_to_face_mode_drops_entities_from_the_selection()
    {
        var ctx = RegistryTests.Headless(Level(out var box, out var light));
        Select(ctx, box.Id, light.Id);
        ctx.SetMode(SelectMode.Face);
        Assert.All(ctx.Sel.Items, i => Assert.Equal(box.Id, i.Mesh));
        Assert.False(ctx.Sections.Single(s => s.Id == "entity").Visible(ctx));
    }
}
