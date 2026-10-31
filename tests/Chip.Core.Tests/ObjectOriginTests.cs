using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.IO;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Core.Tests;

/// <summary>Object origins: meshes and groups carry a pivot frame that moves with them, saves, and places export nodes.</summary>
public class ObjectOriginTests
{
    static PolyMesh Box(Vector3 min, Vector3 max) => MeshOps.CreateBox(min, max, "dev_a", TextureMapping.Default);

    static void AssertNear(Vector3 expected, Vector3 actual, float tol = 1e-4f) =>
        Assert.True(Vector3.Distance(expected, actual) < tol, $"expected {expected}, got {actual}");

    static Session SessionWith(Document doc, params int[] selected)
    {
        var session = new Session();
        session.Reset(doc, null);
        foreach (var id in selected) session.Sel.Add(ElementRef.Object(id));
        return session;
    }

    [Fact]
    public void Placing_puts_the_origin_at_the_bottom_centre_or_centre()
    {
        var doc = new Document();
        var crate = doc.Add(Box(new Vector3(1, 0, 1), new Vector3(3, 2, 5)));
        var sel = new Selection();
        sel.Add(ElementRef.Object(crate.Id));
        var targets = OriginOps.Targets(doc, sel);
        Assert.Equal(1, OriginOps.Place(doc, targets, OriginPlacement.BottomCentre));
        Assert.Equal(ObjectOrigin.At(new Vector3(2, 0, 3)), crate.Origin);
        OriginOps.Place(doc, targets, OriginPlacement.Centre);
        Assert.Equal(new Vector3(2, 1, 3), crate.Origin!.Value.Position);
        Assert.Equal(1, OriginOps.Clear(targets));
        Assert.Null(crate.Origin);
    }

    [Fact]
    public void A_turned_origin_measures_bottom_along_its_own_axes()
    {
        var doc = new Document();
        var crate = doc.Add(Box(new Vector3(-1, 0, -0.5f), new Vector3(1, 1, 0.5f)));
        // Lay the crate on its side (90° about Z around the world origin), origin turning with it.
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        for (int i = 0; i < crate.Positions.Count; i++) crate.Positions[i] = Vector3.Transform(crate.Positions[i], turn);
        crate.Origin = new ObjectOrigin(Vector3.Zero, turn);
        var sel = new Selection();
        sel.Add(ElementRef.Object(crate.Id));
        OriginOps.Place(doc, OriginOps.Targets(doc, sel), OriginPlacement.BottomCentre);
        // Its own bottom (the face it was built on, now standing in the x = 0 plane), not the world-lowest side.
        AssertNear(Vector3.Zero, crate.Origin!.Value.Position);
        Assert.Equal(turn, crate.Origin!.Value.Rotation);
    }

    [Fact]
    public void A_mesh_origin_is_the_pivot_and_moves_and_turns_with_it_in_one_undo_step()
    {
        var doc = new Document();
        var crate = doc.Add(Box(new Vector3(0, 0, 0), new Vector3(2, 1, 1)));
        crate.Origin = ObjectOrigin.At(new Vector3(0, 0, 0)); // a corner, like a hinge
        var session = SessionWith(doc, crate.Id);
        Assert.Equal(ObjectOrigin.At(Vector3.Zero), OriginOps.UnitOrigin(session.Doc, session.Sel));

        var g = TransformGesture.Start(session, "Rotate")!;
        Assert.Equal(Vector3.Zero, g.Pivot);
        var m = TransformGesture.About(g.Pivot, Matrix4x4.CreateRotationY(MathF.PI / 2)) * Matrix4x4.CreateTranslation(5, 0, 0);
        g.Apply(m, rigid: true);
        g.Apply(m, rigid: true); // absolute, not accumulated
        g.Commit();

        var live = session.Doc.Find(crate.Id)!.Origin!.Value;
        AssertNear(new Vector3(5, 0, 0), live.Position);
        AssertNear(Vector3.Transform(Vector3.UnitX, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2)), live.Axes.X);

        Assert.True(session.Undo());
        Assert.Equal(ObjectOrigin.At(Vector3.Zero), session.Doc.Find(crate.Id)!.Origin);
    }

    [Fact]
    public void Component_edits_leave_the_origin_where_it_is()
    {
        var doc = new Document();
        var crate = doc.Add(Box(new Vector3(0, 0, 0), new Vector3(1, 1, 1)));
        crate.Origin = ObjectOrigin.At(new Vector3(0.5f, 0, 0.5f));
        var session = SessionWith(doc);
        session.Sel.ConvertTo(SelectMode.Face, session.Doc);
        session.Sel.Add(ElementRef.Face(crate.Id, 0));
        var g = TransformGesture.Start(session, "Move")!;
        g.Apply(Matrix4x4.CreateTranslation(0, 3, 0), rigid: true);
        g.Commit();
        Assert.Equal(ObjectOrigin.At(new Vector3(0.5f, 0, 0.5f)), session.Doc.Find(crate.Id)!.Origin);
    }

    [Fact]
    public void A_group_origin_moves_only_when_the_whole_group_does()
    {
        var doc = new Document();
        var a = doc.Add(Box(Vector3.Zero, Vector3.One));
        var b = doc.Add(Box(new Vector3(2, 0, 0), new Vector3(3, 1, 1)));
        var group = GroupOps.Create(doc, [a.Id, b.Id])!;
        group.Origin = ObjectOrigin.At(new Vector3(1.5f, 0, 0.5f));

        // The whole group: it is the pivot and it moves.
        var session = SessionWith(doc, a.Id, b.Id);
        Assert.Equal(new Vector3(1.5f, 0, 0.5f), OriginOps.UnitOrigin(session.Doc, session.Sel)?.Position);
        var g = TransformGesture.Start(session, "Move")!;
        Assert.Equal(new Vector3(1.5f, 0, 0.5f), g.Pivot);
        g.Apply(Matrix4x4.CreateTranslation(0, 0, 4), rigid: true);
        g.Commit();
        Assert.Equal(new Vector3(1.5f, 0, 4.5f), session.Doc.FindGroup(group.Id)!.Origin!.Value.Position);

        // One member alone (Select whole groups off): the group's origin stays.
        session.Sel.Clear();
        session.Sel.Add(ElementRef.Object(a.Id));
        Assert.Null(OriginOps.UnitOrigin(session.Doc, session.Sel));
        g = TransformGesture.Start(session, "Move")!;
        g.Apply(Matrix4x4.CreateTranslation(0, 7, 0), rigid: true);
        g.Commit();
        Assert.Equal(new Vector3(1.5f, 0, 4.5f), session.Doc.FindGroup(group.Id)!.Origin!.Value.Position);
    }

    [Fact]
    public void Without_origins_the_pivot_stays_the_bounds_centre()
    {
        var doc = new Document();
        var a = doc.Add(Box(Vector3.Zero, new Vector3(2, 2, 2)));
        var session = SessionWith(doc, a.Id);
        Assert.Null(OriginOps.UnitOrigin(session.Doc, session.Sel));
        Assert.Equal(Vector3.One, TransformGesture.Start(session, "Move")!.Pivot);
        session.Cancel();
    }

    [Fact]
    public void A_mesh_entity_pivots_on_its_own_origin()
    {
        var doc = new Document();
        var a = doc.Add(Box(new Vector3(2, 0, 0), new Vector3(3, 2, 0.1f)));
        var door = MeshEntityOps.Tie(doc, [a.Id], "door")!;
        door.Position = new Vector3(2, 0, 0); // the hinge
        door.Rotation = new Vector3(0, 30, 0);
        var session = SessionWith(doc, a.Id, door.Id);
        var o = OriginOps.UnitOrigin(session.Doc, session.Sel)!.Value;
        Assert.Equal(new Vector3(2, 0, 0), o.Position);
        Assert.Equal(EntityOps.Orientation(door), o.Rotation);
        // Its meshes have no origin of their own to set.
        Assert.Empty(OriginOps.Targets(session.Doc, session.Sel));
    }

    [Fact]
    public void Origins_save_and_load_and_older_files_have_none()
    {
        var doc = new Document();
        var a = doc.Add(Box(Vector3.Zero, Vector3.One));
        var b = doc.Add(Box(new Vector3(2, 0, 0), new Vector3(3, 1, 1)));
        var plain = doc.Add(Box(new Vector3(5, 0, 0), new Vector3(6, 1, 1)));
        a.Origin = new ObjectOrigin(new Vector3(0.5f, 0, 0.5f), EntityOps.Orientation(new Vector3(0, 45, 0)));
        var group = GroupOps.Create(doc, [b.Id])!;
        group.Origin = ObjectOrigin.At(new Vector3(2.5f, 0, 0.5f));

        var json = DocumentSerializer.Serialize(doc);
        var back = DocumentSerializer.Deserialize(json);
        var o = back.Find(a.Id)!.Origin!.Value;
        Assert.Equal(new Vector3(0.5f, 0, 0.5f), o.Position);
        AssertNear(a.Origin.Value.Axes.X, o.Axes.X);
        Assert.Equal(group.Origin, back.FindGroup(group.Id)!.Origin);
        Assert.Null(back.Find(plain.Id)!.Origin);
        Assert.Equal(2, json.Split("\"origin\"").Length - 1); // the mesh's and the group's; none written for the plain mesh
    }

    [Fact]
    public void A_mesh_with_an_origin_exports_at_it_with_vertices_relative_to_it()
    {
        var doc = new Document { ExportScale = 2 };
        var crate = doc.Add(Box(new Vector3(4, 0, 4), new Vector3(6, 1, 5)));
        var turn = EntityOps.Orientation(new Vector3(0, 90, 0));
        crate.Origin = new ObjectOrigin(new Vector3(5, 0, 4.5f), turn);
        var glb = GlbExporter.Build(doc, new MaterialLibrary());
        var json = Glb.Json(glb);
        var node = json["nodes"]!.AsArray().Single(n => n!["name"]!.GetValue<string>().StartsWith($"block_{crate.Id}"))!;
        var t = node["translation"]!.AsArray().Select(x => x!.GetValue<float>()).ToArray();
        var r = node["rotation"]!.AsArray().Select(x => x!.GetValue<float>()).ToArray();
        Assert.Equal(new Vector3(10, 0, 9), new Vector3(t[0], t[1], t[2]));
        var q = new Quaternion(r[0], r[1], r[2], r[3]);

        var mesh = json["meshes"]![node["mesh"]!.GetValue<int>()]!;
        var local = Glb.Vec3(glb, mesh["primitives"]![0]!["attributes"]!["POSITION"]!.GetValue<int>());
        var world = local.Select(p => Vector3.Transform(p, q) + new Vector3(t[0], t[1], t[2])).ToList();
        var expected = crate.Positions.Select(p => p * 2).ToList();
        Assert.All(world, p => Assert.Contains(expected, e => Vector3.Distance(p, e) < 1e-4f));

        // Re-importing puts it back in world space.
        var lib = new MaterialLibrary();
        var target = new Document();
        GltfImporter.Parse(glb, null, lib, "dev_a").AddTo(target);
        var min = target.Meshes.SelectMany(m => m.Positions).Aggregate(new Vector3(float.MaxValue), Vector3.Min);
        AssertNear(new Vector3(8, 0, 8), min, 1e-3f);
    }

    [Fact]
    public void Duplicates_and_undo_keep_origins()
    {
        var doc = new Document();
        var a = doc.Add(Box(Vector3.Zero, Vector3.One));
        var b = doc.Add(Box(new Vector3(2, 0, 0), new Vector3(3, 1, 1)));
        a.Origin = ObjectOrigin.At(new Vector3(0.5f, 0, 0.5f));
        var group = GroupOps.Create(doc, [b.Id])!;
        group.Origin = ObjectOrigin.At(new Vector3(2.5f, 0, 0.5f));
        var copies = SceneOps.Duplicate(doc, [a.Id, b.Id]);
        Assert.Contains(copies, id => doc.Find(id)?.Origin == a.Origin);
        Assert.Contains(doc.Groups, g => g != group && g.Origin == group.Origin);

        var session = SessionWith(doc, a.Id);
        session.Do("Clear", [a.Id], () => OriginOps.Clear(OriginOps.Targets(session.Doc, session.Sel)));
        Assert.Null(session.Doc.Find(a.Id)!.Origin);
        Assert.True(session.Undo());
        Assert.Equal(ObjectOrigin.At(new Vector3(0.5f, 0, 0.5f)), session.Doc.Find(a.Id)!.Origin);
    }
}
