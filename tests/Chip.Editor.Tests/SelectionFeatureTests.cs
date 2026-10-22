using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Scene;
using Chip.Editor.Tools;

namespace Chip.Editor.Tests;

/// <summary>Stream D commands driven through the registry on a headless context.</summary>
public class SelectionFeatureTests
{
    /// <summary>3×3 quads on XZ; vertex (i, j) = j*4+i, face (i, j) = j*3+i.</summary>
    static PolyMesh Plane()
    {
        var m = new PolyMesh();
        for (int j = 0; j <= 3; j++)
            for (int i = 0; i <= 3; i++) m.Positions.Add(new Vector3(i, 0, j));
        for (int j = 0; j < 3; j++)
            for (int i = 0; i < 3; i++)
                m.Faces.Add(new Face([j * 4 + i, (j + 1) * 4 + i, (j + 1) * 4 + i + 1, j * 4 + i + 1], "dev_a", default));
        return m;
    }

    static (Framework.EditorContext Ctx, PolyMesh Mesh) Setup(SelectMode mode)
    {
        var doc = new Document();
        var mesh = doc.Add(Plane());
        var ctx = RegistryTests.Headless(doc);
        ctx.SetMode(mode);
        return (ctx, ctx.Doc.Find(mesh.Id)!);
    }

    [Fact]
    public void Loop_and_ring_need_selected_edges_and_extend_them()
    {
        var (ctx, m) = Setup(SelectMode.Edge);
        Assert.False(ctx.Commands.Find("select.loop")!.Enabled);
        ctx.Sel.Add(ElementRef.EdgeOf(m.Id, new Edge(5, 9)));
        Assert.True(ctx.Commands.Execute("select.loop"));
        Assert.Equal(3, ctx.Sel.Count);
        ctx.Sel.Clear();
        ctx.Sel.Add(ElementRef.EdgeOf(m.Id, new Edge(5, 6)));
        Assert.True(ctx.Commands.Execute("select.ring"));
        Assert.Equal(4, ctx.Sel.Count);
    }

    [Fact]
    public void Grow_shrink_connected_and_by_material_run_from_the_menu()
    {
        var (ctx, m) = Setup(SelectMode.Face);
        ctx.Sel.Add(ElementRef.Face(m.Id, 4));
        ctx.Commands.Execute("select.grow");
        Assert.Equal(5, ctx.Sel.Count);
        ctx.Commands.Execute("select.shrink");
        Assert.Single(ctx.Sel.Items);
        ctx.Commands.Execute("select.connected");
        Assert.Equal(9, ctx.Sel.Count);

        ctx.Doc.Find(m.Id)!.Faces[0].Material = "dev_b";
        ctx.Sel.Clear();
        ctx.Sel.Add(ElementRef.Face(m.Id, 0));
        ctx.Commands.Execute("select.byMaterial");
        Assert.Single(ctx.Sel.Items);
        ctx.Sel.Clear();
        ctx.Commands.Execute("select.byMaterial"); // no active face: the active material (dev_a)
        Assert.Equal(8, ctx.Sel.Count);
    }

    [Fact]
    public void Selection_sets_save_and_restore_from_the_menu()
    {
        var (ctx, m) = Setup(SelectMode.Face);
        Assert.False(ctx.Commands.Find("select.restoreSet")!.Enabled);
        ctx.Sel.Add(ElementRef.Face(m.Id, 2));
        ctx.Commands.Execute("select.saveSet");
        ctx.SetMode(SelectMode.Vertex);
        ctx.Sel.Clear();
        Assert.Equal("Restore Set 1", ctx.Commands.Find("select.restoreSet")!.CurrentLabel);
        ctx.Commands.Execute("select.restoreSet");
        Assert.Equal(SelectMode.Face, ctx.Sel.Mode);
        Assert.Equal(ElementRef.Face(m.Id, 2), ctx.Sel.Active);
        Assert.Single(Chip.Editor.Features.SelectionTools.SelectionSets.For(ctx).All); // listed under the outliner, in the Scene tab
    }

    [Fact]
    public void Visible_only_toggles_the_select_tool()
    {
        var (ctx, _) = Setup(SelectMode.Vertex);
        var tool = ctx.Tools.OfType<SelectTool>().Single();
        Assert.False(tool.VisibleOnly);
        ctx.Commands.Execute("select.visibleOnly");
        Assert.True(tool.VisibleOnly);
        Assert.True(ctx.Commands.Find("select.visibleOnly")!.IsChecked!());
    }
}
