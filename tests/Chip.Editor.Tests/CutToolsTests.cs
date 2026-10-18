using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using Chip.Editor.Framework;
using Chip.Editor.Tools;

namespace Chip.Editor.Tests;

/// <summary>The cut tools' apply paths on a headless context: one undo step each, selection kept valid.</summary>
public class CutToolsTests
{
    static EditorContext WithBox(out PolyMesh box)
    {
        var doc = new Document();
        box = doc.Add(MeshOps.CreateBox(Vector3.Zero, new Vector3(2, 2, 2), "dev_a", TextureMapping.Default));
        return RegistryTests.Headless(doc);
    }

    static T Tool<T>(EditorContext ctx) => ctx.Tools.OfType<T>().Single();

    static bool Closed(PolyMesh m) => m.Faces.SelectMany(f => f.Edges()).GroupBy(e => e).All(g => g.Count() == 2);

    [Fact]
    public void Cut_tools_register_with_their_scythe_hotkeys()
    {
        var ctx = RegistryTests.Headless();
        Assert.Equal("Shift+X", ctx.Commands.Find("tool.clip")!.HotkeyText);
        Assert.Equal("C", ctx.Commands.Find("tool.knife")!.HotkeyText);
        Assert.Equal("B", ctx.Commands.Find("tool.bevel")!.HotkeyText);
        Assert.All(new[] { "clip", "knife", "bevel" }, id => Assert.True(ctx.Tools.Single(t => t.Id == id).HasOptions));
    }

    [Fact]
    public void Clip_keep_both_splits_the_selected_mesh_and_selects_both_pieces()
    {
        var ctx = WithBox(out var box);
        ctx.SetMode(SelectMode.Object);
        ctx.Sel.Add(ElementRef.Object(box.Id));
        var clip = Tool<ClipTool>(ctx);
        clip.Keep = ClipKeep.Both;
        Assert.Equal(1, clip.Apply(ctx, new Vector3(1, 1, 1), new Vector3(1, 1, 0)));
        Assert.Equal(2, ctx.Doc.Meshes.Count);
        Assert.All(ctx.Doc.Meshes, m => Assert.True(Closed(m)));
        Assert.Equal(2, ctx.Sel.Count);
        Assert.Equal("Clip", ctx.Session.UndoLabel);
        ctx.Commands.Execute("edit.undo");
        Assert.Single(ctx.Doc.Meshes);
        Assert.Equal(6, ctx.Doc.Meshes[0].Faces.Count);
    }

    [Fact]
    public void Clip_keep_front_and_cap_with_the_active_material()
    {
        var ctx = WithBox(out var box);
        var clip = Tool<ClipTool>(ctx);
        clip.Keep = ClipKeep.Front;
        clip.CapWithActive = true;
        ctx.ActiveMaterial = "dev_b";
        clip.Apply(ctx, new Vector3(0, 1.5f, 0), Vector3.UnitY);
        var m = Assert.Single(ctx.Doc.Meshes);
        Assert.Equal(box.Id, m.Id);
        Assert.True(Closed(m));
        Assert.Equal(1, m.Faces.Count(f => f.Material == "dev_b"));
        Assert.True(m.Bounds().Min.Y > 1.49f);
    }

    [Fact]
    public void A_clip_that_misses_reports_an_error_and_adds_no_undo_step()
    {
        var ctx = WithBox(out _);
        Assert.Equal(0, Tool<ClipTool>(ctx).Apply(ctx, new Vector3(0, 5, 0), Vector3.UnitY));
        Assert.Null(ctx.Session.UndoLabel);
        Assert.True(ctx.StatusIsError);
    }

    [Fact]
    public void Knife_cuts_selected_faces_and_selects_the_new_pieces()
    {
        var ctx = WithBox(out var box);
        ctx.SetMode(SelectMode.Face);
        int top = Enumerable.Range(0, 6).Single(f => box.FaceNormal(f).Y > 0.9f);
        ctx.Sel.Add(ElementRef.Face(box.Id, top));
        int pieces = Tool<KnifeTool>(ctx).Apply(ctx, [new Vector3(-1, 2, 1), new Vector3(3, 2, 1)], new Vector3(1, 10, 1));
        Assert.Equal(1, pieces);
        Assert.Equal(7, ctx.Doc.Meshes[0].Faces.Count);
        Assert.True(Closed(ctx.Doc.Meshes[0]));
        Assert.Equal(2, ctx.Sel.Count);
        ctx.Commands.Execute("edit.undo");
        Assert.Equal(6, ctx.Doc.Meshes[0].Faces.Count);
    }

    [Fact]
    public void Bevel_applies_exact_values_through_the_menu_command()
    {
        var ctx = WithBox(out var box);
        ctx.SetMode(SelectMode.Edge);
        Assert.False(ctx.Commands.Find("mesh.bevel")!.Enabled);
        var edge = box.Edges().First();
        ctx.Sel.Add(ElementRef.EdgeOf(box.Id, edge));
        var bevel = Tool<BevelTool>(ctx);
        bevel.Width = 0.25f;
        bevel.Segments = 3;
        Assert.True(ctx.Commands.Execute("mesh.bevel"));
        Assert.Equal(9, ctx.Doc.Meshes[0].Faces.Count);
        Assert.True(Closed(ctx.Doc.Meshes[0]));
        Assert.True(ctx.Sel.IsEmpty);
        ctx.Commands.Execute("edit.undo");
        Assert.Equal(6, ctx.Doc.Meshes[0].Faces.Count);
        Assert.Single(ctx.Sel.Items); // the snapshot restores the selection too
    }

    [Fact]
    public void Idle_cut_tools_have_nothing_to_cancel()
    {
        var ctx = WithBox(out _);
        foreach (var id in new[] { "clip", "knife", "bevel" })
        {
            ctx.SetTool(id);
            Assert.False(ctx.ActiveTool!.Busy);
            ctx.Commands.Execute("edit.cancel"); // Esc leaves the tool
            Assert.Equal("select", ctx.ActiveTool!.Id);
        }
        Assert.Null(ctx.Session.UndoLabel);
    }
}
