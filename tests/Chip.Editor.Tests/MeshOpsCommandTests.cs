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
    public void Arc_extrude_previews_commits_as_one_step_and_esc_restores()
    {
        var ctx = WithBoxes(1);
        int id = ctx.Doc.Meshes[0].Id;
        SelectFaces(ctx, 3); // top
        Assert.True(ctx.Commands.Execute("mesh.arcExtrude"));
        var tool = Assert.IsType<ArcExtrudeTool>(ctx.ActiveTool);
        Assert.True(ctx.Interacting);
        Assert.True(ctx.Doc.Meshes[0].Faces.Count > 6); // live from the start
        ctx.Commands.Execute("edit.cancel");
        Assert.Equal(6, ctx.Doc.Meshes[0].Faces.Count);
        Assert.Null(ctx.Session.UndoLabel);
        Assert.Contains(ElementRef.Face(id, 3), ctx.Sel.Items);

        ctx.Commands.Execute("mesh.arcExtrude");
        tool.BendDirection = Vector3.UnitX;
        tool.Radius = 2;
        tool.AngleDegrees = 90;
        tool.Segments = 3;
        tool.Smooth = false;
        tool.Commit(ctx);
        Assert.Equal("select", ctx.ActiveTool!.Id);
        Assert.Equal("Arc Extrude", ctx.Session.UndoLabel);
        var m = ctx.Doc.Meshes[0];
        Assert.Equal(18, m.Faces.Count);
        Assert.True(Vector3.Distance(Vector3.UnitX, m.FaceNormal(3)) < 1e-4f);
        Assert.Equal([ElementRef.Face(id, 3)], ctx.Sel.Items); // the cap stays selected to keep going
        ctx.Commands.Execute("edit.undo");
        Assert.Equal(6, ctx.Doc.Meshes[0].Faces.Count);
    }

    [Fact]
    public void Arc_extrude_leaves_a_corridor_end_forward_even_when_the_end_faces_inward()
    {
        var doc = new Document();
        var corridor = MeshOps.CreateBox(Vector3.Zero, new Vector3(2, 2, 10), "dev_a", TextureMapping.Default);
        MeshOps.FlipFaces(corridor, [5]); // the +Z end now faces back into the corridor
        doc.Add(corridor);
        var ctx = RegistryTests.Headless(doc);
        SelectFaces(ctx, 5);

        ctx.Commands.Execute("mesh.arcExtrude");
        var tool = Assert.IsType<ArcExtrudeTool>(ctx.ActiveTool);
        tool.BendDirection = Vector3.UnitX;
        tool.InnerRadius = 2; // measured from the corridor's +X wall, a metre out from its centre
        Assert.Equal(3, tool.Radius, 4);
        tool.Refresh(ctx);
        var m = ctx.Doc.Meshes[0];
        Assert.True(m.Bounds().Max.Z > 12, $"swept backward: {m.Bounds()}");
        Assert.True(m.Bounds().Min.Z >= -1e-4f);

        tool.Reverse = true; // Tab: back into the corridor
        tool.Refresh(ctx);
        Assert.True(m.Bounds().Max.Z <= 10 + 1e-4f);
        ctx.Commands.Execute("edit.cancel");
        Assert.Equal(6, ctx.Doc.Meshes[0].Faces.Count);
    }

    [Fact]
    public void Arc_extrude_needs_faces_or_open_edges()
    {
        var ctx = WithBoxes(1);
        ctx.Commands.Execute("select.modeVertex");
        ctx.Sel.Add(ElementRef.Vertex(ctx.Doc.Meshes[0].Id, 0));
        Assert.False(ctx.Commands.Find("mesh.arcExtrude")!.Enabled);
        ctx.Commands.Execute("select.modeEdge");
        ctx.Sel.Clear();
        ctx.Sel.Add(ElementRef.EdgeOf(ctx.Doc.Meshes[0].Id, new Edge(0, 1))); // a box has no open edges
        ctx.Commands.Execute("mesh.arcExtrude");
        Assert.False(ctx.Interacting);
        Assert.Equal("select", ctx.ActiveTool!.Id);
        Assert.Null(ctx.Session.UndoLabel);
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

    [Fact]
    public void Fill_caps_the_hole_from_one_rim_edge_and_selects_the_new_face()
    {
        var ctx = WithBoxes(1);
        var mesh = ctx.Doc.Meshes[0];
        MeshOps.DeleteFaces(mesh, [3]); // the top
        ctx.Commands.Execute("select.modeEdge");
        ctx.Sel.Add(ElementRef.EdgeOf(mesh.Id, new Edge(0, 1))); // a closed edge: nothing to fill
        Assert.True(ctx.Commands.Execute("mesh.fill"));
        Assert.Equal(5, mesh.Faces.Count);
        Assert.Null(ctx.Session.UndoLabel);

        mesh = ctx.Doc.Meshes[0]; // the rollback restored a copy
        ctx.Sel.Clear();
        ctx.Sel.Add(ElementRef.EdgeOf(mesh.Id, new Edge(2, 6)));
        Assert.True(ctx.Commands.Execute("mesh.fill"));
        Assert.Equal(6, mesh.Faces.Count);
        Assert.Equal(SelectMode.Face, ctx.Sel.Mode);
        Assert.Equal([ElementRef.Face(mesh.Id, 5)], ctx.Sel.Items);
        Assert.Equal("Fill", ctx.Session.UndoLabel);
        ctx.Commands.Execute("edit.undo");
        Assert.Equal(5, ctx.Doc.Meshes[0].Faces.Count);
        Assert.Equal(SelectMode.Edge, ctx.Sel.Mode);
    }
}
