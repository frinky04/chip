using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using Chip.Editor.Features.Scene;

namespace Chip.Editor.Tests;

/// <summary>The outliner's tree model: structure, filtering, flattening and counts.</summary>
public class OutlinerTests
{
    static PolyMesh Mesh(string name) => new PolyMesh { Name = name }.Also(m =>
    {
        var box = MeshOps.CreateBox(Vector3.Zero, Vector3.One, "m", TextureMapping.Default);
        m.Positions.AddRange(box.Positions);
        m.Faces.AddRange(box.Faces);
    });

    static Document Level(out int floor, out int wall, out int crate, out int light, out int spawn)
    {
        var doc = new Document();
        floor = doc.Add(Mesh("floor")).Id;
        wall = doc.Add(Mesh("wall")).Id;
        crate = doc.Add(Mesh("crate")).Id;
        var l = doc.Add(EntityClasses.Create("light_point", Vector3.One));
        l.Name = "key light";
        light = l.Id;
        spawn = doc.Add(EntityClasses.Create("info_player_start", Vector3.Zero)).Id;
        var room = GroupOps.Create(doc, [floor, wall, light], "room")!;
        GroupOps.Create(doc, [light], "lights");
        return doc;
    }

    [Fact]
    public void Tree_lists_groups_first_then_loose_meshes_then_loose_entities()
    {
        var doc = Level(out int floor, out int wall, out int crate, out int light, out int spawn);
        var roots = OutlinerTree.Build(doc);
        Assert.Equal([OutlinerKind.Group, OutlinerKind.Mesh, OutlinerKind.Entity], roots.Select(r => r.Kind));
        Assert.Equal(["room", "crate", "info_player_start"], roots.Select(r => r.Name));
        var room = roots[0];
        Assert.Equal("3 objects", room.Detail);
        Assert.Equal(["lights", "floor", "wall"], room.Children.Select(c => c.Name)); // nested groups before members
        var lightNode = room.Children[0].Children.Single();
        Assert.Equal(light, lightNode.Id);
        Assert.Equal("key light", lightNode.Name);
        Assert.Equal("light_point", lightNode.Detail);
        Assert.Equal(2, lightNode.Depth);
    }

    [Fact]
    public void Filter_keeps_matches_and_their_ancestors()
    {
        var doc = Level(out _, out _, out _, out int light, out _);
        var byClass = OutlinerTree.Build(doc, "LIGHT_point");
        var room = Assert.Single(byClass);
        Assert.Equal("room", room.Name);
        Assert.Equal("lights", Assert.Single(room.Children).Name); // the group name matches too, so it keeps its contents
        var byName = OutlinerTree.Build(doc, "wal");
        Assert.Equal(["wall"], Assert.Single(byName).Children.Select(c => c.Name));
        Assert.Empty(OutlinerTree.Build(doc, "nothing like this"));
        var group = OutlinerTree.Build(doc, "room"); // a matching group keeps everything inside
        Assert.Equal(3, Assert.Single(group).Children.Count);
    }

    [Fact]
    public void Flatten_skips_collapsed_groups_unless_searching()
    {
        var doc = Level(out _, out _, out _, out _, out _);
        var roots = OutlinerTree.Build(doc);
        var room = roots[0];
        Assert.Equal(7, OutlinerTree.Flatten(roots, new HashSet<int>()).Count);
        Assert.Equal(3, OutlinerTree.Flatten(roots, new HashSet<int> { room.Id }).Count);
        Assert.Equal(7, OutlinerTree.Flatten(roots, new HashSet<int> { room.Id }, expandAll: true).Count);
    }

    [Fact]
    public void Group_rows_stand_for_all_their_members()
    {
        var doc = Level(out int floor, out int wall, out _, out int light, out _);
        var room = OutlinerTree.Build(doc)[0];
        Assert.Equal(new[] { floor, wall, light }.Order(), OutlinerTree.Objects(doc, room).Order());
        Assert.Equal("2 groups, 3 meshes, 2 entities", OutlinerTree.Summary(doc));
        Assert.Equal("0 meshes", OutlinerTree.Summary(new Document()));
    }

    [Fact]
    public void Deleted_members_disappear_from_the_tree()
    {
        var doc = Level(out int floor, out _, out _, out _, out _);
        doc.Meshes.RemoveAll(m => m.Id == floor); // e.g. merged away by another op: the group still lists the id
        var room = OutlinerTree.Build(doc)[0];
        Assert.DoesNotContain(room.Children, c => c.Id == floor);
        Assert.Equal("2 objects", room.Detail);
    }

    [Fact]
    public void A_mesh_entity_holds_its_meshes_wherever_they_are_grouped()
    {
        var doc = Level(out int floor, out int wall, out int crate, out int light, out _);
        var door = MeshEntityOps.Tie(doc, [wall, crate], "info_target")!;
        var roots = OutlinerTree.Build(doc);
        var row = Assert.Single(roots, r => r.Id == door.Id);
        Assert.Equal([wall, crate], row.Children.Select(c => c.Id));
        Assert.Equal(1, row.Children[0].Depth);
        var all = OutlinerTree.Flatten(roots, new HashSet<int>());
        Assert.Single(all, r => r.Id == wall); // not again under the room group
        Assert.Equal(all.Count - 2, OutlinerTree.Flatten(roots, new HashSet<int> { door.Id }).Count);
        Assert.Equal([door.Id], OutlinerTree.Ancestors(doc, wall)); // its row is under the door, which isn't grouped
        Assert.Equal(new[] { "lights", "room" }, OutlinerTree.Ancestors(doc, light).Select(id => doc.FindGroup(id)!.Name));
        Assert.Contains(floor, OutlinerTree.Objects(doc, roots[0]));
    }

    [Fact]
    public void Folders_show_as_folders_and_stay_listed_when_empty()
    {
        var doc = Level(out _, out _, out int crate, out _, out _);
        var props = GroupOps.CreateFolder(doc, [crate], "props");
        var empty = GroupOps.CreateFolder(doc, [], "later");
        var roots = OutlinerTree.Build(doc);
        Assert.Equal(OutlinerKind.Folder, roots.Single(r => r.Id == props.Id).Kind);
        var later = roots.Single(r => r.Id == empty.Id);
        Assert.True(later.IsContainer);
        Assert.Equal("empty", later.Detail);
        Assert.DoesNotContain(OutlinerTree.Build(doc, show: OutlinerShow.Entities), r => r.Id == empty.Id); // narrowing hides it
        Assert.Equal("2 folders, 2 groups, 3 meshes, 2 entities", OutlinerTree.Summary(doc));
    }

    [Fact]
    public void Showing_one_kind_keeps_the_groups_that_hold_it()
    {
        var doc = Level(out _, out _, out int crate, out int light, out int spawn);
        var entities = OutlinerTree.Build(doc, show: OutlinerShow.Entities);
        Assert.Equal(["room", "info_player_start"], entities.Select(r => r.Name));
        Assert.Equal(light, entities[0].Children.Single().Children.Single().Id); // room > lights > key light
        var meshes = OutlinerTree.Build(doc, show: OutlinerShow.Meshes);
        Assert.Equal(["room", "crate"], meshes.Select(r => r.Name));
        Assert.Equal(["floor", "wall"], meshes[0].Children.Select(c => c.Name)); // the lights group held no meshes
        Assert.Empty(OutlinerTree.Build(doc, "crate", OutlinerShow.Entities));
    }

    [Fact]
    public void A_linked_copy_of_one_mesh_is_one_row_and_a_bigger_one_lists_its_parts()
    {
        var doc = new Document();
        int crate = doc.Add(Mesh("crate")).Id;
        var one = InstanceOps.Make(doc, [crate], "crate prop")!;
        int body = doc.Add(Mesh("body")).Id, lid = doc.Add(Mesh("lid")).Id;
        var two = InstanceOps.Make(doc, [body, lid], "chest prop")!;

        var roots = OutlinerTree.Build(doc);
        var crateRow = roots.Single(r => r.Id == one.Id);
        Assert.True(crateRow.IsCopy);
        Assert.Empty(crateRow.Children);
        Assert.Equal("", crateRow.Detail);
        var chestRow = roots.Single(r => r.Id == two.Id);
        Assert.Equal(["body", "lid"], chestRow.Children.Select(c => c.Name));
        Assert.Equal("2 parts", chestRow.Detail);

        // The copy's row stands for its meshes; showing only meshes keeps copies as one row each.
        Assert.Equal(one.Id, OutlinerTree.RowFor(doc, crate));
        Assert.Equal(crate, OutlinerTree.RowFor(doc, crate) == one.Id ? OutlinerTree.Objects(doc, crateRow).Single() : -1);
        Assert.Equal(2, OutlinerTree.Build(doc, show: OutlinerShow.Meshes).Count);
        Assert.Empty(OutlinerTree.Build(doc, show: OutlinerShow.Entities));
    }
}

static class TestExtensions
{
    public static T Also<T>(this T value, Action<T> act)
    {
        act(value);
        return value;
    }
}
