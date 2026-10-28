using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Picking;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using Chip.Editor.Features.Scene;

namespace Chip.Editor.Tests;

/// <summary>The pick filter commands, folders from the Edit menu, and folders in whole-group selection, on a headless context.</summary>
public class ObjectPickingCommandTests
{
    static Document Level(out PolyMesh wall, out PolyMesh panel, out Entity door, out Entity light)
    {
        var doc = new Document();
        wall = doc.Add(MeshOps.CreateBox(Vector3.Zero, new Vector3(4, 3, 0.2f), "dev_a", TextureMapping.Default));
        panel = doc.Add(MeshOps.CreateBox(new Vector3(1, 0, 0), new Vector3(2, 2, 0.1f), "dev_a", TextureMapping.Default));
        door = MeshEntityOps.Tie(doc, [panel.Id], "info_target")!;
        light = doc.Add(EntityClasses.Create("light_point", new Vector3(1, 4, 1)));
        return doc;
    }

    [Fact]
    public void Five_toggles_entities_only_and_switches_to_object_mode()
    {
        var ctx = RegistryTests.Headless(Level(out _, out _, out _, out _));
        var command = ctx.Commands.Find("select.pickEntities")!;
        Assert.Equal("5", command.HotkeyText);
        ctx.SetMode(SelectMode.Face);
        command.Execute();
        Assert.Equal(SelectMode.Object, ctx.Sel.Mode);
        Assert.Equal(PickFilter.Entities, ctx.PickFilter);
        Assert.True(command.IsChecked!());
        command.Execute();
        Assert.Equal(PickFilter.All, ctx.PickFilter);
        ctx.Commands.Execute("select.pickGeometry");
        Assert.Equal(PickFilter.Geometry, ctx.PickFilter);
    }

    [Fact]
    public void Select_all_and_invert_follow_the_filter()
    {
        var ctx = RegistryTests.Headless(Level(out var wall, out var panel, out var door, out var light));
        ctx.Commands.Execute("select.pickEntities");
        ctx.Commands.Execute("select.all");
        Assert.Equal(new[] { panel.Id, door.Id, light.Id }.Order(), ctx.Sel.MeshIds().Order());
        ctx.Commands.Execute("select.pickGeometry");
        ctx.Sel.Clear();
        ctx.Commands.Execute("select.invert");
        Assert.Equal([wall.Id], ctx.Sel.MeshIds());
    }

    [Fact]
    public void New_folder_takes_the_selection_or_starts_empty_in_one_undo_step()
    {
        var ctx = RegistryTests.Headless(Level(out var wall, out _, out _, out var light));
        var command = ctx.Commands.Find("scene.folder")!;
        Assert.Equal("New folder", command.CurrentLabel);
        command.Execute();
        var empty = Assert.Single(ctx.Doc.Groups);
        Assert.True(empty.Folder);
        Assert.Empty(empty.Members);

        ctx.Sel.Add(ElementRef.Object(wall.Id));
        ctx.Sel.Add(ElementRef.Object(light.Id));
        Assert.Equal("Move to new folder", command.CurrentLabel);
        command.Execute();
        var folder = ctx.Doc.Groups.Single(g => g.Members.Count > 0);
        Assert.True(folder.Folder);
        Assert.Equal(new[] { wall.Id, light.Id }.Order(), folder.Members.Order());
        ctx.Session.Undo();
        Assert.Single(ctx.Doc.Groups);
    }

    [Fact]
    public void Clicking_a_folder_member_selects_just_it_but_a_group_member_selects_the_group()
    {
        var doc = Level(out var wall, out _, out _, out var light);
        var folder = GroupOps.CreateFolder(doc, [wall.Id, light.Id]);
        var ctx = RegistryTests.Headless(doc);
        var groups = GroupSelection.Of(ctx);
        groups.Update(ctx);
        ctx.Sel.Add(ElementRef.Object(wall.Id));
        groups.Update(ctx);
        Assert.Equal([wall.Id], ctx.Sel.MeshIds());

        ctx.Session.Do("Make Group", () => ctx.Doc.FindGroup(folder.Id)!.Folder = false);
        ctx.Sel.Clear();
        groups.Update(ctx);
        ctx.Sel.Add(ElementRef.Object(wall.Id));
        groups.Update(ctx);
        Assert.Equal(new[] { wall.Id, light.Id }.Order(), ctx.Sel.MeshIds().Order());
    }

    [Fact]
    public void Ungroup_is_off_for_objects_that_are_only_in_a_folder()
    {
        var doc = Level(out var wall, out _, out _, out _);
        GroupOps.CreateFolder(doc, [wall.Id]);
        var ctx = RegistryTests.Headless(doc);
        ctx.Sel.Add(ElementRef.Object(wall.Id));
        Assert.False(ctx.Commands.Find("scene.ungroup")!.Enabled);
        Assert.True(ctx.Commands.Find("scene.group")!.Enabled);
    }
}
