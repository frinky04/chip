using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Chip.Core.Geometry;
using Chip.Core.IO;
using Chip.Core.Picking;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Core.Tests;

/// <summary>Reference models: loading, the box and picking they give their entity, wall placement, export and paths.</summary>
public class ReferenceModelTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("chip-ref-").FullName;

    public ReferenceModelTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "assets", "references"))) root = root.Parent;
        ReferenceModels.BuiltInFolder = Path.Combine(root!.FullName, "assets", "references");
        ReferenceModels.LevelPath = null;
        ReferenceModels.ClearCache();
    }

    public void Dispose()
    {
        ReferenceModels.LevelPath = null;
        ReferenceModels.ClearCache();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    static PolyMesh Box(Vector3 a, Vector3 b) => MeshOps.CreateBox(a, b, "m", TextureMapping.Default);

    static void AssertNear(Vector3 expected, Vector3 actual, float eps = 1e-3f) =>
        Assert.True(Vector3.Distance(expected, actual) < eps, $"expected {expected}, got {actual}");

    /// <summary>
    /// A door like main_door.glb: a 1.4 × 2.2 frame, 0.12 deep (z from -0.06 to 0.06), with a 1.2 × 2.1 opening,
    /// origin at the bottom centre. Written as a .glb through Chip's own exporter.
    /// </summary>
    string WriteDoor(string name = "door.glb", float height = 2.2f)
    {
        var doc = new Document { ExportCollision = false };
        doc.Add(Box(new(-0.7f, 0, -0.06f), new(-0.6f, height, 0.06f)));
        doc.Add(Box(new(0.6f, 0, -0.06f), new(0.7f, height, 0.06f)));
        doc.Add(Box(new(-0.6f, 2.1f, -0.06f), new(0.6f, height, 0.06f)));
        var path = Path.Combine(_dir, name);
        GlbExporter.Export(doc, new MaterialLibrary(), path);
        return path;
    }

    static Entity Reference(string model, Vector3 position = default, float yaw = 0)
    {
        var e = EntityClasses.Create(ReferenceModels.ClassName, position, yaw);
        e.Properties[ReferenceModels.ModelKey] = EntityValues.Of(model);
        return e;
    }

    [Fact]
    public void The_built_in_figure_stands_on_its_origin_at_person_height()
    {
        var (model, error) = ReferenceModels.Get(ReferenceModels.DefaultModel);
        Assert.Null(error);
        Assert.NotNull(model);
        Assert.Equal(0, model!.Min.Y, 3);
        Assert.Equal(1.78f, model.Max.Y, 2);
        Assert.Equal(0, (model.Min.X + model.Max.X) / 2, 3); // centred on the origin
        Assert.Contains(ReferenceModels.DefaultModel, ReferenceModels.BuiltIns());
    }

    [Fact]
    public void A_new_reference_shows_the_figure_and_stays_out_of_exports()
    {
        var e = EntityClasses.Create(ReferenceModels.ClassName, Vector3.Zero);
        Assert.Equal(ReferenceModels.DefaultModel, ReferenceModels.ModelOf(e));
        Assert.False(ReferenceModels.Exports(e));
        Assert.True(ReferenceModels.Exports(EntityClasses.Create("info_player_start", Vector3.Zero)));
    }

    [Fact]
    public void Multi_mesh_files_merge_and_the_model_bounds_become_the_entity_box()
    {
        var e = Reference(WriteDoor(), new Vector3(5, 0, 0), yaw: 90);
        var (min, max) = EntityOps.Box(e);
        AssertNear(new Vector3(-0.7f, 0, -0.06f), min);
        AssertNear(new Vector3(0.7f, 2.2f, 0.06f), max);
        // Yaw 90 turns model +X toward world -Z: the frame now runs along Z.
        var corners = EntityOps.BoxCorners(e);
        Assert.Equal(1.4f, corners.Max(c => c.Z) - corners.Min(c => c.Z), 3);
        Assert.Equal(0.12f, corners.Max(c => c.X) - corners.Min(c => c.X), 3);
    }

    [Fact]
    public void Clicks_hit_the_frame_but_go_through_the_doorway()
    {
        var e = Reference(WriteDoor());
        var throughFrame = new Ray(new Vector3(0.65f, 1, 5), -Vector3.UnitZ);
        var throughOpening = new Ray(new Vector3(0, 1, 5), -Vector3.UnitZ);
        Assert.Equal(4.94f, EntityOps.RayHit(e, throughFrame)!.Value, 3);
        Assert.Null(EntityOps.RayHit(e, throughOpening));

        var doc = new Document();
        doc.Add(e);
        var view = new ViewInfo(Matrix4x4.CreateLookAt(new Vector3(0, 1, 5), new Vector3(0, 1, 0), Vector3.UnitY) *
            Matrix4x4.CreatePerspectiveFieldOfView(1, 1, 0.1f, 100), new Vector2(512, 512), new Vector3(0, 1, 5));
        Assert.Null(Picker.PickEntity(doc, view, new Vector2(256, 256))); // no glyph in the doorway either
    }

    [Fact]
    public void Placing_on_a_model_lands_on_its_triangles_not_its_box()
    {
        var figure = Reference(ReferenceModels.DefaultModel, new Vector3(3, 0, 0));
        var model = ReferenceModels.Get(figure).Model!;
        var hit = EntityOps.SurfaceHit(figure, new Ray(new Vector3(3, 10, 0), -Vector3.UnitY))!.Value;
        Assert.InRange(hit.Point.Y, model.Max.Y - 0.05f, model.Max.Y + 1e-3f); // the top of the head
        Assert.True(hit.Normal.Y > 0.5f, $"{hit.Normal}");
        // Past the figure's side, inside its bounds: the box would be hit, the model isn't.
        Assert.Null(EntityOps.SurfaceHit(figure, new Ray(new Vector3(3 + model.Max.X - 0.01f, 10, model.Max.Z - 0.01f), -Vector3.UnitY)));
    }

    [Fact]
    public void A_missing_file_falls_back_to_the_class_box_with_a_reason()
    {
        var e = Reference(Path.Combine(_dir, "gone.glb"));
        var (model, error) = ReferenceModels.Get(e);
        Assert.Null(model);
        Assert.Contains("gone.glb", error);
        var cls = EntityClasses.Get(ReferenceModels.ClassName);
        Assert.Equal((cls.BoxMin, cls.BoxMax), EntityOps.Box(e));
    }

    [Fact]
    public void Edited_files_reload_on_refresh()
    {
        var path = WriteDoor();
        Assert.Equal(2.2f, ReferenceModels.Get(path).Model!.Max.Y, 3);
        Assert.False(ReferenceModels.Refresh());
        WriteDoor(height: 2.4f);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5)); // the same second on coarse clocks
        int revision = ReferenceModels.Revision;
        Assert.True(ReferenceModels.Refresh());
        Assert.NotEqual(revision, ReferenceModels.Revision);
        Assert.Equal(2.4f, ReferenceModels.Get(path).Model!.Max.Y, 3);
    }

    [Fact]
    public void Picked_files_are_stored_relative_to_a_saved_level()
    {
        var door = WriteDoor();
        Assert.Equal(Path.GetFullPath(door), ReferenceModels.StoredPath(door)); // unsaved level: absolute

        Directory.CreateDirectory(Path.Combine(_dir, "levels"));
        ReferenceModels.LevelPath = Path.Combine(_dir, "levels", "hangar.chip");
        Assert.Equal("../door.glb", ReferenceModels.StoredPath(door));
        Assert.Equal(Path.GetFullPath(door), ReferenceModels.Resolve("../door.glb").Path);
        Assert.NotNull(ReferenceModels.Get("../door.glb").Model);

        ReferenceModels.LevelPath = null;
        Assert.Null(ReferenceModels.Resolve("../door.glb").Path);
        Assert.Contains("Save the level", ReferenceModels.Resolve("../door.glb").Error);
    }

    [Fact]
    public void Export_skips_references_unless_they_are_markers()
    {
        var doc = new Document();
        doc.Add(Box(new(-1, -0.1f, -1), new(1, 0, 1)));
        var figure = doc.Add(EntityClasses.Create(ReferenceModels.ClassName, Vector3.Zero));
        var door = doc.Add(Reference(WriteDoor("main_door.glb"), new Vector3(2, 0, 0), 90));
        door.Properties[ReferenceModels.MarkerKey] = EntityValues.Of(true);

        var result = GlbExporter.Build(doc, new MaterialLibrary(), null);
        Assert.Equal(1, result.Entities);
        var nodes = Gltf(result.Data)["nodes"]!.AsArray().Select(n => n!["name"]!.GetValue<string>()).ToList();
        Assert.Contains($"main_door_{door.Id}", nodes);
        Assert.DoesNotContain(nodes, n => n.Contains(ReferenceModels.DefaultModel) || n.Contains(figure.Id.ToString() + "_"));

        door.Name = "front_door";
        Assert.Equal("front_door", GlbExporter.EntityNodeName(door));
    }

    static JsonNode Gltf(byte[] glb)
    {
        int len = BitConverter.ToInt32(glb, 12);
        return JsonNode.Parse(Encoding.UTF8.GetString(glb, 20, len))!;
    }

    /// <summary>A wall 4 m long along X, 0.2 m thick (z from -0.2 to 0), its outside face at z = 0 facing +Z, from y = 0 to 3.</summary>
    static Document Wall()
    {
        var doc = new Document();
        doc.Add(Box(new(-2, 0, -0.2f), new(2, 3, 0)));
        return doc;
    }

    [Fact]
    public void A_door_clicked_on_a_wall_is_centred_in_its_thickness()
    {
        var doc = Wall();
        var door = ReferenceModels.Get(WriteDoor()).Model!;
        var r = ReferencePlacement.OnWall(doc, new Vector3(0.5f, 1.3f, 0), Vector3.UnitZ, 0, door);
        Assert.True(r.Inset);
        Assert.Equal(0.2f, r.Thickness!.Value, 3);
        Assert.Equal(0, r.Yaw);
        AssertNear(new Vector3(0.5f, 0, -0.1f), r.Position);
    }

    [Fact]
    public void A_person_clicked_on_a_wall_stands_against_it_facing_out()
    {
        var doc = Wall();
        var figure = ReferenceModels.Get(ReferenceModels.DefaultModel).Model!;
        // The wall's far side, facing -Z: the figure turns round and its back (model min z) touches the face.
        var r = ReferencePlacement.OnWall(doc, new Vector3(1, 1.5f, -0.2f), -Vector3.UnitZ, 0, figure);
        Assert.False(r.Inset);
        Assert.Equal(180, MathF.Abs(r.Yaw), 2);
        var e = EntityClasses.Create(ReferenceModels.ClassName, r.Position, r.Yaw);
        var corners = EntityOps.BoxCorners(e);
        Assert.Equal(-0.2f, corners.Max(c => c.Z), 3); // back flat on the wall
        Assert.Equal(0, corners.Min(c => c.Y), 3);
    }

    [Fact]
    public void Solid_surfaces_have_no_thickness_and_the_model_stands_against_them()
    {
        var doc = new Document();
        doc.Add(Box(new(-2, 0, -5), new(2, 3, 0))); // 5 m deep: past the thickness limit
        var door = ReferenceModels.Get(WriteDoor()).Model!;
        var r = ReferencePlacement.OnWall(doc, new Vector3(0, 1, 0), Vector3.UnitZ, 0, door);
        Assert.Null(r.Thickness);
        Assert.False(r.Inset);
        AssertNear(new Vector3(0, 0, 0.06f), r.Position);
    }

    [Fact]
    public void References_round_trip_through_the_level_file()
    {
        var doc = new Document();
        var e = doc.Add(Reference("../props/door.glb", new Vector3(1, 0, 2), 90));
        e.Properties[ReferenceModels.MarkerKey] = EntityValues.Of("1"); // v2 levels stored "1"; still reads as on
        var path = Path.Combine(_dir, "level.chip");
        DocumentSerializer.Save(doc, path);
        var back = DocumentSerializer.Load(path).Entities.Single();
        Assert.Equal(ReferenceModels.ClassName, back.ClassName);
        Assert.Equal("../props/door.glb", ReferenceModels.ModelOf(back));
        Assert.True(ReferenceModels.Exports(back));
    }
}
