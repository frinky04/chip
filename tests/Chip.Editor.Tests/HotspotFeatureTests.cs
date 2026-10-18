using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Hotspots;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using Chip.Editor.Features.Hotspots;
using Chip.Editor.Framework;

namespace Chip.Editor.Tests;

/// <summary>Hotspot commands on a headless context: the bundled sample is found, apply is one undo step.</summary>
public class HotspotFeatureTests
{
    const string Sample = "hotspot_concrete";

    static EditorContext WithBox(out PolyMesh box)
    {
        var doc = new Document();
        box = doc.Add(MeshOps.CreateBox(Vector3.Zero, new Vector3(4, 1, 0.25f), "dev_a", TextureMapping.Default));
        return RegistryTests.Headless(doc);
    }

    [Fact]
    public void Hotspot_commands_use_the_reserved_hotkeys()
    {
        var ctx = RegistryTests.Headless();
        Assert.Equal("Shift+H", ctx.Commands.Find("hotspot.apply")!.HotkeyText);
        Assert.Equal("Shift+F", ctx.Commands.Find("hotspot.applyFaces")!.HotkeyText);
        Assert.Equal("Ctrl+Shift+H", ctx.Commands.Find("hotspot.editor")!.HotkeyText);
        Assert.Contains(ctx.Sections, s => s.Id == "hotspot");
    }

    [Fact]
    public void Bundled_sample_material_and_asset_are_discovered()
    {
        var ctx = RegistryTests.Headless();
        Assert.True(ctx.Materials.Contains(Sample));
        var asset = HotspotState.For(ctx).Library.ForMaterial(Sample);
        Assert.NotNull(asset);
        Assert.Equal(12, asset!.Patches.Count);
    }

    [Fact]
    public void Apply_hotspot_on_an_object_is_one_undo_step_and_undo_restores_the_faces()
    {
        var ctx = WithBox(out var box);
        ctx.ActiveMaterial = Sample;
        ctx.SetMode(SelectMode.Object);
        ctx.Sel.Add(ElementRef.Object(box.Id));
        Assert.True(ctx.Commands.Execute("hotspot.apply"));
        Assert.Equal("Apply Hotspot", ctx.Session.UndoLabel);
        var faces = ctx.Doc.Find(box.Id)!.Faces;
        Assert.All(faces, f =>
        {
            Assert.Equal(Sample, f.Material);
            Assert.NotNull(f.UVs);
        });
        Assert.False(ctx.StatusIsError);

        ctx.Commands.Execute("edit.undo");
        Assert.All(ctx.Doc.Find(box.Id)!.Faces, f =>
        {
            Assert.Equal("dev_a", f.Material);
            Assert.Null(f.UVs);
        });
    }

    [Fact]
    public void Individual_faces_reapply_to_the_same_uvs()
    {
        var ctx = WithBox(out var box);
        ctx.ActiveMaterial = Sample;
        ctx.SetMode(SelectMode.Face);
        for (int f = 0; f < box.Faces.Count; f++) ctx.Sel.Add(ElementRef.Face(box.Id, f));
        ctx.Commands.Execute("hotspot.applyFaces");
        var first = ctx.Doc.Find(box.Id)!.Faces.Select(f => f.UVs!.ToArray()).ToList();
        ctx.Commands.Execute("hotspot.applyFaces");
        var second = ctx.Doc.Find(box.Id)!.Faces.Select(f => f.UVs!).ToList();
        for (int i = 0; i < first.Count; i++) Assert.Equal(first[i], second[i]);
        Assert.Equal("Hotspot Individual Faces", ctx.Session.UndoLabel);
    }

    [Fact]
    public void Without_a_hotspot_material_apply_explains_and_changes_nothing()
    {
        var ctx = WithBox(out var box);
        ctx.ActiveMaterial = "dev_a";
        ctx.SetMode(SelectMode.Object);
        ctx.Sel.Add(ElementRef.Object(box.Id));
        ctx.Commands.Execute("hotspot.apply");
        Assert.True(ctx.StatusIsError);
        Assert.Contains("hotspot", ctx.StatusMessage);
        Assert.Null(ctx.Session.UndoLabel);
    }

    [Fact]
    public void The_editors_unsaved_working_copy_is_what_apply_uses()
    {
        var ctx = WithBox(out var box);
        ctx.ActiveMaterial = Sample;
        var editor = HotspotEditor.For(ctx);
        editor.Show(ctx, null);
        var state = HotspotState.For(ctx);
        Assert.NotNull(state.Editing);
        Assert.NotSame(state.Library.ForMaterial(Sample), state.Editing);
        // Keep only one patch in the working copy: every face must land in it.
        var only = state.Editing!.Patches[2];
        state.Editing.Patches.Clear();
        state.Editing.Patches.Add(only);
        state.Dirty = true;
        Assert.Same(state.Editing, state.AssetFor(Sample));

        ctx.SetMode(SelectMode.Object);
        ctx.Sel.Add(ElementRef.Object(box.Id));
        ctx.Commands.Execute("hotspot.apply");
        Assert.All(ctx.Doc.Find(box.Id)!.Faces, f => Assert.All(f.UVs!, uv =>
        {
            Assert.InRange(uv.X, only.X - 1e-4f, only.X + only.W + 1e-4f);
            Assert.InRange(uv.Y, only.Y - 1e-4f, only.Y + only.H + 1e-4f);
        }));
    }

    [Fact]
    public void Apply_settings_commands_toggle_the_shared_settings()
    {
        var ctx = RegistryTests.Headless();
        var s = HotspotState.For(ctx).Settings;
        Assert.False(s.DisallowRandom);
        ctx.Commands.Execute("hotspot.disallowRandom");
        ctx.Commands.Execute("hotspot.dontApplyMaterial");
        Assert.True(s.DisallowRandom);
        Assert.True(s.DontApplyMaterial);
        Assert.Equal(HotspotTiling.Allow, s.Tiling);
        Assert.Equal(HotspotMapping.Automatic, s.Mapping);
    }
}
