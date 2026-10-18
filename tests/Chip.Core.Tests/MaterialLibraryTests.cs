using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Chip.Core.Geometry;
using Chip.Core.IO;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Core.Tests;

public sealed class MaterialLibraryTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "chip-materials", Guid.NewGuid().ToString("N"));

    public MaterialLibraryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    // ---- Tiny image files ----

    static byte[] Png(int w, int h) => MaterialLibrary.EncodePng(w, h, new byte[w * h * 4]);

    /// <summary>SOI, an APP0 segment to skip, a DQT, then a baseline SOF0 frame header.</summary>
    static byte[] Jpeg(int w, int h, byte sof = 0xC0)
    {
        var b = new List<byte> { 0xFF, 0xD8 };
        b.AddRange([0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0]);
        b.AddRange([0xFF, 0xDB, 0x00, 0x04, 0x00, 0x01]);
        b.AddRange([0xFF, sof, 0x00, 0x11, 0x08, (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w, 0x03]);
        b.AddRange(new byte[9]);
        b.AddRange([0xFF, 0xD9]);
        return b.ToArray();
    }

    /// <summary>Uncompressed 24-bit (type 2) or RLE 32-bit (type 10) TGA; pixel i is (i, 2i, 3i).</summary>
    static byte[] Tga(int w, int h, bool rle = false, bool topDown = false)
    {
        var b = new List<byte>();
        var head = new byte[18];
        head[2] = (byte)(rle ? 10 : 2);
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(12), (ushort)w);
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(14), (ushort)h);
        head[16] = (byte)(rle ? 32 : 24);
        head[17] = (byte)(topDown ? 0x20 : 0);
        b.AddRange(head);
        for (int i = 0; i < w * h; i++)
        {
            if (rle) b.Add(0); // raw packet of one pixel
            b.AddRange([(byte)(3 * i), (byte)(2 * i), (byte)i]); // BGR
            if (rle) b.Add(255);
        }
        return b.ToArray();
    }

    string Write(string rel, byte[] bytes)
    {
        var path = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // ---- Headers ----

    [Fact]
    public void Header_readers_report_png_jpg_and_tga_sizes()
    {
        Assert.True(MaterialLibrary.TryReadImageSize(Write("a.png", Png(7, 3)), out int w, out int h));
        Assert.Equal((7, 3), (w, h));
        Assert.True(MaterialLibrary.TryReadImageSize(Write("b.jpg", Jpeg(640, 480)), out w, out h));
        Assert.Equal((640, 480), (w, h));
        Assert.True(MaterialLibrary.TryReadImageSize(Write("p.jpeg", Jpeg(300, 1024, sof: 0xC2)), out w, out h)); // progressive
        Assert.Equal((300, 1024), (w, h));
        Assert.True(MaterialLibrary.TryReadImageSize(Write("c.tga", Tga(5, 9)), out w, out h));
        Assert.Equal((5, 9), (w, h));

        Assert.False(MaterialLibrary.TryReadImageSize(Write("d.png", Encoding.ASCII.GetBytes("not an image at all, sorry")), out _, out _));
        Assert.False(MaterialLibrary.TryReadImageSize(Write("e.tga", new byte[10]), out _, out _));
        Assert.False(MaterialLibrary.TryReadImageSize(Path.Combine(_dir, "missing.png"), out _, out _));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Tga_decodes_top_down_and_exports_as_png(bool rle, bool topDown)
    {
        Assert.True(MaterialLibrary.TryDecodeTga(Tga(2, 2, rle, topDown), out int w, out int h, out var rgba));
        Assert.Equal((2, 2), (w, h));
        // File pixel 0 is the bottom-left unless the top-down bit is set.
        int top0 = topDown ? 0 : 2;
        Assert.Equal(new byte[] { (byte)top0, (byte)(2 * top0), (byte)(3 * top0), 255 }, rgba[..4]);

        var info = new MaterialInfo("t", Write("t.tga", Tga(3, 2, rle, topDown)), 3, 2);
        var image = MaterialLibrary.ExportImage(info);
        Assert.NotNull(image);
        Assert.Equal("image/png", image!.Value.MimeType);
        Assert.True(MaterialLibrary.TryReadImageSize(new MemoryStream(image.Value.Data), ".png", out w, out h));
        Assert.Equal((3, 2), (w, h));
        // IDAT inflates to filter byte + RGBA per row.
        var idat = Chunk(image.Value.Data, "IDAT");
        using var z = new ZLibStream(new MemoryStream(idat), CompressionMode.Decompress);
        var raw = new MemoryStream();
        z.CopyTo(raw);
        Assert.Equal((3 * 4 + 1) * 2, raw.Length);
    }

    static byte[] Chunk(byte[] png, string type)
    {
        for (int p = 8; p < png.Length;)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(p));
            if (Encoding.ASCII.GetString(png, p + 4, 4) == type) return png[(p + 8)..(p + 8 + len)];
            p += 12 + len;
        }
        throw new InvalidDataException(type);
    }

    [Fact]
    public void Jpeg_exports_as_jpeg()
    {
        var image = MaterialLibrary.ExportImage(new MaterialInfo("j", Write("j.jpg", Jpeg(4, 4)), 4, 4));
        Assert.Equal("image/jpeg", image!.Value.MimeType);
    }

    // ---- Scanning ----

    [Fact]
    public void Scans_nested_folders_with_aliases_and_duplicates()
    {
        Write("wall.png", Png(512, 512));
        Write("props/crate.jpg", Jpeg(256, 256));
        Write("props/metal/plate.tga", Tga(128, 64));
        Write("pbr/brick_Albedo.png", Png(1024, 1024));
        Write("pbr/brick_Normal.png", Png(1024, 1024));
        Write("pbr/brick_Roughness.jpg", Jpeg(1024, 1024));
        Write("zz/wall.png", Png(64, 64));      // same name as the root file: first wins
        Write("readme.txt", Encoding.ASCII.GetBytes("hello"));
        Write("broken.png", new byte[3]);

        var lib = new MaterialLibrary();
        lib.AddFolder(_dir);
        Assert.False(lib.AddFolder(_dir + Path.DirectorySeparatorChar)); // same folder
        var result = lib.Rescan();

        Assert.Equal(["brick", "crate", "plate", "wall"], lib.All.Select(m => m.Name).Order().ToArray());
        Assert.Equal(4, result.Materials);
        Assert.Equal(1, result.Duplicates);
        Assert.Equal(1, result.Unreadable);
        Assert.Equal("props/metal", lib.Get("plate").Folder);
        Assert.Equal("", lib.Get("wall").Folder);
        Assert.Equal(512, lib.Get("wall").Width);
        Assert.Equal((128, 64), (lib.Get("plate").Width, lib.Get("plate").Height));
        Assert.Equal(Path.GetFullPath(_dir), lib.Get("crate").Source);
        Assert.True(MaterialLibrary.IsMissing(lib.Get("nope"))); // a copy carrying this library's density

        // Rescan picks up new files and keeps code-added materials.
        lib.Add(new MaterialInfo("manual", null, 32, 32));
        Write("props/new.png", Png(16, 16));
        lib.Rescan();
        Assert.True(lib.Contains("new"));
        Assert.True(lib.Contains("manual"));
        Assert.Equal("manual", lib.All[0].Name);
    }

    [Fact]
    public void Missing_folders_are_reported_and_env_paths_split()
    {
        var lib = new MaterialLibrary();
        lib.AddFolder(Path.Combine(_dir, "nope"));
        Assert.Equal(1, lib.Rescan().MissingFolders);
        Assert.Equal([@"C:\a", @"D:\b c"], MaterialLibrary.EnvironmentFolders(@" C:\a ;; ""D:\b c"" ;"));
        Assert.Empty(MaterialLibrary.EnvironmentFolders(""));
    }

    [Theory]
    [InlineData("brick_BaseColor", "brick")]
    [InlineData("brick_albedo", "brick")]
    [InlineData("brick_diffuse", "brick")]
    [InlineData("concrete_rough", "concrete_rough")]
    [InlineData("sheet_metal", "sheet_metal")]
    [InlineData("dev_measurewall01a", "dev_measurewall01a")]
    [InlineData("brick_normal", null)]
    [InlineData("brick_Roughness", null)]
    [InlineData("brick_ORM", null)]
    public void Material_names_alias_base_colour_and_skip_other_maps(string stem, string? expected) =>
        Assert.Equal(expected, MaterialLibrary.MaterialName(stem));

    // ---- Texel density ----

    [Fact]
    public void Density_override_sets_scale_tile_size_and_new_geometry()
    {
        Write("wall.png", Png(512, 512));
        var lib = MaterialLibrary.LoadFolder(_dir);
        Assert.Equal(TextureMapping.DefaultScale, lib.ScaleFor("wall"));
        Assert.Equal(1f, lib.Get("wall").TileWidth);

        Assert.True(lib.SetTexelDensity("wall", 256)); // 512 px over 2 m
        Assert.Equal(1 / 256f, lib.ScaleFor("wall"));
        Assert.Equal(2f, lib.Get("wall").TileWidth);

        // A 2 m block face spans exactly one tile at the override.
        var box = MeshOps.CreateBox(Vector3.Zero, new Vector3(2, 2, 2), "wall", lib.MappingFor("wall"));
        var face = box.Faces[5];
        var uvs = face.Verts.Select((v, i) => face.UV(i, box.Positions[v], 512, 512)).ToList();
        Assert.Equal(1f, uvs.Max(u => u.X) - uvs.Min(u => u.X), 4);

        // The override persists in the folder's side-car and survives a fresh scan.
        Assert.True(File.Exists(Path.Combine(_dir, MaterialLibrary.SidecarName)));
        Assert.Equal(256f, MaterialLibrary.LoadFolder(_dir).Get("wall").TexelDensity);

        lib.SetTexelDensity("wall", null);
        Assert.Equal(TextureMapping.DefaultScale, lib.ScaleFor("wall"));
        Assert.False(File.Exists(Path.Combine(_dir, MaterialLibrary.SidecarName)));
    }

    static MaterialLibrary TwoDensities()
    {
        var lib = new MaterialLibrary();
        lib.Add(new MaterialInfo("fine", null, 512, 512));
        lib.Add(new MaterialInfo("coarse", null, 512, 512) { TexelDensity = 128 }); // tiles every 4 m
        return lib;
    }

    [Fact]
    public void Assign_and_align_follow_the_material_density()
    {
        var lib = TwoDensities();
        var box = MeshOps.CreateBox(Vector3.Zero, Vector3.One, "fine", lib.MappingFor("fine"));
        box.Faces[0].Map.ScaleU *= -2; // a flipped, doubled face keeps its tweak relative to the density
        lib.Assign(box.Faces[0], "coarse");
        Assert.Equal(-2f / 128, box.Faces[0].Map.ScaleU, 6);
        Assert.Equal(1f / 128, box.Faces[0].Map.ScaleV, 6);

        lib.ApplyDensity(box.Faces[0]);
        Assert.Equal(-1f / 128, box.Faces[0].Map.ScaleU, 6);

        box.Faces[1].Material = "coarse";
        lib.AlignToGrid(box, [1]);
        Assert.Equal(1f / 128, box.Faces[1].Map.ScaleU, 6);
        Assert.Equal(1f / 512, box.Faces[2].Map.ScaleU, 6); // untouched
    }

    [Fact]
    public void Replace_swaps_a_material_across_the_document_in_one_undo_step()
    {
        var lib = TwoDensities();
        var doc = new Document();
        doc.Add(MeshOps.CreateBox(Vector3.Zero, Vector3.One, "fine", lib.MappingFor("fine")));
        var b = doc.Add(MeshOps.CreateBox(Vector3.One, Vector3.One * 2, "fine", lib.MappingFor("fine")));
        b.Faces[3].Material = "other";
        var s = new Session();
        s.Reset(doc, null);

        int n = 0;
        s.Do("Replace Material", () => n = lib.Replace(s.Doc, "FINE", "coarse"));
        Assert.Equal(11, n);
        Assert.Equal([("coarse", 11), ("other", 1)], MaterialLibrary.Usage(s.Doc));
        Assert.Equal(1f / 128, s.Doc.Meshes[0].Faces[0].Map.ScaleU, 6);

        s.Undo();
        Assert.Equal([("fine", 11), ("other", 1)], MaterialLibrary.Usage(s.Doc));
    }

    // ---- Tool materials ----

    [Theory]
    [InlineData("nodraw", ToolMaterial.NoDraw)]
    [InlineData("toolsnodraw", ToolMaterial.NoDraw)]
    [InlineData("CLIP", ToolMaterial.Clip)]
    [InlineData("trigger", ToolMaterial.Trigger)]
    [InlineData("skip", ToolMaterial.Skip)]
    [InlineData("tools", ToolMaterial.None)]
    [InlineData("dev_grey", ToolMaterial.None)]
    public void Tool_materials_are_recognised_by_name(string name, ToolMaterial kind) =>
        Assert.Equal(kind, MaterialLibrary.ToolKind(name));

    static JsonNode ExportJson(Document doc)
    {
        var glb = GlbExporter.Build(doc, new MaterialLibrary());
        return JsonNode.Parse(Encoding.UTF8.GetString(glb, 20, (int)BitConverter.ToUInt32(glb, 12)))!;
    }

    static int IndexCount(JsonNode json, JsonNode mesh) =>
        mesh["primitives"]!.AsArray().Sum(p => json["accessors"]![p!["indices"]!.GetValue<int>()]!["count"]!.GetValue<int>());

    [Fact]
    public void Export_omits_nodraw_and_skip_and_emits_colonly_for_clip()
    {
        var doc = new Document { ExportCollision = true };
        doc.Export.BakeCollision = false; // the per-mesh collider path (baking is covered in CollisionBakeExportTests)
        var a = doc.Add(MeshOps.CreateBox(Vector3.Zero, Vector3.One, "wall", TextureMapping.Default));
        a.Faces[2].Material = "nodraw"; // bottom: hidden but solid
        a.Faces[3].Material = "skip";   // top: gone entirely
        a.Faces[0].Material = "clip";
        var t = doc.Add(MeshOps.CreateBox(Vector3.One, Vector3.One * 2, "trigger", TextureMapping.Default));
        var n = doc.Add(MeshOps.CreateBox(Vector3.One * 3, Vector3.One * 4, "nodraw", TextureMapping.Default));
        n.Collision = CollisionMode.None; // a non-colliding nodraw mesh exports nothing

        var json = ExportJson(doc);
        var nodes = json["nodes"]!.AsArray();
        var names = nodes.Select(x => x!["name"]!.GetValue<string>()).ToList();
        Assert.Equal([$"block_{a.Id}-col", $"block_{a.Id}_clip-colonly", $"block_{t.Id}_trigger-colonly"], names);

        var meshes = json["meshes"]!.AsArray();
        Assert.Equal(3 * 6, IndexCount(json, meshes[0]!));  // 3 rendered quads
        Assert.Equal(2 * 6, IndexCount(json, meshes[1]!));  // clip + nodraw collide
        Assert.All(meshes[1]!["primitives"]!.AsArray(), p => Assert.Null(p!["material"]));
        Assert.Equal("trigger", nodes[2]!["extras"]!["chip_tool"]!.GetValue<string>());
        Assert.DoesNotContain(json["materials"]!.AsArray(), m => m!["name"]!.GetValue<string>() is "nodraw" or "skip" or "clip");
    }

    [Fact]
    public void Nodraw_on_a_non_colliding_document_is_simply_dropped()
    {
        var doc = new Document { ExportCollision = false };
        var a = doc.Add(MeshOps.CreateBox(Vector3.Zero, Vector3.One, "wall", TextureMapping.Default));
        a.Faces[2].Material = "nodraw";
        var json = ExportJson(doc);
        Assert.Single(json["nodes"]!.AsArray());
        Assert.Equal(5 * 6, IndexCount(json, json["meshes"]![0]!));
    }
}
