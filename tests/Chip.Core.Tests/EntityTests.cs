using System.Numerics;
using System.Text.Json.Nodes;
using Chip.Core.Geometry;
using Chip.Core.IO;
using Chip.Core.Picking;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Core.Tests;

/// <summary>Point entities: orientation, selection alongside meshes, gizmo transforms, picking and file round trips.</summary>
public class EntityTests
{
    static PolyMesh Box(Vector3 a, Vector3 b) => MeshOps.CreateBox(a, b, "m", TextureMapping.Default);

    static ViewInfo LookingDownZ(Vector3 eye) => new(Matrix4x4.CreateLookAt(eye, eye - Vector3.UnitZ, Vector3.UnitY) *
        Matrix4x4.CreatePerspectiveFieldOfView(1, 1, 0.1f, 100), new Vector2(512, 512), eye);

    static void AssertNear(Vector3 expected, Vector3 actual, float eps = 1e-3f) =>
        Assert.True(Vector3.Distance(expected, actual) < eps, $"expected {expected}, got {actual}");

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 90, 0)]
    [InlineData(30, -45, 10)]
    [InlineData(-80, 170, -120)]
    public void Euler_angles_round_trip_through_quaternions(float x, float y, float z)
    {
        var q = EntityOps.Orientation(new Vector3(x, y, z));
        var back = EntityOps.Orientation(EntityOps.ToEuler(q));
        Assert.True(MathF.Abs(Quaternion.Dot(q, back)) > 0.99999f);
        AssertNear(new Vector3(x, y, z), EntityOps.ToEuler(q), 0.01f);
    }

    [Fact]
    public void Yaw_turns_the_forward_axis_counter_clockwise_seen_from_above()
    {
        var e = EntityClasses.Create("info_player_start", Vector3.Zero);
        AssertNear(-Vector3.UnitZ, EntityOps.Forward(e));
        e.Rotation = new Vector3(0, 90, 0);
        AssertNear(-Vector3.UnitX, EntityOps.Forward(e));
        e.Rotation = new Vector3(-90, 0, 0); // pitch up... or down: forward leaves the ground plane
        Assert.True(MathF.Abs(EntityOps.Forward(e).Y) > 0.999f);
    }

    [Fact]
    public void New_entities_get_their_class_defaults_and_unique_ids()
    {
        var doc = new Document();
        var mesh = doc.Add(Box(Vector3.Zero, Vector3.One));
        var light = doc.Add(EntityClasses.Create("light_point", new Vector3(1, 2, 3)));
        Assert.NotEqual(mesh.Id, light.Id);
        Assert.Equal("10", light.Properties["range"]);
        light.Properties["range"] = "4";
        Assert.Equal("10", EntityClasses.Get("light_point").Defaults["range"]); // defaults aren't shared
        Assert.Equal(new Vector3(-0.2f), EntityClasses.Get("my_custom").BoxMin);
    }

    [Fact]
    public void Entities_groups_and_lock_round_trip_through_the_file()
    {
        var doc = new Document();
        var mesh = doc.Add(Box(Vector3.Zero, Vector3.One));
        var e = doc.Add(EntityClasses.Create("light_spot", new Vector3(1, 2, 3), 45));
        e.Name = "lamp";
        e.Locked = true;
        e.Properties["angle"] = "30";
        var outer = GroupOps.Create(doc, [mesh.Id, e.Id], "room")!;
        var inner = GroupOps.Create(doc, [e.Id], "lights")!;
        inner.Hidden = true;

        var back = DocumentSerializer.Deserialize(DocumentSerializer.Serialize(doc));
        var be = back.FindEntity(e.Id)!;
        Assert.Equal("lamp", be.Name);
        Assert.True(be.Locked);
        Assert.Equal(new Vector3(0, 45, 0), be.Rotation);
        Assert.Equal("30", be.Properties["angle"]);
        Assert.Equal(2, back.Groups.Count);
        var bi = back.FindGroup(inner.Id)!;
        Assert.Equal(outer.Id, bi.Parent);
        Assert.Equal([e.Id], bi.Members);
        Assert.True(bi.Hidden);
        Assert.Equal([mesh.Id], back.FindGroup(outer.Id)!.Members);
        Assert.True(back.NextId > inner.Id);
        Assert.Equal(doc.NextId, back.NextId);
    }

    [Fact]
    public void Files_without_groups_or_entity_lock_still_load()
    {
        var doc = new Document();
        doc.Add(EntityClasses.Create("info_target", Vector3.One));
        var json = DocumentSerializer.Serialize(doc);
        Assert.DoesNotContain("groups", json);
        var node = JsonNode.Parse(json)!;
        node["entities"]![0]!.AsObject().Remove("locked"); // as written before entity lock existed
        var back = DocumentSerializer.Deserialize(node.ToJsonString());
        Assert.Empty(back.Groups);
        Assert.False(back.Entities[0].Locked);
    }

    [Fact]
    public void Prune_keeps_entities_in_object_mode_and_drops_hidden_locked_or_deleted_ones()
    {
        var doc = new Document();
        var mesh = doc.Add(Box(Vector3.Zero, Vector3.One));
        var a = doc.Add(EntityClasses.Create("info_target", Vector3.One));
        var b = doc.Add(EntityClasses.Create("info_target", Vector3.One));
        var c = doc.Add(EntityClasses.Create("info_target", Vector3.One));
        var sel = new Selection();
        foreach (var id in new[] { mesh.Id, a.Id, b.Id, c.Id }) sel.Add(ElementRef.Object(id));
        b.Locked = true;
        doc.Entities.Remove(c);
        sel.Prune(doc);
        Assert.Equal([mesh.Id, a.Id], sel.MeshIds().Order());
        Assert.Equal([a], sel.Entities(doc));
        a.Hidden = true;
        sel.Prune(doc);
        Assert.Equal([mesh.Id], sel.MeshIds());
    }

    [Fact]
    public void Mesh_only_queries_skip_entities_and_component_modes_drop_them()
    {
        var doc = new Document();
        var mesh = doc.Add(Box(Vector3.Zero, Vector3.One));
        var e = doc.Add(EntityClasses.Create("info_target", new Vector3(5, 0, 0)));
        var sel = new Selection();
        sel.Add(ElementRef.Object(mesh.Id));
        sel.Add(ElementRef.Object(e.Id));
        Assert.Single(sel.VertexSets(doc));
        Assert.Single(sel.FaceSets(doc));

        sel.ConvertTo(SelectMode.Face, doc);
        Assert.Equal(6, sel.Count);
        Assert.All(sel.Items, i => Assert.Equal(mesh.Id, i.Mesh));
        Assert.Empty(sel.Entities(doc));
        sel.ConvertTo(SelectMode.Object, doc);
        Assert.Equal([ElementRef.Object(mesh.Id)], sel.Items);
    }

    [Fact]
    public void Select_all_and_invert_include_selectable_entities_in_object_mode_only()
    {
        var doc = new Document();
        var mesh = doc.Add(Box(Vector3.Zero, Vector3.One));
        var a = doc.Add(EntityClasses.Create("info_target", Vector3.One));
        var locked = doc.Add(EntityClasses.Create("info_target", Vector3.One));
        locked.Locked = true;
        var sel = new Selection();
        sel.SelectAll(doc);
        Assert.Equal([mesh.Id, a.Id], sel.MeshIds().Order());
        sel.Clear();
        sel.Add(ElementRef.Object(mesh.Id));
        sel.Invert(doc);
        Assert.Equal([a.Id], sel.MeshIds());
        sel.ConvertTo(SelectMode.Vertex, doc);
        sel.SelectAll(doc);
        Assert.All(sel.Items, i => Assert.Equal(mesh.Id, i.Mesh));
    }

    [Fact]
    public void Gizmo_moves_and_turns_entities_with_the_meshes_in_one_undo_step()
    {
        var session = new Session();
        var doc = new Document();
        var mesh = doc.Add(Box(new Vector3(-1, 0, -1), new Vector3(1, 2, 1)));
        var e = doc.Add(EntityClasses.Create("info_player_start", new Vector3(3, 0, 0)));
        session.Reset(doc, null);
        session.Sel.Add(ElementRef.Object(mesh.Id));
        session.Sel.Add(ElementRef.Object(e.Id));

        var g = TransformGesture.Start(session, "Rotate")!;
        Assert.Equal(new Vector3(1, 1, 0), g.Pivot); // bounds include the entity's origin
        var turn = TransformGesture.About(Vector3.Zero, Matrix4x4.CreateRotationY(MathF.PI / 2));
        g.Apply(turn, rigid: true);
        g.Apply(turn, rigid: true); // absolute: applying twice doesn't accumulate
        g.Commit();

        var live = session.Doc.FindEntity(e.Id)!;
        AssertNear(Vector3.Transform(new Vector3(3, 0, 0), turn), live.Position);
        AssertNear(Vector3.TransformNormal(-Vector3.UnitZ, turn), EntityOps.Forward(live));
        Assert.Equal(90, live.Rotation.Y, 2);

        Assert.True(session.Undo());
        var restored = session.Doc.FindEntity(e.Id)!;
        Assert.Equal(new Vector3(3, 0, 0), restored.Position);
        Assert.Equal(Vector3.Zero, restored.Rotation);
        Assert.Equal(2, session.Sel.Count); // selection restored too
    }

    [Fact]
    public void Moving_or_scaling_keeps_typed_rotation_and_entity_size()
    {
        var session = new Session();
        var doc = new Document();
        var e = doc.Add(EntityClasses.Create("info_target", new Vector3(2, 0, 0)));
        e.Rotation = new Vector3(0, 270, 0);
        session.Reset(doc, null);
        session.Sel.Add(ElementRef.Object(e.Id));

        var g = TransformGesture.Start(session, "Scale")!;
        g.Apply(TransformGesture.About(Vector3.Zero, Matrix4x4.CreateScale(2)), rigid: false);
        g.Commit();
        Assert.Equal(new Vector3(4, 0, 0), session.Doc.FindEntity(e.Id)!.Position);
        Assert.Equal(new Vector3(0, 270, 0), session.Doc.FindEntity(e.Id)!.Rotation);

        var cancel = TransformGesture.Start(session, "Move")!;
        cancel.Apply(Matrix4x4.CreateTranslation(1, 1, 1), rigid: true);
        cancel.Cancel();
        Assert.Equal(new Vector3(4, 0, 0), session.Doc.FindEntity(e.Id)!.Position);
    }

    [Fact]
    public void Entities_are_picked_by_box_or_glyph_unless_a_surface_hides_them()
    {
        var doc = new Document();
        var e = doc.Add(EntityClasses.Create("info_target", Vector3.Zero));
        var view = LookingDownZ(new Vector3(0, 0, 10));
        var center = new Vector2(256, 256);

        Assert.Equal(ElementRef.Object(e.Id), Picker.Pick(doc, view, center, SelectMode.Object));
        Assert.Null(Picker.Pick(doc, view, center, SelectMode.Face)); // entities are object-mode only
        Assert.Equal(e, Picker.PickEntity(doc, view, center + new Vector2(8, 0))); // on the glyph, off the small box
        Assert.Null(Picker.PickEntity(doc, view, center + new Vector2(60, 0)));

        var wall = doc.Add(Box(new Vector3(-2, -2, 4), new Vector3(2, 2, 5)));
        Assert.Equal(ElementRef.Object(wall.Id), Picker.Pick(doc, view, center, SelectMode.Object));
        wall.Hidden = true;
        Assert.Equal(ElementRef.Object(e.Id), Picker.Pick(doc, view, center, SelectMode.Object));
        e.Locked = true;
        Assert.Null(Picker.Pick(doc, view, center, SelectMode.Object));
    }

    [Fact]
    public void Box_select_in_object_mode_includes_entities()
    {
        var doc = new Document();
        var e = doc.Add(EntityClasses.Create("info_target", Vector3.Zero));
        var hidden = doc.Add(EntityClasses.Create("info_target", new Vector3(0.5f, 0, 0)));
        hidden.Hidden = true;
        var view = LookingDownZ(new Vector3(0, 0, 10));
        var picked = Picker.Marquee(doc, view, new Vector2(200, 200), new Vector2(312, 312), SelectMode.Object);
        Assert.Equal([ElementRef.Object(e.Id)], picked);
        Assert.Empty(Picker.Marquee(doc, view, new Vector2(200, 200), new Vector2(312, 312), SelectMode.Vertex));
    }

    [Fact]
    public void Ray_hits_the_oriented_class_box()
    {
        var e = EntityClasses.Create("info_player_start", Vector3.Zero); // box 0.6 x 1.8 x 0.6 from the feet up
        var down = new Ray(new Vector3(0, 10, 0), -Vector3.UnitY);
        Assert.Equal(8.2f, EntityOps.RayHit(e, down)!.Value, 3);
        var beside = new Ray(new Vector3(0.5f, 10, 0), -Vector3.UnitY);
        Assert.Null(EntityOps.RayHit(e, beside));
        e.Rotation = new Vector3(0, 0, 90); // lying on its side: now 1.8 m along X
        Assert.NotNull(EntityOps.RayHit(e, new Ray(new Vector3(-1.5f, 10, 0), -Vector3.UnitY)));
    }
}
