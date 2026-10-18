using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Core.Tests;

/// <summary>Outliner groups: grouping, nesting, ungrouping, group selection, and hide/lock propagation.</summary>
public class GroupTests
{
    static (Document Doc, int[] Ids) Scene(int meshes, int entities = 0)
    {
        var doc = new Document();
        var ids = new List<int>();
        for (int i = 0; i < meshes; i++)
            ids.Add(doc.Add(MeshOps.CreateBox(new Vector3(i * 3, 0, 0), new Vector3(i * 3 + 1, 1, 1), "m", TextureMapping.Default)).Id);
        for (int i = 0; i < entities; i++) ids.Add(doc.Add(EntityClasses.Create("info_target", new Vector3(i, 5, 0))).Id);
        return (doc, ids.ToArray());
    }

    [Fact]
    public void Grouping_whole_groups_nests_them_instead_of_splitting()
    {
        var (doc, ids) = Scene(3, 1);
        var a = GroupOps.Create(doc, [ids[0], ids[1]])!;
        Assert.Equal("Group 1", a.Name);
        var outer = GroupOps.Create(doc, ids)!; // covers all of a, plus two loose objects
        Assert.Equal("Group 2", outer.Name);
        Assert.Equal(outer.Id, a.Parent);
        Assert.Equal([ids[0], ids[1]], a.Members);
        Assert.Equal([ids[2], ids[3]], outer.Members.Order());
        Assert.Equal(ids.Order(), GroupOps.AllMembers(doc, outer).Order());
    }

    [Fact]
    public void Grouping_part_of_a_group_makes_a_subgroup_inside_it()
    {
        var (doc, ids) = Scene(3);
        var room = GroupOps.Create(doc, ids, "room")!;
        var sub = GroupOps.Create(doc, [ids[0], ids[1]])!;
        Assert.Equal(room.Id, sub.Parent);
        Assert.Equal([ids[2]], room.Members);
        Assert.Equal(room, GroupOps.Ancestry(doc, sub)[^1]);
    }

    [Fact]
    public void An_object_belongs_to_one_group()
    {
        var (doc, ids) = Scene(2);
        var a = GroupOps.Create(doc, [ids[0]])!;
        var b = GroupOps.Create(doc, [ids[1]])!;
        GroupOps.MoveTo(doc, ids[0], b);
        Assert.Equal(b, GroupOps.GroupOf(doc, ids[0]));
        Assert.DoesNotContain(ids[0], a.Members);
        GroupOps.MoveTo(doc, ids[0], null);
        Assert.Null(GroupOps.GroupOf(doc, ids[0]));
    }

    [Fact]
    public void A_group_cannot_move_inside_itself()
    {
        var (doc, ids) = Scene(2);
        var inner = GroupOps.Create(doc, [ids[0]])!;
        var outer = GroupOps.Create(doc, [ids[0], ids[1]])!;
        Assert.Equal(outer.Id, inner.Parent);
        Assert.False(GroupOps.MoveTo(doc, outer, inner));
        Assert.False(GroupOps.MoveTo(doc, outer, outer));
        Assert.True(GroupOps.MoveTo(doc, inner, null));
        Assert.Equal(0, inner.Parent);
    }

    [Fact]
    public void Ungroup_dissolves_the_covered_group_and_moves_its_contents_up()
    {
        var (doc, ids) = Scene(3);
        var inner = GroupOps.Create(doc, [ids[0], ids[1]])!;
        var outer = GroupOps.Create(doc, ids)!;
        Assert.Equal(1, GroupOps.Ungroup(doc, ids)); // the outermost covered group goes
        Assert.Null(doc.FindGroup(outer.Id));
        Assert.Equal(0, inner.Parent);
        Assert.Null(GroupOps.GroupOf(doc, ids[2]));
        Assert.Equal(1, GroupOps.Ungroup(doc, [ids[0], ids[1]]));
        Assert.Empty(doc.Groups);
    }

    [Fact]
    public void Ungrouping_part_of_a_group_takes_those_objects_out()
    {
        var (doc, ids) = Scene(3);
        var g = GroupOps.Create(doc, ids)!;
        Assert.Equal(0, GroupOps.Ungroup(doc, [ids[0]]));
        Assert.Equal([ids[1], ids[2]], g.Members);
        Assert.Null(GroupOps.GroupOf(doc, ids[0]));
    }

    [Fact]
    public void Selecting_a_member_selects_its_outermost_group()
    {
        var (doc, ids) = Scene(4, 1);
        GroupOps.Create(doc, [ids[0], ids[1]]);
        GroupOps.Create(doc, [ids[0], ids[1], ids[2]]);
        doc.Find(ids[2])!.Locked = true; // locked members stay out
        Assert.Equal([ids[0], ids[1]], GroupOps.ExpandToGroups(doc, [ids[1]]).Order());
        Assert.Equal([ids[3]], GroupOps.ExpandToGroups(doc, [ids[3]]));
    }

    [Fact]
    public void Hiding_and_locking_a_group_reaches_nested_members()
    {
        var (doc, ids) = Scene(2, 1);
        var inner = GroupOps.Create(doc, [ids[2]])!;
        var outer = GroupOps.Create(doc, ids)!;
        GroupOps.SetHidden(doc, outer, true);
        Assert.True(inner.Hidden);
        Assert.True(doc.Find(ids[0])!.Hidden);
        Assert.True(doc.FindEntity(ids[2])!.Hidden);
        Assert.True(SceneOps.AnyHidden(doc));
        SceneOps.UnhideAll(doc);
        Assert.False(outer.Hidden || inner.Hidden || doc.FindEntity(ids[2])!.Hidden);

        GroupOps.SetLocked(doc, inner, true);
        Assert.True(doc.FindEntity(ids[2])!.Locked);
        Assert.False(doc.Find(ids[0])!.Locked);
        Assert.False(SceneOps.IsSelectable(doc, ids[2]));
        SceneOps.UnlockAll(doc);
        Assert.False(SceneOps.AnyLocked(doc));
    }

    [Fact]
    public void Groups_and_their_flags_are_undoable()
    {
        var (doc, ids) = Scene(2);
        var session = new Session();
        session.Reset(doc, null);
        session.Do("Group", () => GroupOps.Create(session.Doc, ids));
        var g = session.Doc.Groups.Single();
        session.Do("Hide group", () => GroupOps.SetHidden(session.Doc, session.Doc.Groups[0], true));
        Assert.True(session.Doc.Find(ids[0])!.Hidden);
        session.Undo();
        Assert.False(session.Doc.Find(ids[0])!.Hidden);
        Assert.False(session.Doc.Groups[0].Hidden);
        session.Undo();
        Assert.Empty(session.Doc.Groups);
        session.Redo();
        Assert.Equal(g.Id, session.Doc.Groups.Single().Id);
    }
}
