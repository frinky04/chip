using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.IO;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using Chip.Editor.Features.Base;
using Chip.Editor.Features.Pipeline;

namespace Chip.Editor.Tests;

/// <summary>Pipeline commands on a headless context: watch mode, import, per-mesh collision.</summary>
public class PipelineTests
{
    static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chip-pipeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static Chip.Editor.Framework.EditorContext WithBox(out PolyMesh box)
    {
        var doc = new Document();
        box = doc.Add(MeshOps.CreateBox(Vector3.Zero, new Vector3(2, 2, 2), "dev_a", TextureMapping.Default));
        return RegistryTests.Headless(doc);
    }

    [Fact]
    public void Pipeline_commands_register_in_the_file_menu()
    {
        var ctx = RegistryTests.Headless();
        Assert.Equal("Ctrl+Alt+E", ctx.Commands.Find("pipeline.exportOnSave")!.HotkeyText);
        foreach (var id in new[] { "pipeline.exportOnSave", "pipeline.exportObj", "pipeline.exportSettings", "pipeline.import" })
            Assert.Equal("File", ctx.Commands.Find(id)!.Menu);
        Assert.Contains(ctx.Sections, s => s.Id == "collision");
    }

    [Fact]
    public void Saving_with_export_on_save_rewrites_the_glb()
    {
        var dir = TempDir();
        var ctx = WithBox(out _);
        var chip = Path.Combine(dir, "level.chip");
        ctx.Doc.ExportPath = "level.glb"; // relative: resolved next to the .chip
        ctx.Session.Reset(ctx.Doc, chip);

        ctx.Commands.Execute("pipeline.exportOnSave");
        Assert.True(ctx.Doc.Export.ExportOnSave);
        Assert.True(ctx.Commands.Find("pipeline.exportOnSave")!.IsChecked!());
        Assert.Equal("Turn On Export on Save", ctx.Session.UndoLabel);

        Assert.True(FileFeature.Save(ctx, saveAs: false));
        var glb = Path.Combine(dir, "level.glb");
        Assert.True(File.Exists(glb));
        Assert.Contains("exported 1 mesh to level.glb", ctx.StatusMessage);
        Assert.False(ctx.StatusIsError);
        Assert.True(DocumentSerializer.Load(chip).Export.ExportOnSave); // the setting is saved with the level

        File.Delete(glb);
        ctx.Commands.Execute("pipeline.exportOnSave");
        Assert.True(FileFeature.Save(ctx, saveAs: false));
        Assert.False(File.Exists(glb));
        Assert.StartsWith("Saved level.chip", ctx.StatusMessage);
    }

    [Fact]
    public void Export_on_save_without_a_path_says_how_to_fix_it()
    {
        var ctx = WithBox(out _);
        ctx.Doc.Export.ExportOnSave = true;
        ctx.Session.MarkSaved(Path.Combine(TempDir(), "level.chip"));
        Assert.True(ctx.StatusIsError);
        Assert.Contains("Export GLB as", ctx.StatusMessage);
    }

    [Fact]
    public void Import_adds_selected_meshes_and_entities_as_one_undo_step()
    {
        var dir = TempDir();
        var src = new Document();
        src.Add(MeshOps.CreateBox(Vector3.Zero, Vector3.One, "dev_b", TextureMapping.Default));
        src.Add(MeshOps.CreateBox(new Vector3(3, 0, 0), new Vector3(4, 1, 1), "rock", TextureMapping.Default));
        src.Add(new Entity { ClassName = "info_player_start", Position = new Vector3(1, 0, 1) });
        var glb = Path.Combine(dir, "props.glb");
        var lib = new MaterialLibrary();
        GlbExporter.Export(src, lib, glb);

        var ctx = WithBox(out _);
        var result = PipelineActions.Import(ctx, glb);
        Assert.NotNull(result);
        Assert.Equal(3, ctx.Doc.Meshes.Count);
        Assert.Single(ctx.Doc.Entities);
        Assert.Equal(SelectMode.Object, ctx.Sel.Mode);
        Assert.Equal(2, ctx.Sel.Count);
        Assert.Equal(ctx.Doc.Meshes.Skip(1).Select(m => m.Id).ToHashSet(), ctx.Sel.MeshIds());
        Assert.Contains("Imported 2 meshes and 1 entity (12 faces) from props.glb", ctx.StatusMessage);
        Assert.Contains("rock", ctx.StatusMessage); // not in the library: say so
        Assert.Equal("Import", ctx.Session.UndoLabel);

        ctx.Commands.Execute("edit.undo");
        Assert.Single(ctx.Doc.Meshes);
        Assert.Empty(ctx.Doc.Entities);
    }

    [Fact]
    public void Imported_ids_never_clash_with_the_document()
    {
        var dir = TempDir();
        var ctx = WithBox(out var box);
        var obj = Path.Combine(dir, "a.obj");
        ObjExporter.Export(ctx.Doc, ctx.Materials, obj);
        PipelineActions.Import(ctx, obj);
        Assert.Equal(2, ctx.Doc.Meshes.Select(m => m.Id).Distinct().Count());
        Assert.NotEqual(box.Id, ctx.Doc.Meshes[1].Id);
    }

    [Fact]
    public void Import_of_an_unreadable_file_reports_an_error_and_changes_nothing()
    {
        var path = Path.Combine(TempDir(), "broken.glb");
        File.WriteAllBytes(path, [1, 2, 3]);
        var ctx = WithBox(out _);
        Assert.Null(PipelineActions.Import(ctx, path));
        Assert.True(ctx.StatusIsError);
        Assert.StartsWith("Couldn't import broken.glb", ctx.StatusMessage);
        Assert.Single(ctx.Doc.Meshes);
        Assert.False(ctx.Session.Dirty);
    }

    [Fact]
    public void Collision_mode_applies_to_selected_meshes_as_one_undo_step()
    {
        var ctx = WithBox(out var box);
        var section = ctx.Sections.Single(s => s.Id == "collision");
        Assert.False(section.Visible(ctx));
        ctx.SetMode(SelectMode.Object);
        ctx.Sel.Add(ElementRef.Object(box.Id));
        Assert.True(section.Visible(ctx));

        PipelineActions.SetCollision(ctx, CollisionMode.ConvexOnly);
        Assert.Equal(CollisionMode.ConvexOnly, ctx.Doc.Meshes[0].Collision);
        Assert.Equal("Set Collision", ctx.Session.UndoLabel);
        ctx.Commands.Execute("edit.undo");
        Assert.Equal(CollisionMode.Default, ctx.Doc.Meshes[0].Collision);
    }

    [Fact]
    public void Relative_export_paths_resolve_against_the_chip_folder()
    {
        var ctx = RegistryTests.Headless();
        Assert.Null(PipelineActions.ResolveExportPath(ctx));
        var dir = TempDir();
        ctx.Doc.ExportPath = Path.Combine("..", "godot", "level.glb");
        ctx.Session.Reset(ctx.Doc, Path.Combine(dir, "levels", "a.chip"));
        Assert.Equal(Path.GetFullPath(Path.Combine(dir, "godot", "level.glb")), PipelineActions.ResolveExportPath(ctx));
    }
}
