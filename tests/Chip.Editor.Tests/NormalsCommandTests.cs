using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Geometry.Primitives;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using Chip.Editor.Features.Normals;
using Chip.Editor.Features.View;
using Chip.Editor.Framework;

namespace Chip.Editor.Tests;

/// <summary>Stream L commands through a headless context: scope per selection mode, one undo step, no step when nothing changes.</summary>
public class NormalsCommandTests
{
    static EditorContext WithBox(out PolyMesh box)
    {
        var doc = new Document();
        box = doc.Add(MeshOps.CreateBox(Vector3.Zero, new Vector3(2, 2, 2), "dev_a", TextureMapping.Default));
        return RegistryTests.Headless(doc);
    }

    static PolyMesh Mesh(EditorContext ctx) => ctx.Doc.Meshes[0];

    static int FaceFacing(PolyMesh m, Vector3 dir) => Enumerable.Range(0, m.Faces.Count).First(f => Vector3.Dot(m.FaceNormal(f), dir) > 0.99f);

    [Fact]
    public void Hotkeys_follow_the_plan()
    {
        var ctx = RegistryTests.Headless();
        Assert.Equal("J", ctx.Commands.Find("mesh.softNormals")!.HotkeyText);
        Assert.Equal("Ctrl+J", ctx.Commands.Find("mesh.hardNormals")!.HotkeyText);
        Assert.Equal("K", ctx.Commands.Find("mesh.dominantNormals")!.HotkeyText);
        Assert.Equal("Mesh", ctx.Commands.Find("mesh.autoSmooth")!.Menu);
        Assert.Equal("Mesh", ctx.Commands.Find("mesh.clearSmoothing")!.Menu);
        Assert.Equal("View", ctx.Commands.Find("view.showNormals")!.Menu);
        Assert.Contains(ctx.Sections, s => s.Id == "smoothing");
    }

    [Fact]
    public void Soft_on_objects_smooths_the_whole_mesh_and_undoes_in_one_step()
    {
        var ctx = WithBox(out var box);
        Assert.False(ctx.Commands.Find("mesh.softNormals")!.Enabled); // nothing selected
        ctx.Sel.Add(ElementRef.Object(box.Id));
        Assert.True(ctx.Commands.Execute("mesh.softNormals"));
        Assert.Equal(12, Mesh(ctx).SoftEdges.Count);
        Assert.Equal("Soft Normals", ctx.Session.UndoLabel);
        ctx.Commands.Execute("edit.undo");
        Assert.Empty(Mesh(ctx).SoftEdges);
        Assert.Null(ctx.Session.UndoLabel);
    }

    [Fact]
    public void Edge_mode_acts_on_the_selected_edges_only()
    {
        var ctx = WithBox(out var box);
        ctx.Commands.Execute("select.modeEdge");
        var edges = box.Edges().Take(2).ToList();
        foreach (var e in edges) ctx.Sel.Add(ElementRef.EdgeOf(box.Id, e));
        ctx.Commands.Execute("mesh.softNormals");
        Assert.Equal(edges.ToHashSet(), Mesh(ctx).SoftEdges);

        ctx.Sel.Clear();
        ctx.Sel.Add(ElementRef.EdgeOf(box.Id, edges[0]));
        ctx.Commands.Execute("mesh.hardNormals");
        Assert.Equal([edges[1]], Mesh(ctx).SoftEdges);
        Assert.Equal("Hard Normals", ctx.Session.UndoLabel);
    }

    [Fact]
    public void Face_mode_covers_inner_and_outline_edges()
    {
        var ctx = WithBox(out var box);
        ctx.Commands.Execute("select.modeFace");
        int top = FaceFacing(box, Vector3.UnitY), front = FaceFacing(box, Vector3.UnitZ);
        ctx.Sel.Add(ElementRef.Face(box.Id, top));
        ctx.Sel.Add(ElementRef.Face(box.Id, front));
        ctx.Commands.Execute("mesh.softNormals");
        Assert.Equal(7, Mesh(ctx).SoftEdges.Count); // 4 + 4 minus the shared one
    }

    [Fact]
    public void Repeating_a_command_that_changes_nothing_leaves_no_undo_step()
    {
        var ctx = WithBox(out var box);
        ctx.Sel.Add(ElementRef.Object(box.Id));
        ctx.Commands.Execute("mesh.hardNormals");
        Assert.Null(ctx.Session.UndoLabel);
        Assert.Contains("Already hard", ctx.StatusMessage);
        Assert.False(ctx.StatusIsError);
        ctx.Commands.Execute("mesh.clearSmoothing");
        Assert.Null(ctx.Session.UndoLabel);
    }

    [Fact]
    public void Dominant_needs_faces_and_hard_clears_it()
    {
        var ctx = WithBox(out var box);
        ctx.Sel.Add(ElementRef.Object(box.Id));
        Assert.False(ctx.Commands.Find("mesh.dominantNormals")!.Enabled);
        ctx.Commands.Execute("select.modeFace");
        ctx.Sel.Clear();
        int top = FaceFacing(box, Vector3.UnitY);
        ctx.Sel.Add(ElementRef.Face(box.Id, top));
        Assert.True(ctx.Commands.Execute("mesh.dominantNormals"));
        Assert.True(Mesh(ctx).Faces[top].Dominant);
        Assert.Empty(Mesh(ctx).SoftEdges); // right-angle neighbours stay hard
        Assert.Equal("Dominant Normals", ctx.Session.UndoLabel);

        ctx.Commands.Execute("mesh.dominantNormals");
        Assert.Equal("Dominant Normals", ctx.Session.UndoLabel); // still one step: the repeat did nothing
        ctx.Commands.Execute("edit.undo");
        Assert.False(Mesh(ctx).Faces[top].Dominant);
        ctx.Commands.Execute("edit.redo");

        ctx.Commands.Execute("mesh.hardNormals");
        Assert.False(Mesh(ctx).Faces[top].Dominant);
        Assert.Empty(Mesh(ctx).SoftEdges);
        Assert.Equal("Hard Normals", ctx.Session.UndoLabel);
    }

    [Fact]
    public void Dominant_softens_the_chamfer_next_to_the_selected_faces()
    {
        var ctx = WithBox(out var box);
        var m = Mesh(ctx);
        var edge = new Edge(m.Positions.FindIndex(p => p == new Vector3(0, 2, 2)), m.Positions.FindIndex(p => p == new Vector3(2, 2, 2)));
        MeshOps.BevelEdges(m, [edge], 0.25f, 1);
        ctx.Commands.Execute("select.modeFace");
        ctx.Sel.Add(ElementRef.Face(box.Id, FaceFacing(m, Vector3.UnitY)));
        ctx.Sel.Add(ElementRef.Face(box.Id, FaceFacing(m, Vector3.UnitZ)));
        ctx.Commands.Execute("mesh.dominantNormals");
        Assert.Equal(2, Mesh(ctx).SoftEdges.Count);
        Assert.Equal(2, Mesh(ctx).Faces.Count(f => f.Dominant));
    }

    [Fact]
    public void Auto_smooth_uses_the_section_angle_and_clear_flattens()
    {
        var doc = new Document();
        var cyl = doc.Add(Primitive.Build(PrimitiveShape.Cylinder, new PrimitiveOptions { Sides = 16, Smooth = false },
            PrimitiveFrame.FromBlock(Vector3.Zero, new Vector3(4, 0, 4), Vector3.UnitY, 3), "dev_a", TextureMapping.Default));
        var ctx = RegistryTests.Headless(doc);
        ctx.Sel.Add(ElementRef.Object(cyl.Id));
        var feature = ((SmoothingSection)ctx.Sections.First(s => s.Id == "smoothing")).Feature;
        feature.SmoothAngle = 20; // below the cylinder's 22.5° steps: nothing to soften
        ctx.Commands.Execute("mesh.autoSmooth");
        Assert.Empty(Mesh(ctx).SoftEdges);
        Assert.Null(ctx.Session.UndoLabel);

        feature.SmoothAngle = 45;
        Assert.True(ctx.Commands.Execute("mesh.autoSmooth"));
        Assert.Equal(16, Mesh(ctx).SoftEdges.Count);
        Assert.Equal("Auto-smooth", ctx.Session.UndoLabel);
        Assert.Contains("45°", ctx.Commands.Find("mesh.autoSmooth")!.CurrentLabel);

        ctx.Commands.Execute("mesh.clearSmoothing");
        Assert.Empty(Mesh(ctx).SoftEdges);
        Assert.Equal("Clear Smoothing", ctx.Session.UndoLabel);
        ctx.Commands.Execute("edit.undo");
        Assert.Equal(16, Mesh(ctx).SoftEdges.Count);
    }

    [Fact]
    public void Show_normals_toggles_the_view_setting()
    {
        var ctx = RegistryTests.Headless();
        Assert.False(ViewSettings.Of(ctx).ShowNormals);
        ctx.Commands.Execute("view.showNormals");
        Assert.True(ViewSettings.Of(ctx).ShowNormals);
        Assert.True(ctx.Commands.Find("view.showNormals")!.IsChecked!());
    }
}
