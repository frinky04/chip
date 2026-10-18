using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Core.Tests;

/// <summary>Object-level delete, duplicate, hide, lock and rename across meshes, entities and groups.</summary>
public class SceneOpsTests
{
    static Document Doc(out PolyMesh a, out PolyMesh b, out Entity e)
    {
        var doc = new Document();
        a = doc.Add(MeshOps.CreateBox(Vector3.Zero, Vector3.One, "m", TextureMapping.Default));
        b = doc.Add(MeshOps.CreateBox(new Vector3(3, 0, 0), new Vector3(4, 1, 1), "m", TextureMapping.Default));
        e = doc.Add(EntityClasses.Create("light_point", new Vector3(0, 3, 0)));
        return doc;
    }

    [Fact]
    public void Delete_removes_meshes_and_entities_and_empty_groups()
    {
        var doc = Doc(out var a, out var b, out var e);
        var g = GroupOps.Create(doc, [a.Id, e.Id])!;
        var keep = GroupOps.Create(doc, [b.Id])!;
        SceneOps.Delete(doc, [a.Id, e.Id]);
        Assert.Equal([b], doc.Meshes);
        Assert.Empty(doc.Entities);
        Assert.Null(doc.FindGroup(g.Id));
        Assert.NotNull(doc.FindGroup(keep.Id));
    }

    [Fact]
    public void Duplicate_copies_entities_into_the_same_group()
    {
        var doc = Doc(out var a, out var b, out var e);
        var g = GroupOps.Create(doc, [a.Id, e.Id])!;
        var copies = SceneOps.Duplicate(doc, [e.Id]);
        var copy = doc.FindEntity(Assert.Single(copies))!;
        Assert.NotEqual(e.Id, copy.Id);
        Assert.Equal(e.Properties, copy.Properties);
        Assert.NotSame(e.Properties, copy.Properties);
        Assert.Contains(copy.Id, g.Members);
    }

    [Fact]
    public void Duplicating_a_whole_group_copies_the_group()
    {
        var doc = Doc(out var a, out var b, out var e);
        var inner = GroupOps.Create(doc, [e.Id], "lights")!;
        var outer = GroupOps.Create(doc, [a.Id, e.Id], "room")!;
        var copies = SceneOps.Duplicate(doc, [a.Id, e.Id]);
        Assert.Equal(2, copies.Count);
        Assert.Equal(4, doc.Groups.Count);
        var outerCopy = doc.Groups.Single(g => g.Name == "room" && g != outer);
        var innerCopy = doc.Groups.Single(g => g.Name == "lights" && g != inner);
        Assert.Equal(outerCopy.Id, innerCopy.Parent);
        Assert.Equal(copies.Order(), GroupOps.AllMembers(doc, outerCopy).Order());
        Assert.Equal(0, outerCopy.Parent);
    }

    [Fact]
    public void Hide_lock_and_rename_work_on_any_object()
    {
        var doc = Doc(out var a, out _, out var e);
        SceneOps.SetHidden(doc, [a.Id, e.Id], true);
        Assert.True(a.Hidden && e.Hidden);
        SceneOps.SetLocked(doc, [e.Id], true);
        Assert.True(e.Locked);
        Assert.True(SceneOps.Rename(doc, e.Id, "  key light "));
        Assert.Equal("key light", SceneOps.NameOf(doc, e.Id));
        e.Name = "";
        Assert.Equal("light_point", SceneOps.NameOf(doc, e.Id));
        Assert.False(SceneOps.Rename(doc, 999, "x"));
    }
}
