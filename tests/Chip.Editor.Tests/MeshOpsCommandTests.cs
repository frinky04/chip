using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using Chip.Editor.Features.MeshEditing;
using Chip.Editor.Framework;

namespace Chip.Editor.Tests;

/// <summary>Stream A commands through a headless context: one undo step each, no step when nothing happens.</summary>
public class MeshOpsCommandTests
{
    static EditorContext WithBoxes(int count)
    {
        var doc = new Document();
        for (int i = 0; i < count; i++)
            doc.Add(MeshOps.CreateBox(new Vector3(3 * i, 0, 0), new Vector3(3 * i + 2, 2, 2), "dev_a", TextureMapping.Default));
        return RegistryTests.Headless(doc);
    }

    static void SelectFaces(EditorContext ctx, params int[] faces)
    {
        ctx.Commands.Execute("select.modeFace");
        ctx.Sel.Clear();
        foreach (var f in faces) ctx.Sel.Add(ElementRef.Face(ctx.Doc.Meshes[0].Id, f));
    }

    [Fact]
    public void Connect_splits_and_undoes_in_one_step()
    {
        var ctx = WithBoxes(1);
        ctx.Commands.Execute("select.modeVertex");
        ctx.Sel.Add(ElementRef.Vertex(ctx.Doc.Meshes[0].Id, 2));
        Assert.False(ctx.Commands.Find("mesh.connect")!.Enabled); // needs two vertices
        ctx.Sel.Add(ElementRef.Vertex(ctx.Doc.Meshes[0].Id, 7));
        Assert.True(ctx.Commands.Execute("mesh.connect"));
        Assert.Equal(7, ctx.Doc.Meshes[0].Faces.Count);
        Assert.Equal("Connect", ctx.Session.UndoLabel);
        ctx.Commands.Execute("edit.undo");
        Assert.Equal(6, ctx.Doc.Meshes[0].Faces.Count);
    }

    [Fact]
    public void A_command_that_cannot_act_reports_why_and_leaves_no_undo_step()
    {
        var ctx = WithBoxes(1);
        ctx.Commands.Execute("select.modeVertex");
        ctx.Sel.Add(ElementRef.Vertex(ctx.Doc.Meshes[0].Id, 2));
        ctx.Sel.Add(ElementRef.Vertex(ctx.Doc.Meshes[0].Id, 6)); // already an edge
        ctx.Commands.Execute("mesh.connect");
        Assert.Null(ctx.Session.UndoLabel);
        Assert.True(ctx.StatusIsError);

        ctx.Commands.Execute("mesh.weld");
        Assert.Null(ctx.Session.UndoLabel);
        Assert.Contains("1 mm", ctx.StatusMessage);
    }

    [Fact]
    public void Inset_previews_on_start_and_esc_restores_everything()
    {
        var ctx = WithBoxes(1);
        SelectFaces(ctx, 3);
        Assert.True(ctx.Commands.Execute("mesh.inset"));
        Assert.Equal("inset", ctx.ActiveTool!.Id);
        Assert.True(ctx.Interacting);
        Assert.Equal(10, ctx.Doc.Meshes[0].Faces.Count);

        ctx.Commands.Execute("edit.cancel");
        Assert.Equal("select", ctx.ActiveTool!.Id);
        Assert.Equal(6, ctx.Doc.Meshes[0].Faces.Count);
        Assert.Null(ctx.Session.UndoLabel);
        Assert.Contains(ElementRef.Face(ctx.Doc.Meshes[0].Id, 3), ctx.Sel.Items);
    }

    [Fact]
    public void Inset_commit_is_one_undo_step_and_returns_to_select()
    {
        var ctx = WithBoxes(1);
        SelectFaces(ctx, 3);
        ctx.Commands.Execute("mesh.outset");
        var tool = Assert.IsType<InsetTool>(ctx.ActiveTool);
        Assert.Equal("Outset", tool.Name);
        tool.SetAmount(ctx, 0.5f);
        tool.Commit(ctx);
        Assert.Equal("select", ctx.ActiveTool!.Id);
        Assert.Equal("Outset", ctx.Session.UndoLabel);
        Assert.Equal(-0.5f, ctx.Doc.Meshes[0].Bounds().Min.X, 4);
        ctx.Commands.Execute("edit.undo");
        Assert.Equal(6, ctx.Doc.Meshes[0].Faces.Count);
        Assert.Null(ctx.Session.UndoLabel);
    }

    [Fact]
    public void Offset_previews_commits_as_one_step_and_esc_restores()
    {
        var ctx = WithBoxes(1);
        SelectFaces(ctx, 1, 5);
        Assert.True(ctx.Commands.Execute("mesh.offset"));
        var tool = Assert.IsType<OffsetTool>(ctx.ActiveTool);
        Assert.True(ctx.Interacting);
        ctx.Commands.Execute("edit.cancel");
        Assert.Equal(6, ctx.Doc.Meshes[0].Faces.Count);
        Assert.Null(ctx.Session.UndoLabel);

        ctx.Commands.Execute("mesh.offset");
        tool.SetAmount(ctx, 0.125f);
        tool.Commit(ctx);
        Assert.Equal("select", ctx.ActiveTool!.Id);
        Assert.Equal("Offset", ctx.Session.UndoLabel);
        Assert.Equal(2.125f, ctx.Doc.Meshes[0].Bounds().Max.X, 4);
        Assert.Equal(2.125f, ctx.Doc.Meshes[0].Bounds().Max.Z, 4);
        Assert.Contains(ElementRef.Face(ctx.Doc.Meshes[0].Id, 1), ctx.Sel.Items); // the moved faces stay selected
        ctx.Commands.Execute("edit.undo");
        Assert.Equal(6, ctx.Doc.Meshes[0].Faces.Count);
    }

    [Fact]
    public void Tags_popup_is_ctrl_t_and_only_for_faces_or_objects()
    {
        var ctx = WithBoxes(1);
        var cmd = ctx.Commands.Find("tags.edit")!;
        Assert.Contains(cmd.Hotkeys, h => h.ToString() == "Ctrl+T");
        ctx.Commands.Execute("select.modeVertex");
        Assert.False(cmd.Enabled);
        SelectFaces(ctx, 1);
        Assert.True(cmd.Enabled);
        ctx.Commands.Execute("select.modeObject");
        Assert.True(cmd.Enabled);
    }

    [Fact]
    public void Merge_meshes_then_detach_and_extract()
    {
        var ctx = WithBoxes(2);
        ctx.Commands.Execute("select.all");
        Assert.True(ctx.Commands.Execute("mesh.mergeMeshes"));
        var merged = Assert.Single(ctx.Doc.Meshes);
        Assert.Equal(12, merged.Faces.Count);

        SelectFaces(ctx, 3);
        Assert.True(ctx.Commands.Execute("mesh.extract"));
        Assert.Equal(2, ctx.Doc.Meshes.Count);
        Assert.Equal(12, ctx.Doc.Meshes[0].Faces.Count);
        Assert.All(ctx.Sel.Items, i => Assert.Equal(ctx.Doc.Meshes[1].Id, i.Mesh));

        ctx.Sel.Clear();
        ctx.Sel.Add(ElementRef.Face(ctx.Doc.Meshes[0].Id, 3));
        Assert.True(ctx.Commands.Execute("mesh.detach"));
        Assert.Equal(3, ctx.Doc.Meshes.Count);
        Assert.Equal(11, ctx.Doc.Meshes[0].Faces.Count);

        ctx.Commands.Execute("edit.undo");
        ctx.Commands.Execute("edit.undo");
        ctx.Commands.Execute("edit.undo");
        Assert.Equal(2, ctx.Doc.Meshes.Count);
    }

    [Fact]
    public void Bridge_and_merge_faces_and_mirror_run_from_commands()
    {
        var ctx = WithBoxes(2);
        ctx.Commands.Execute("select.all");
        ctx.Commands.Execute("mesh.mergeMeshes");
        SelectFaces(ctx, 1, 6);
        Assert.True(ctx.Commands.Execute("mesh.bridge"));
        Assert.Equal(14, ctx.Doc.Meshes[0].Faces.Count);
        Assert.Equal(4, ctx.Sel.Count);

        ctx.Commands.Execute("select.modeObject");
        ctx.Commands.Execute("select.all");
        var before = ctx.Doc.Meshes[0].Bounds();
        Assert.True(ctx.Commands.Execute("mesh.mirror"));
        Assert.Equal(before.Min, ctx.Doc.Meshes[0].Bounds().Min); // symmetric about its own pivot
        Assert.Equal("Mirror", ctx.Session.UndoLabel);
    }
}
