using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Chip.Core.Geometry;
using Chip.Core.IO;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Core.Tests;

/// <summary>The v1 acceptance scenario driven through the same Session/TransformGesture surface the editor uses.</summary>
public class ScenarioTests
{
    const string Wall = "dev_measurewall01d", Floor = "dev_measurewall01a";

    static PolyMesh Block(Session s, Vector3 min, Vector3 max, string mat)
    {
        PolyMesh? made = null;
        s.Do("Create Block", () => made = s.Doc.Add(MeshOps.CreateBox(min, max, mat, TextureMapping.Default)));
        return made!;
    }

    static void Select(Session s, SelectMode mode, params ElementRef[] items)
    {
        s.Sel.ConvertTo(mode, s.Doc);
        s.Sel.Clear();
        foreach (var i in items) s.Sel.Add(i);
    }

    static void Move(Session s, Vector3 delta, bool extrude = false)
    {
        var g = TransformGesture.Start(s, extrude ? "Extrude" : "Move", extrude)!;
        g.Apply(Matrix4x4.CreateTranslation(delta), rigid: true);
        g.Commit();
    }

    static int FaceFacing(PolyMesh m, Vector3 dir) => Enumerable.Range(0, m.Faces.Count).Single(f => Vector3.Dot(m.FaceNormal(f), dir) > 0.99f);

    [Fact]
    public void Two_rooms_doorway_corridor_ramp_save_reopen_export()
    {
        var s = new Session();
        s.Reset(new Document(), null);

        // Room A (8 x 8 m) and its east wall with a 1 m x 2.25 m doorway (two wall pieces + lintel).
        Block(s, new(0, -0.25f, 0), new(8, 0, 8), Floor);
        Block(s, new(8, 0, 0), new(8.25f, 3, 3.5f), Wall);
        Block(s, new(8, 0, 4.5f), new(8.25f, 3, 8), Wall);
        Block(s, new(8, 2.25f, 3.5f), new(8.25f, 3, 4.5f), Wall);

        // Corridor: a 2 m floor block, its +X face extruded 2 m further.
        var corridor = Block(s, new(8.25f, -0.25f, 3.5f), new(10.25f, 0, 4.5f), Floor);
        Select(s, SelectMode.Face, ElementRef.Face(corridor.Id, FaceFacing(corridor, Vector3.UnitX)));
        Move(s, new Vector3(2, 0, 0), extrude: true);
        corridor = s.Doc.Find(corridor.Id)!;
        Assert.Equal(10, corridor.Faces.Count);
        Assert.Equal(12.25f, corridor.Bounds().Max.X);

        // Room B floor, then a ramp: drag the top-west edge of a block down to the floor.
        Block(s, new(12.25f, -0.25f, 0), new(20.25f, 0, 8), Floor);
        var ramp = Block(s, new(14, 0, 1.5f), new(16, 1, 3), Wall);
        var topWest = ramp.Edges().Single(e =>
            ramp.Positions[e.A].X == 14 && ramp.Positions[e.B].X == 14 && ramp.Positions[e.A].Y == 1 && ramp.Positions[e.B].Y == 1);
        Select(s, SelectMode.Edge, ElementRef.EdgeOf(ramp.Id, topWest));
        Move(s, new Vector3(0, -1, 0));
        ramp = s.Doc.Find(ramp.Id)!;
        Assert.Equal(6, ramp.Positions.Count);              // wedge: two pairs of verts merged
        Assert.Equal(5, ramp.Faces.Count);                  // the west face collapsed away
        Assert.True(ramp.Faces.SelectMany(f => f.Edges()).GroupBy(e => e).All(g => g.Count() == 2)); // still closed
        Assert.Contains(ramp.Faces, f => f.Verts.Length == 3);
        Assert.Single(s.Sel.Items);                         // selection followed the merge

        // Undo the ramp edit, redo it.
        Assert.True(s.Undo());
        Assert.Equal(6, s.Doc.Find(ramp.Id)!.Faces.Count);
        Assert.True(s.Redo());
        Assert.Equal(5, s.Doc.Find(ramp.Id)!.Faces.Count);

        // Save → reopen → export.
        var dir = Path.Combine(Path.GetTempPath(), "chip-scenario", Guid.NewGuid().ToString("N")); // unique: tests may run in parallel worktrees
        Directory.CreateDirectory(dir);
        var chip = Path.Combine(dir, "level.chip");
        DocumentSerializer.Save(s.Doc, chip);
        var reopened = DocumentSerializer.Load(chip);
        Assert.Equal(s.Doc.Meshes.Count, reopened.Meshes.Count);
        Assert.Equal(s.Doc.Meshes.Sum(m => m.Faces.Count), reopened.Meshes.Sum(m => m.Faces.Count));

        var assets = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "assets", "textures"));
        var lib = MaterialLibrary.LoadFolder(assets);
        Assert.Equal(3, lib.All.Count);
        var glbPath = Path.Combine(dir, "level.glb");
        GlbExporter.Export(reopened, lib, glbPath);
        var glb = File.ReadAllBytes(glbPath);
        var json = JsonNode.Parse(Encoding.UTF8.GetString(glb, 20, (int)BitConverter.ToUInt32(glb, 12)))!;
        Assert.Equal(7, json["nodes"]!.AsArray().Count);
        Assert.All(json["nodes"]!.AsArray(), n => Assert.EndsWith("-col", n!["name"]!.GetValue<string>()));
        Assert.Equal(2, json["images"]!.AsArray().Count);   // only the two materials in use are embedded

        // Re-export after an edit keeps every node at the world origin (stable placement in Godot).
        Select(s, SelectMode.Object, ElementRef.Object(ramp.Id));
        Move(s, new Vector3(1, 0, 0));
        GlbExporter.Export(s.Doc, lib, glbPath);
        var json2 = JsonNode.Parse(Encoding.UTF8.GetString(File.ReadAllBytes(glbPath), 20, (int)BitConverter.ToUInt32(File.ReadAllBytes(glbPath), 12)))!;
        Assert.All(json2["nodes"]!.AsArray(), n => Assert.Null(n!["translation"]));
    }
}
