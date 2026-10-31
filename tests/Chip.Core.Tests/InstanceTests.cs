using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.IO;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Core.Tests;

/// <summary>Linked copies: editing any copy of a definition updates the others, live, in one undo step.</summary>
public class InstanceTests
{
    static PolyMesh Box(Vector3 min, Vector3 max) => MeshOps.CreateBox(min, max, "dev_a", TextureMapping.Default);

    static void AssertNear(Vector3 expected, Vector3 actual, float tol = 1e-4f) =>
        Assert.True(Vector3.Distance(expected, actual) < tol, $"expected {expected}, got {actual}");

    static readonly Quaternion Quarter = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);

    /// <summary>A two-mesh container made into a definition, plus a second copy turned a quarter and moved to x = 10.</summary>
    static (Session Session, Group A, Group B) TwoCopies()
    {
        var doc = new Document();
        var body = doc.Add(Box(new Vector3(-1, 0, -0.5f), new Vector3(1, 1, 0.5f)));
        var door = doc.Add(Box(new Vector3(1, 0, -0.5f), new Vector3(1.1f, 1, 0.5f)));
        var session = new Session();
        session.Reset(doc, null);
        Group? a = null, b = null;
        session.Do("Make", [body.Id, door.Id], () =>
        {
            a = InstanceOps.Make(session.Doc, [body.Id, door.Id], "container")!;
            b = InstanceOps.Place(session.Doc, session.Doc.FindDefinition(a.Instance)!, new ObjectOrigin(new Vector3(10, 0, 0), Quarter));
        });
        return (session, session.Doc.FindGroup(a!.Id)!, session.Doc.FindGroup(b!.Id)!);
    }

    static List<PolyMesh> Meshes(Session s, Group g) => s.Doc.FindGroup(g.Id)!.Members.Select(s.Doc.Find).OfType<PolyMesh>().ToList();

    /// <summary>A copy's mesh positions in its own space, part by part.</summary>
    static List<Vector3[]> Local(Session s, Group g)
    {
        var copy = s.Doc.FindGroup(g.Id)!;
        return Meshes(s, copy).OrderBy(m => m.Part).Select(m => InstanceOps.ToLocal(m, copy.Origin!.Value).Positions.ToArray()).ToList();
    }

    static void AssertSameShape(Session s, Group a, Group b)
    {
        var la = Local(s, a);
        var lb = Local(s, b);
        Assert.Equal(la.Count, lb.Count);
        for (int i = 0; i < la.Count; i++)
        {
            Assert.Equal(la[i].Length, lb[i].Length);
            for (int j = 0; j < la[i].Length; j++) AssertNear(la[i][j], lb[i][j]);
        }
    }

    [Fact]
    public void Making_an_instance_makes_a_definition_and_a_copy_at_the_bottom_centre()
    {
        var (s, a, b) = TwoCopies();
        var def = Assert.Single(s.Doc.Definitions);
        Assert.Equal("container", def.Name);
        Assert.Equal(2, def.Parts.Count);
        Assert.Equal(def.Id, a.Instance);
        AssertNear(new Vector3(0.05f, 0, 0), a.Origin!.Value.Position);
        Assert.All(Meshes(s, a), m => Assert.NotEqual(0, m.Part));
        Assert.Equal(2, Meshes(s, b).Count);
        AssertSameShape(s, a, b);
        // The definition's parts sit around its origin.
        var min = def.Parts.SelectMany(p => p.Mesh.Positions).Aggregate(new Vector3(float.MaxValue), Vector3.Min);
        AssertNear(new Vector3(-1.05f, 0, -0.5f), min);
    }

    [Fact]
    public void Entities_and_copies_cant_be_made_into_a_definition()
    {
        var (s, a, _) = TwoCopies();
        Assert.Null(InstanceOps.Make(s.Doc, a.Members));
        var e = s.Doc.Add(EntityClasses.Create("info_player_start", Vector3.Zero));
        var m = s.Doc.Add(Box(Vector3.Zero, Vector3.One));
        Assert.Null(InstanceOps.Make(s.Doc, [e.Id, m.Id]));
    }

    [Fact]
    public void Editing_vertices_of_one_copy_updates_the_other_live_and_undo_restores_both()
    {
        var (s, a, b) = TwoCopies();
        var body = Meshes(s, a).Single(m => m.Part == 1);
        int top = Enumerable.Range(0, body.Positions.Count).First(i => body.Positions[i].Y > 0.5f);
        var before = Local(s, b);

        s.Sel.ConvertTo(SelectMode.Vertex, s.Doc);
        s.Sel.Add(ElementRef.Vertex(body.Id, top));
        var g = TransformGesture.Start(s, "Move")!;
        g.Apply(Matrix4x4.CreateTranslation(0, 2, 0), rigid: true);
        // Live: the other copy already follows, mid-drag.
        AssertSameShape(s, a, b);
        Assert.NotEqual(before[0][top], Local(s, b)[0][top]);
        g.Commit();
        AssertSameShape(s, a, b);
        // In the turned copy the vertex rose in world Y too (up is up in both).
        var copyB = s.Doc.FindGroup(b.Id)!;
        var bBody = Meshes(s, copyB).Single(m => m.Part == 1);
        Assert.Equal(3, bBody.Positions[top].Y, 3);

        Assert.True(s.Undo());
        Assert.Equal(1, Meshes(s, s.Doc.FindGroup(b.Id)!).Single(m => m.Part == 1).Positions[top].Y, 3);
        AssertSameShape(s, a, b);
        Assert.True(s.Redo());
        Assert.Equal(3, Meshes(s, s.Doc.FindGroup(b.Id)!).Single(m => m.Part == 1).Positions[top].Y, 3);
    }

    [Fact]
    public void Moving_and_turning_a_whole_copy_leaves_the_others_alone()
    {
        var (s, a, b) = TwoCopies();
        var bBefore = Meshes(s, b).Select(m => m.Positions.ToArray()).ToList();
        foreach (var id in a.Members) s.Sel.Add(ElementRef.Object(id));
        var g = TransformGesture.Start(s, "Rotate")!;
        g.Apply(TransformGesture.About(g.Pivot, Matrix4x4.CreateRotationY(0.7f)) * Matrix4x4.CreateTranslation(3, 1, -2), rigid: true);
        g.Commit();
        var bAfter = Meshes(s, b).Select(m => m.Positions.ToArray()).ToList();
        Assert.Equal(bBefore, bAfter);
        AssertSameShape(s, a, b);
    }

    [Fact]
    public void Edits_of_any_copy_go_to_every_copy_and_back()
    {
        var (s, a, b) = TwoCopies();
        // Edit B (the turned one) by pushing its door's faces out; then A must match.
        var door = Meshes(s, b).Single(m => m.Part == 2);
        s.Do("Flip", [door.Id], () => MeshOps.FlipFaces(door, Enumerable.Range(0, door.Faces.Count).ToList()));
        var aDoor = Meshes(s, a).Single(m => m.Part == 2);
        Assert.Equal(door.Faces[0].Verts, aDoor.Faces[0].Verts);
        AssertSameShape(s, a, b);
    }

    [Fact]
    public void Deleting_a_part_from_one_copy_deletes_it_from_all_and_adding_a_mesh_adds_it_to_all()
    {
        var (s, a, b) = TwoCopies();
        var door = Meshes(s, a).Single(m => m.Part == 2);
        s.Do("Delete", () => SceneOps.Delete(s.Doc, [door.Id]));
        Assert.Single(Meshes(s, b));
        Assert.Single(s.Doc.Definitions[0].Parts);

        // A new mesh put into copy A appears in B, placed the same relative to B's origin.
        var lamp = s.Doc.Add(Box(new Vector3(0, 1, 0), new Vector3(0.2f, 1.2f, 0.2f)));
        s.Do("Into group", () => GroupOps.MoveTo(s.Doc, lamp.Id, s.Doc.FindGroup(a.Id)));
        Assert.Equal(2, Meshes(s, b).Count);
        AssertSameShape(s, a, b);
        Assert.Equal(2, s.Doc.Definitions[0].Parts.Count);
    }

    [Fact]
    public void Grouping_meshes_inside_a_copy_keeps_them_in_it()
    {
        var (s, a, b) = TwoCopies();
        var members = s.Doc.FindGroup(a.Id)!.Members.ToList();
        s.Do("Group", () => GroupOps.Create(s.Doc, members));
        var copyA = s.Doc.FindGroup(a.Id)!;
        Assert.Equal(2, InstanceOps.MeshIds(s.Doc, copyA).Count);
        Assert.Same(copyA, InstanceOps.CopyOf(s.Doc, members[0]));
        Assert.Equal(2, Meshes(s, b).Count);
        Assert.Equal(2, s.Doc.Definitions[0].Parts.Count);
    }

    [Fact]
    public void Turning_a_copy_into_a_folder_isnt_possible_while_linked_so_make_unique_first()
    {
        var (s, a, _) = TwoCopies();
        s.Do("Folder", () => { InstanceOps.MakeUnique(s.Doc, [s.Doc.FindGroup(a.Id)!]); s.Doc.FindGroup(a.Id)!.Folder = true; });
        Assert.False(s.Doc.FindGroup(a.Id)!.IsInstance);
        Assert.Single(InstanceOps.Copies(s.Doc, s.Doc.Definitions[0].Id));
    }

    [Fact]
    public void Duplicating_a_copy_makes_another_linked_copy()
    {
        var (s, a, b) = TwoCopies();
        List<int> copies = [];
        s.Do("Duplicate", () => copies = SceneOps.Duplicate(s.Doc, a.Members));
        var c = InstanceOps.CopyOf(s.Doc, copies[0])!;
        Assert.Equal(a.Instance, c.Instance);
        Assert.Equal(3, InstanceOps.Copies(s.Doc, a.Instance).Count);

        var mesh = Meshes(s, c).Single(m => m.Part == 1);
        s.Do("Flip", [mesh.Id], () => MeshOps.FlipFaces(mesh, [0]));
        Assert.Equal(mesh.Faces[0].Verts, Meshes(s, b).Single(m => m.Part == 1).Faces[0].Verts);
    }

    [Fact]
    public void Make_unique_breaks_the_link_and_the_last_copy_takes_the_definition_with_it()
    {
        var (s, a, b) = TwoCopies();
        s.Do("Unique", () => InstanceOps.MakeUnique(s.Doc, [s.Doc.FindGroup(b.Id)!]));
        var copyB = s.Doc.FindGroup(b.Id)!;
        Assert.False(copyB.IsInstance);
        Assert.All(Meshes(s, copyB), m => Assert.Equal(0, m.Part));
        var bBody = Meshes(s, copyB)[0];
        var before = bBody.Positions.ToArray();
        var aBody = Meshes(s, a).Single(m => m.Part == 1);
        s.Do("Flip", [aBody.Id], () => MeshOps.FlipFaces(aBody, [0]));
        Assert.Equal(before, Meshes(s, copyB)[0].Positions.ToArray());

        s.Do("Unique", () => InstanceOps.MakeUnique(s.Doc, [s.Doc.FindGroup(a.Id)!]));
        Assert.Empty(s.Doc.Definitions);
    }

    [Fact]
    public void Deleting_every_copy_drops_the_definition_and_undo_brings_it_back()
    {
        var (s, a, b) = TwoCopies();
        s.Do("Delete", () => SceneOps.Delete(s.Doc, [.. a.Members, .. b.Members]));
        Assert.Empty(s.Doc.Definitions);
        Assert.True(s.Undo());
        Assert.Single(s.Doc.Definitions);
        // ...and the link still works.
        var aBody = Meshes(s, a).Single(m => m.Part == 1);
        s.Do("Flip", [aBody.Id], () => MeshOps.FlipFaces(aBody, [0]));
        Assert.Equal(aBody.Faces[0].Verts, Meshes(s, b).Single(m => m.Part == 1).Faces[0].Verts);
    }

    [Fact]
    public void Copies_save_and_load_and_stay_linked()
    {
        var (s, a, b) = TwoCopies();
        var doc = DocumentSerializer.Deserialize(DocumentSerializer.Serialize(s.Doc));
        var def = Assert.Single(doc.Definitions);
        Assert.Equal(2, def.Parts.Count);
        Assert.Equal(def.Id, doc.FindGroup(b.Id)!.Instance);
        var session = new Session();
        session.Reset(doc, null);
        var aBody = Meshes(session, a).Single(m => m.Part == 1);
        int top = Enumerable.Range(0, aBody.Positions.Count).First(i => aBody.Positions[i].Y > 0.5f);
        session.Do("Raise", [aBody.Id], () => aBody.Positions[top] += new Vector3(0, 1, 0));
        AssertSameShape(session, a, b);
    }

    [Fact]
    public void Copies_export_as_nodes_at_their_origins_sharing_one_mesh_per_part()
    {
        var (s, a, b) = TwoCopies();
        var glb = GlbExporter.Build(s.Doc, new MaterialLibrary());
        var json = Glb.Json(glb);
        var partNodes = json["nodes"]!.AsArray().Where(n => n!["extras"]?["chip_part"] != null).ToList();
        Assert.Equal(4, partNodes.Count);
        // Two glTF meshes for two parts, whatever the number of copies.
        Assert.Equal(2, partNodes.Select(n => n!["mesh"]!.GetValue<int>()).Distinct().Count());
        Assert.All(partNodes, n => Assert.Equal("container", n!["extras"]!["chip_instance"]!.GetValue<string>()));

        // Copy B's node puts the shared mesh where B's meshes are.
        var bBody = Meshes(s, b).Single(m => m.Part == 1);
        var node = partNodes.Single(n => n!["extras"]!["chip_id"]!.GetValue<int>() == bBody.Id)!;
        var t = node["translation"]!.AsArray().Select(x => x!.GetValue<float>()).ToArray();
        var r = node["rotation"]!.AsArray().Select(x => x!.GetValue<float>()).ToArray();
        var mesh = json["meshes"]![node["mesh"]!.GetValue<int>()]!;
        var local = Glb.Vec3(glb, mesh["primitives"]![0]!["attributes"]!["POSITION"]!.GetValue<int>());
        var world = local.Select(p => Vector3.Transform(p, new Quaternion(r[0], r[1], r[2], r[3])) + new Vector3(t[0], t[1], t[2]));
        Assert.All(world, p => Assert.Contains(bBody.Positions, q => Vector3.Distance(p, q) < 1e-3f));
    }

    [Fact]
    public void Textures_look_the_same_on_turned_copies()
    {
        var (s, a, b) = TwoCopies();
        var aBody = Meshes(s, a).Single(m => m.Part == 1);
        var bBody = Meshes(s, b).Single(m => m.Part == 1);
        for (int f = 0; f < aBody.Faces.Count; f++)
            for (int i = 0; i < aBody.Faces[f].Verts.Length; i++)
            {
                var ua = aBody.Faces[f].UV(i, aBody.Positions[aBody.Faces[f].Verts[i]], 128, 128);
                var ub = bBody.Faces[f].UV(i, bBody.Positions[bBody.Faces[f].Verts[i]], 128, 128);
                Assert.True(Vector2.Distance(ua, ub) < 1e-3f, $"face {f} corner {i}: {ua} vs {ub}");
            }
    }
}
