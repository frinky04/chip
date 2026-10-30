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
        Assert.Equal("Place Info player start", ctx.Session.UndoLabel);
        ctx.Commands.Execute("edit.undo");
        Assert.Single(ctx.Doc.Entities);
    }

    [Fact]
    public void Inspector_edits_are_one_undo_step_each_and_bad_keys_change_nothing()
    {
        var ctx = RegistryTests.Headless(Level(out _, out var light));
        int id = light.Id;
        Assert.True(EntitySection.Edit(ctx, id, "Edit Property", e => EntityOps.SetProperty(e, "note", "25")));
        Assert.True(EntitySection.Edit(ctx, id, "Rename Property", e => EntityOps.RenameProperty(e, "note", "memo")));
        Assert.True(EntitySection.Edit(ctx, id, "Edit Property", e => EntityOps.SetProperty(e, "other", "x")));
        var live = ctx.Doc.FindEntity(id)!;
        Assert.Equal("25", EntityValues.Text(live.Properties["memo"]));
        Assert.False(live.Properties.ContainsKey("note"));

        Assert.False(EntitySection.Edit(ctx, id, "Rename Property", e => EntityOps.RenameProperty(e, "memo", "other")));
        Assert.True(ctx.StatusIsError);
        Assert.Equal("Edit Property", ctx.Session.UndoLabel); // the refused edit left no step
        Assert.Equal("25", EntityValues.Text(ctx.Doc.FindEntity(id)!.Properties["memo"]));

        ctx.Commands.Execute("edit.undo");
        ctx.Commands.Execute("edit.undo");
        Assert.Equal("25", EntityValues.Text(ctx.Doc.FindEntity(id)!.Properties["note"]));
        ctx.Commands.Execute("edit.undo");
        Assert.Empty(ctx.Doc.FindEntity(id)!.Properties);
    }

    [Fact]
    public void An_edit_on_several_entities_is_one_undo_step_and_one_failure_changes_none()
    {
        var doc = Level(out _, out var a);
        var b = doc.Add(EntityClasses.Create("light_spot", new Vector3(5, 1, 0)));
        var ctx = RegistryTests.Headless(doc);
        int[] ids = [a.Id, b.Id];
        var energy = EntityClasses.Get("light_point").Prop("light_energy")!;

        Assert.True(EntitySection.Edit(ctx, ids, "Set Energy on 2 Entities",
            e => EntityOps.SetProperty(e, EntityMultiEdit.SpecFor(e, energy), EntityValues.Of(3.0))));
        Assert.All(ids, id => Assert.Equal(3, ctx.Doc.FindEntity(id)!.Properties["light_energy"].GetDouble()));

        // Typing one axis lines them up on it and keeps each one's others.
        Assert.True(EntitySection.Edit(ctx, ids, "Move 2 Entities", e => { e.Position = EntityMultiEdit.WithAxis(e.Position, 1, 0); return null; }));
        Assert.Equal(new Vector3(1, 0, 1), ctx.Doc.FindEntity(a.Id)!.Position);
        Assert.Equal(new Vector3(5, 0, 0), ctx.Doc.FindEntity(b.Id)!.Position);

        // The second entity refuses: the first keeps its value too, and the error names the one that refused.
        Assert.True(EntitySection.Edit(ctx, [b.Id], "Edit Property", e => EntityOps.SetProperty(e, "taken", "x")));
        Assert.False(EntitySection.Edit(ctx, ids, "Edit Property", e => { e.Properties["note"] = EntityValues.Of("y"); return EntityOps.RenameProperty(e, "note", "taken"); }));
        Assert.True(ctx.StatusIsError);
        Assert.False(ctx.Doc.FindEntity(a.Id)!.Properties.ContainsKey("note"));
        Assert.False(ctx.Doc.FindEntity(a.Id)!.Properties.ContainsKey("taken"));

        ctx.Commands.Execute("edit.undo"); // the one-entity edit
        ctx.Commands.Execute("edit.undo"); // the move, both at once
        Assert.Equal(new Vector3(1, 4, 1), ctx.Doc.FindEntity(a.Id)!.Position);
        Assert.Equal(new Vector3(5, 1, 0), ctx.Doc.FindEntity(b.Id)!.Position);
        ctx.Commands.Execute("edit.undo");
        Assert.All(ids, id => Assert.False(ctx.Doc.FindEntity(id)!.Properties.ContainsKey("light_energy")));
    }

    [Fact]
    public void Class_properties_are_typed_and_a_default_value_is_not_stored()
    {
        var ctx = RegistryTests.Headless(Level(out _, out var light));
        int id = light.Id;
        var range = EntityClasses.Get("light_point").Prop("omni_range")!;
        Assert.True(EntitySection.Edit(ctx, id, "Set Omni range", e => EntityOps.SetProperty(e, range, EntityValues.Of("12"))));
        Assert.Equal(12, ctx.Doc.FindEntity(id)!.Properties["omni_range"].GetDouble()); // typed text becomes a number

        Assert.False(EntitySection.Edit(ctx, id, "Set Omni range", e => EntityOps.SetProperty(e, range, EntityValues.Of("far"))));
        Assert.True(ctx.StatusIsError);
        Assert.Equal(12, ctx.Doc.FindEntity(id)!.Properties["omni_range"].GetDouble());

        // Setting the class default removes the value, so the entity follows the default if Godot changes it.
        Assert.True(EntitySection.Edit(ctx, id, "Set Omni range", e => EntityOps.SetProperty(e, range, range.Default)));
        Assert.False(ctx.Doc.FindEntity(id)!.Properties.ContainsKey("omni_range"));
        Assert.Equal(range.Default.GetDouble(), EntityValues.Effective(ctx.Doc.FindEntity(id)!, range).GetDouble());
    }

    [Fact]
    public void Changing_class_keeps_values()
    {
        var ctx = RegistryTests.Headless(Level(out _, out var light));
        light.Properties["light_energy"] = EntityValues.Of(3.0);
        EntitySection.Edit(ctx, light.Id, "Change Entity Class", e => { EntityOps.SetClass(e, "light_spot"); return null; });
        var live = ctx.Doc.FindEntity(light.Id)!;
        Assert.Equal("light_spot", live.ClassName);
        Assert.Equal(3, live.Properties["light_energy"].GetDouble());
        Assert.Single(live.Properties); // no defaults copied in
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
