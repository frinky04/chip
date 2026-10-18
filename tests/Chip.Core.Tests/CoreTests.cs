using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Chip.Core.Geometry;
using Chip.Core.IO;
using Chip.Core.Picking;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Core.Tests;

public class CoreTests
{
    static PolyMesh Box(Vector3 min, Vector3 max) => MeshOps.CreateBox(min, max, "dev_measurewall01a", TextureMapping.Default);

    static bool IsClosedManifold(PolyMesh m) =>
        m.Faces.SelectMany(f => f.Edges()).GroupBy(e => e).All(g => g.Count() == 2);

    static Session SessionWith(params PolyMesh[] meshes)
    {
        var doc = new Document();
        foreach (var m in meshes) doc.Add(m);
        var s = new Session();
        s.Reset(doc, null);
        return s;
    }

    [Fact]
    public void Box_is_closed_with_outward_normals()
    {
        var m = Box(Vector3.Zero, new Vector3(64, 128, 32));
        Assert.Equal(6, m.Faces.Count);
        Assert.True(IsClosedManifold(m));
        var center = new Vector3(32, 64, 16);
        for (int f = 0; f < 6; f++)
            Assert.True(Vector3.Dot(m.FaceNormal(f), m.FaceCenter(f) - center) > 0);
    }

    [Fact]
    public void Extruding_top_face_keeps_mesh_closed_and_moves_only_the_cap()
    {
        var m = Box(Vector3.Zero, new Vector3(64, 64, 64));
        int top = Enumerable.Range(0, 6).Single(f => m.FaceNormal(f).Y > 0.9f);
        var (move, sides) = MeshOps.ExtrudeFaces(m, [top]);
        Assert.Equal(4, move.Count);
        Assert.Equal(4, sides.Count);
        foreach (var v in move) m.Positions[v] += new Vector3(0, 32, 0);
        MeshOps.AlignToGrid(m, sides);
        Assert.True(IsClosedManifold(m));
        Assert.Equal(10, m.Faces.Count);
        Assert.Equal(96, m.Bounds().Max.Y);
        var center = new Vector3(32, 48, 32);
        foreach (var f in sides) Assert.True(Vector3.Dot(m.FaceNormal(f), m.FaceCenter(f) - center) > 0);
    }

    [Fact]
    public void A_512px_texture_tiles_every_metre_and_a_128px_one_every_quarter_metre()
    {
        var map = TextureMapping.AlignToGrid(Vector3.UnitZ, TextureMapping.Default);
        var a = map.Project(new Vector3(0, 1, 0), 512, 512);
        var b = map.Project(new Vector3(1, 0, 0), 512, 512);
        Assert.Equal(1f, map.Project(new Vector3(0.25f, 0, 0), 128, 128).X, 4);
        Assert.Equal(-1f, a.Y, 4); // V points down: one tile up
        Assert.Equal(1f, b.X, 4);  // facing +Z, U runs +X (unmirrored)
    }

    [Fact]
    public void Fit_maps_face_to_unit_square()
    {
        var m = Box(Vector3.Zero, new Vector3(200, 100, 8));
        int front = Enumerable.Range(0, 6).Single(f => m.FaceNormal(f).Z > 0.9f);
        var map = m.Faces[front].Map.FitTo(m.FacePoints(front), 512, 512);
        var uvs = m.FacePoints(front).Select(p => map.Project(p, 512, 512)).ToList();
        Assert.Equal(0f, uvs.Min(u => u.X), 4);
        Assert.Equal(1f, uvs.Max(u => u.X), 4);
        Assert.Equal(0f, uvs.Min(u => u.Y), 4);
        Assert.Equal(1f, uvs.Max(u => u.Y), 4);
    }

    [Fact]
    public void Object_move_keeps_texture_locked()
    {
        var s = SessionWith(Box(Vector3.Zero, new Vector3(64, 64, 64)));
        var mesh = s.Doc.Meshes[0];
        int f = 0;
        var before = mesh.Faces[f].Map.Project(mesh.Positions[mesh.Faces[f].Verts[0]], 512, 512);
        s.Sel.Add(ElementRef.Object(mesh.Id));
        var g = TransformGesture.Start(s, "Move")!;
        var m = TransformGesture.About(g.Pivot, Matrix4x4.CreateRotationY(MathF.PI / 2)) * Matrix4x4.CreateTranslation(40, 8, -16);
        g.Apply(m, rigid: true);
        g.Commit();
        mesh = s.Doc.Meshes[0];
        var after = mesh.Faces[f].Map.Project(mesh.Positions[mesh.Faces[f].Verts[0]], 512, 512);
        Assert.Equal(before.X, after.X, 3);
        Assert.Equal(before.Y, after.Y, 3);
    }

    [Fact]
    public void Gesture_cancel_and_undo_restore_state()
    {
        var s = SessionWith(Box(Vector3.Zero, new Vector3(64, 64, 64)));
        int id = s.Doc.Meshes[0].Id;
        s.Sel.ConvertTo(SelectMode.Face, s.Doc);
        int top = Enumerable.Range(0, 6).Single(f => s.Doc.Meshes[0].FaceNormal(f).Y > 0.9f);
        s.Sel.Add(ElementRef.Face(id, top));

        var g = TransformGesture.Start(s, "Extrude", extrude: true)!;
        g.Apply(Matrix4x4.CreateTranslation(0, 16, 0), true);
        g.Cancel();
        Assert.Equal(6, s.Doc.Meshes[0].Faces.Count);
        Assert.Null(s.UndoLabel);

        g = TransformGesture.Start(s, "Extrude", extrude: true)!;
        g.Apply(Matrix4x4.CreateTranslation(0, 16, 0), true);
        g.Commit();
        Assert.Equal(10, s.Doc.Meshes[0].Faces.Count);
        Assert.True(s.Undo());
        Assert.Equal(6, s.Doc.Meshes[0].Faces.Count);
        Assert.Contains(ElementRef.Face(id, top), s.Sel.Items);
        Assert.True(s.Redo());
        Assert.Equal(10, s.Doc.Meshes[0].Faces.Count);
    }

    [Fact]
    public void Unmoved_extrude_leaves_no_trace()
    {
        var s = SessionWith(Box(Vector3.Zero, new Vector3(64, 64, 64)));
        s.Sel.ConvertTo(SelectMode.Face, s.Doc);
        s.Sel.Add(ElementRef.Face(s.Doc.Meshes[0].Id, 3));
        TransformGesture.Start(s, "Extrude", extrude: true)!.Commit();
        Assert.Equal(6, s.Doc.Meshes[0].Faces.Count);
        Assert.Null(s.UndoLabel);
    }

    [Fact]
    public void Edge_selection_converts_to_adjacent_faces()
    {
        var s = SessionWith(Box(Vector3.Zero, new Vector3(64, 64, 64)));
        var mesh = s.Doc.Meshes[0];
        s.Sel.ConvertTo(SelectMode.Edge, s.Doc);
        s.Sel.Add(ElementRef.EdgeOf(mesh.Id, new Edge(0, 2))); // vertical edge at min x/min z
        s.Sel.ConvertTo(SelectMode.Face, s.Doc);
        Assert.Equal(2, s.Sel.Count);
        s.Sel.ConvertTo(SelectMode.Vertex, s.Doc);
        Assert.Equal(6, s.Sel.Count); // two quads share an edge: 6 distinct verts
    }

    [Fact]
    public void Raycast_hits_front_face()
    {
        var doc = new Document();
        doc.Add(Box(Vector3.Zero, new Vector3(64, 64, 64)));
        var hit = Picker.RaycastFace(doc, new Ray(new Vector3(32, 32, 200), -Vector3.UnitZ));
        Assert.NotNull(hit);
        Assert.Equal(136f, hit.Value.Distance, 3);
        Assert.True(hit.Value.Normal.Z > 0.9f);
    }

    [Fact]
    public void Backface_mode_picks_a_room_interior_from_outside()
    {
        var doc = new Document();
        doc.Add(Box(Vector3.Zero, new Vector3(4, 4, 4)));
        // From inside the box every face points away from the eye, so normally nothing is hit.
        var inside = new Ray(new Vector3(2, 2, 2), -Vector3.UnitZ);
        Assert.Null(Picker.RaycastFace(doc, inside));
        var hit = Picker.RaycastFace(doc, inside, backfaces: true);
        Assert.NotNull(hit);
        Assert.Equal(2f, hit.Value.Distance, 3);
        Assert.True(hit.Value.Normal.Z < -0.9f); // the -Z wall
        // From outside, the nearest hit stays the front face.
        var outside = Picker.RaycastFace(doc, new Ray(new Vector3(2, 2, 10), -Vector3.UnitZ), backfaces: true);
        Assert.Equal(6f, outside!.Value.Distance, 3);
    }

    [Fact]
    public void Concave_face_triangulates_to_n_minus_2_triangles()
    {
        var m = new PolyMesh();
        m.Positions.AddRange([new(0, 0, 0), new(0, 0, 2), new(1, 0, 2), new(1, 0, 1), new(2, 0, 1), new(2, 0, 0)]); // L shape
        m.Faces.Add(new Face([0, 1, 2, 3, 4, 5], "x", TextureMapping.Default));
        var tris = new List<int>();
        Triangulator.Triangulate(m, 0, tris);
        Assert.Equal(12, tris.Count);
        float area = 0;
        for (int i = 0; i < tris.Count; i += 3)
            area += Vector3.Cross(m.Positions[tris[i + 1]] - m.Positions[tris[i]], m.Positions[tris[i + 2]] - m.Positions[tris[i]]).Length() / 2;
        Assert.Equal(3f, area, 4);
    }

    [Fact]
    public void Document_round_trips()
    {
        var doc = new Document { ExportScale = 100f, ExportCollision = false };
        var box = doc.Add(Box(new Vector3(-8, 0, -8), new Vector3(8, 16, 8)));
        box.Hidden = true;
        box.Faces[2].Map.Rotation = 15;
        var back = DocumentSerializer.Deserialize(DocumentSerializer.Serialize(doc));
        Assert.Equal(100f, back.ExportScale);
        Assert.False(back.ExportCollision);
        var m = Assert.Single(back.Meshes);
        Assert.True(m.Hidden);
        Assert.Equal(box.Positions, m.Positions);
        Assert.Equal(15, m.Faces[2].Map.Rotation);
        Assert.Equal(box.Faces[4].Verts, m.Faces[4].Verts);
        Assert.Equal(doc.NextId, back.NextId);
    }

    [Fact]
    public void Glb_has_valid_header_scaled_bounds_and_collision_names()
    {
        var doc = new Document { ExportScale = 100f }; // metres -> centimetres
        doc.Export.BakeCollision = false; // the per-mesh collider path (baking is covered in CollisionBakeExportTests)
        doc.Add(Box(Vector3.Zero, new Vector3(2.54f, 1, 1)));
        var lib = new MaterialLibrary();
        var glb = GlbExporter.Build(doc, lib);
        Assert.Equal(0x46546C67u, BitConverter.ToUInt32(glb, 0));
        Assert.Equal((uint)glb.Length, BitConverter.ToUInt32(glb, 8));
        int jsonLen = (int)BitConverter.ToUInt32(glb, 12);
        var json = JsonNode.Parse(Encoding.UTF8.GetString(glb, 20, jsonLen))!;
        Assert.EndsWith("-col", json["nodes"]![0]!["name"]!.GetValue<string>());
        var max = json["accessors"]![0]!["max"]!.AsArray();
        Assert.Equal(254f, max[0]!.GetValue<float>(), 2);
        Assert.Equal(12 * 3, json["accessors"]![3]!["count"]!.GetValue<int>()); // 6 quads -> 12 tris
    }
}
