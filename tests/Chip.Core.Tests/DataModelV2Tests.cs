using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Chip.Core.Geometry;
using Chip.Core.IO;
using Chip.Core.Picking;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Core.Tests;

/// <summary>Schema v2: per-corner UVs, soft edges, entities, per-mesh lock/collision, and v1 compatibility.</summary>
public class DataModelV2Tests
{
    static PolyMesh Box() => MeshOps.CreateBox(Vector3.Zero, new Vector3(2, 2, 2), "m", TextureMapping.Default);

    [Fact]
    public void Explicit_uvs_override_projection_and_round_trip()
    {
        var doc = new Document();
        var box = doc.Add(Box());
        box.Faces[0].UVs = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        Assert.Equal(new Vector2(1, 1), box.Faces[0].UV(2, box.Positions[box.Faces[0].Verts[2]], 512, 512));
        var back = DocumentSerializer.Deserialize(DocumentSerializer.Serialize(doc));
        Assert.Equal(box.Faces[0].UVs, back.Meshes[0].Faces[0].UVs);
        Assert.Null(back.Meshes[0].Faces[1].UVs);
    }

    [Fact]
    public void Flip_and_collapse_keep_uvs_attached_to_their_corners()
    {
        var m = Box();
        var face = m.Faces[3]; // +Y
        face.UVs = face.Verts.Select((v, i) => new Vector2(i, 10 + i)).ToArray();
        var before = face.Verts.Zip(face.UVs).ToDictionary(p => p.First, p => p.Second);

        MeshOps.FlipFaces(m, [3]);
        Assert.All(face.Verts.Zip(face.UVs!), p => Assert.Equal(before[p.First], p.Second));

        // Drop one top vertex onto its neighbour: the face loses a corner, the rest keep their UVs.
        int v0 = face.Verts[0], v1 = face.Verts[1];
        m.Positions[v0] = m.Positions[v1];
        Assert.NotNull(MeshOps.CollapseCoincident(m, 1e-4f));
        var top = m.Faces.Single(f => f.UVs != null);
        Assert.Equal(3, top.Verts.Length);
        Assert.Equal(3, top.UVs!.Length);
    }

    [Fact]
    public void Soft_edges_smooth_corner_normals_and_survive_compaction()
    {
        var m = Box();
        var flat = Topology.CornerNormals(m);
        Assert.All(Enumerable.Range(0, 6), f => Assert.All(flat[f], n => Assert.Equal(m.FaceNormal(f), n)));

        foreach (var e in m.Edges()) m.SoftEdges.Add(e);
        var smooth = Topology.CornerNormals(m);
        var corner = smooth[3][0]; // a cube corner averages three equal faces
        Assert.Equal(1f / MathF.Sqrt(3), MathF.Abs(corner.X), 3);
        Assert.Equal(1f / MathF.Sqrt(3), MathF.Abs(corner.Y), 3);

        int before = m.SoftEdges.Count;
        MeshOps.DeleteFaces(m, [0]); // removes no vertices, but the deleted face's edges stay used by neighbours
        Assert.Equal(before, m.SoftEdges.Count);
        Assert.All(m.SoftEdges, e => Assert.Contains(e, m.Edges()));
    }

    [Fact]
    public void Entities_and_mesh_flags_round_trip()
    {
        var doc = new Document();
        var box = doc.Add(Box());
        box.Locked = true;
        box.Collision = CollisionMode.Only;
        box.SoftEdges.Add(box.Edges().First());
        var e = doc.Add(new Entity { ClassName = "info_player_start", Name = "spawn", Position = new(1, 0, 2), Rotation = new(0, 90, 0) });
        e.Properties["team"] = "blue";

        var back = DocumentSerializer.Deserialize(DocumentSerializer.Serialize(doc));
        var m = back.Meshes[0];
        Assert.True(m.Locked);
        Assert.Equal(CollisionMode.Only, m.Collision);
        Assert.Single(m.SoftEdges);
        var be = Assert.Single(back.Entities);
        Assert.Equal("info_player_start", be.ClassName);
        Assert.Equal(new Vector3(0, 90, 0), be.Rotation);
        Assert.Equal("blue", be.Properties["team"]);
        Assert.NotEqual(m.Id, be.Id);
        Assert.True(back.NextId > Math.Max(m.Id, be.Id));
    }

    [Fact]
    public void Version_1_files_still_load()
    {
        const string v1 = """
            { "version": 1, "exportScale": 1, "exportCollision": true, "nextId": 2,
              "meshes": [ { "id": 1, "name": "block", "hidden": false,
                "positions": [[0,0,0],[1,0,0],[1,0,1],[0,0,1]],
                "faces": [ { "verts": [0,3,2,1], "material": "m", "u": [1,0,0], "v": [0,0,1],
                             "shift": [0,0], "scale": [0.002,0.002], "rotation": 0 } ] } ] }
            """;
        var doc = DocumentSerializer.Deserialize(v1);
        var m = Assert.Single(doc.Meshes);
        Assert.False(m.Locked);
        Assert.Equal(CollisionMode.Default, m.Collision);
        Assert.Empty(m.SoftEdges);
        Assert.Empty(doc.Entities);
        Assert.Contains("\"version\": 2", DocumentSerializer.Serialize(doc));
    }

    [Fact]
    public void Locked_meshes_render_and_place_but_cannot_be_selected()
    {
        var doc = new Document();
        var box = doc.Add(Box());
        box.Locked = true;
        var ray = new Ray(new Vector3(1, 1, 10), -Vector3.UnitZ);
        Assert.NotNull(Picker.RaycastFace(doc, ray));
        Assert.Null(Picker.RaycastFace(doc, ray, selectable: true));
        var sel = new Selection();
        sel.SelectAll(doc);
        Assert.True(sel.IsEmpty);
    }

    [Fact]
    public void Export_uses_collision_modes_and_smoothed_normals()
    {
        var doc = new Document { ExportCollision = true };
        var a = doc.Add(Box());
        var b = doc.Add(Box());
        var c = doc.Add(Box());
        b.Collision = CollisionMode.None;
        c.Collision = CollisionMode.Only;
        foreach (var e in a.Edges()) a.SoftEdges.Add(e);
        var glb = GlbExporter.Build(doc, new MaterialLibrary());
        var json = JsonNode.Parse(Encoding.UTF8.GetString(glb, 20, (int)BitConverter.ToUInt32(glb, 12)))!;
        var names = json["nodes"]!.AsArray().Select(n => n!["name"]!.GetValue<string>()).ToList();
        Assert.EndsWith("-col", names[0]);
        Assert.DoesNotContain("-col", names[1]);
        Assert.EndsWith("-colonly", names[2]);
    }
}
