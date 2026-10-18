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
}

static class TestExtensions
{
    public static T Also<T>(this T value, Action<T> act)
    {
        act(value);
        return value;
    }
}
