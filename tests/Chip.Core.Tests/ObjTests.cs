using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Geometry.Primitives;
using Chip.Core.IO;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Core.Tests;

public class ObjTests
{
    static MaterialLibrary Lib()
    {
        var lib = new MaterialLibrary();
        lib.Add(new MaterialInfo("dev_a", null, 512, 512));
        lib.Add(new MaterialInfo("dev_b", null, 256, 128));
        return lib;
    }

    static PolyMesh Box(Vector3 min, Vector3 max, string material = "dev_a") => MeshOps.CreateBox(min, max, material, TextureMapping.Default);

    [Fact]
    public void Obj_writer_output_parses_back_with_ngons_uvs_and_materials()
    {
        var lib = Lib();
        var doc = new Document { ExportScale = 2 };
        var box = doc.Add(Box(Vector3.Zero, new Vector3(1, 2, 3)));
        box.Faces[0].Material = "dev_b";
        box.Faces[3].UVs = [new(0, 0), new(0.5f, 0), new(0.5f, 0.25f), new(0, 0.25f)];
        Vector3[] l = [new(5, 0, 0), new(8, 0, 0), new(8, 0, 1), new(6, 0, 1), new(6, 0, 3), new(5, 0, 3)];
        doc.Add(Primitive.Prism(l, new Vector3(0, 1, 0), "dev_a", TextureMapping.Default)!);

        var files = ObjExporter.Build(doc, lib, null);
        Assert.Equal(2, files.Meshes);
        Assert.Contains("mtllib export.mtl", files.Obj);
        Assert.Contains("newmtl dev_a", files.Mtl);
        Assert.Contains("newmtl dev_b", files.Mtl);
        Assert.Contains("f ", files.Obj);
        // n-gons are written as is: the L caps have 6 corners.
        Assert.Contains(files.Obj.Split('\n'), line => line.StartsWith("f ") && line.Split(' ').Length == 7);

        var back = ObjImporter.Parse(files.Obj, lib, "dev_a", scale: 0.5f);
        Assert.Equal(2, back.Meshes.Count);
        var m = back.Meshes[0];
        Assert.Equal("block", m.Name); // Chip's "_<id>" uniqueness suffix comes back off
        Assert.Equal(6, m.Faces.Count);
        Assert.Equal(box.Bounds(), m.Bounds());
        Assert.Equal(box.Faces.Select(f => f.Material).Order(), m.Faces.Select(f => f.Material).Order());
        Assert.Equal(8, back.Meshes[1].Faces.Count);

        // UVs: V is flipped on write and flipped back on read.
        int top = Enumerable.Range(0, m.Faces.Count).Single(f => Vector3.Dot(m.FaceNormal(f), box.FaceNormal(3)) > 0.999f);
        var uvs = m.Faces[top].UVs!;
        Assert.Equal(box.Faces[3].UVs!.OrderBy(u => u.X).ThenBy(u => u.Y), uvs.OrderBy(u => u.X).ThenBy(u => u.Y));
    }

    [Fact]
    public void Obj_leaves_out_collision_only_meshes_and_tool_faces()
    {
        var doc = new Document();
        var a = doc.Add(Box(Vector3.Zero, Vector3.One));
        a.Faces[0].Material = "nodraw";
        a.Faces[1].Material = "clip";
        doc.Add(Box(Vector3.One, Vector3.One * 2)).Collision = CollisionMode.Only;
        var files = ObjExporter.Build(doc, Lib(), null);
        Assert.Equal(1, files.Meshes);
        Assert.Equal(4, files.Obj.Split('\n').Count(l => l.StartsWith("f ")));
        Assert.DoesNotContain("nodraw", files.Mtl);
        Assert.DoesNotContain("clip", files.Mtl);
    }

    [Fact]
    public void Obj_reader_handles_objects_negative_indices_and_partial_corner_data()
    {
        const string text = """
            # two objects
            mtllib scene.mtl
            o first thing
            v 0 0 0
            v 1 0 0
            v 1 0 1
            v 0 0 1
            vn 0 1 0
            usemtl DEV_B
            f -4//1 -1//1 -2//1 -3//1
            o wall_3
            v 0 1 0
            v 1 1 0
            v 0 1 1
            vt 0 0
            vt 1 0
            vt 0 1
            usemtl unknown_mat
            f 5/1 7/3 6/2
            f 5 6 99
            """;
        var r = ObjImporter.Parse(text, Lib(), "dev_a");
        Assert.Equal(2, r.Meshes.Count);
        Assert.Equal("first thing", r.Meshes[0].Name);
        Assert.Equal("wall_3", r.Meshes[1].Name); // other tools' names are left alone
        var quad = Assert.Single(r.Meshes[0].Faces);
        Assert.Equal("dev_b", quad.Material);   // resolved to the library's casing
        Assert.Null(quad.UVs);
        Assert.True(r.Meshes[0].FaceNormal(0).Y > 0.99f);
        var tri = Assert.Single(r.Meshes[1].Faces);
        Assert.Equal("unknown_mat", tri.Material); // kept, shows as missing until the texture is added
        Assert.Equal(new Vector2(0, 1), tri.UVs![0]); // vt 0 0 -> V flipped
        Assert.Single(r.Warnings);
    }

    [Fact]
    public void Obj_export_writes_obj_mtl_and_referenced_textures()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chip-obj-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var png = Path.Combine(dir, "brick.png");
        File.WriteAllBytes(png, MaterialLibrary.EncodePng(2, 2, new byte[16]));
        var lib = Lib();
        lib.Add(new MaterialInfo("brick", png, 2, 2));
        var doc = new Document();
        doc.Add(Box(Vector3.Zero, Vector3.One, "brick"));
        var path = Path.Combine(dir, "level.obj");
        ObjExporter.Export(doc, lib, path);
        Assert.Contains("mtllib level.mtl", File.ReadAllText(path));
        Assert.Contains("map_Kd brick.png", File.ReadAllText(Path.Combine(dir, "level.mtl")));
    }
}
