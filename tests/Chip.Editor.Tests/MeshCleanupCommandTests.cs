using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using Chip.Editor.Framework;

namespace Chip.Editor.Tests;

/// <summary>Dissolve, check and clean up through a headless context: one undo step each, none when nothing happens.</summary>
public class MeshCleanupCommandTests
{
    /// <summary>A floor (face 0) and wall (face 1) meeting along z = 0, with a removed door's corners (4, 5) left in the floor's edge.</summary>
    static EditorContext DoorLeftovers()
    {
        var m = new PolyMesh();
        m.Positions.AddRange([new(0, 0, 0), new(0, 0, 4), new(4, 0, 4), new(4, 0, 0), new(2.5f, 0, 0), new(1.5f, 0, 0), new(4, 3, 0), new(0, 3, 0)]);
        m.Faces.Add(new Face([0, 1, 2, 3, 4, 5], "dev_a", default));
        m.Faces.Add(new Face([0, 3, 6, 7], "dev_a", default));
        var doc = new Document();
        doc.Add(m);
        return RegistryTests.Headless(doc);
    }

    static PolyMesh Mesh(EditorContext ctx) => ctx.Doc.Meshes[0];

    [Fact]
    public void Dissolve_removes_the_stray_vertices_in_one_undo_step()
    {
        var ctx = DoorLeftovers();
        ctx.Commands.Execute("select.modeVertex");
        Assert.False(ctx.Commands.Find("mesh.dissolve")!.Enabled);
        ctx.Sel.Add(ElementRef.Vertex(Mesh(ctx).Id, 4));
        ctx.Sel.Add(ElementRef.Vertex(Mesh(ctx).Id, 5));
        Assert.True(ctx.Commands.Execute("mesh.dissolve"));
        Assert.Equal("Dissolve Vertices", ctx.Session.UndoLabel);
        Assert.Equal(4, Mesh(ctx).Faces[0].Verts.Length);
        Assert.Empty(MeshOps.Check(Mesh(ctx)));
        Assert.True(ctx.Sel.IsEmpty);
        Assert.Contains("2 vertices", ctx.StatusMessage);

        ctx.Commands.Execute("edit.undo");
        Assert.Equal(6, Mesh(ctx).Faces[0].Verts.Length);
    }

    [Fact]
    public void Dissolving_a_box_corner_says_why_not_and_leaves_no_step()
    {
        var doc = new Document();
        doc.Add(MeshOps.CreateBox(Vector3.Zero, new Vector3(2), "dev_a", TextureMapping.Default));
        var ctx = RegistryTests.Headless(doc);
        ctx.Commands.Execute("select.modeVertex");
        ctx.Sel.Add(ElementRef.Vertex(Mesh(ctx).Id, 0));
        ctx.Commands.Execute("mesh.dissolve");
        Assert.Null(ctx.Session.UndoLabel);
        Assert.True(ctx.StatusIsError);
        Assert.Contains("one plane", ctx.StatusMessage);
    }

    [Fact]
    public void Dissolving_an_edge_merges_the_faces_on_it()
    {
        var doc = new Document();
        doc.Add(MeshOps.CreateBox(Vector3.Zero, new Vector3(2), "dev_a", TextureMapping.Default));
        var ctx = RegistryTests.Headless(doc);
        var box = Mesh(ctx);
        MeshOps.ConnectVertices(box, [2, 7]); // split the top, then dissolve the split
        ctx.Commands.Execute("select.modeEdge");
        ctx.Sel.Add(ElementRef.EdgeOf(box.Id, new Edge(2, 7)));
        Assert.True(ctx.Commands.Execute("mesh.dissolve"));
        Assert.Equal("Dissolve Edges", ctx.Session.UndoLabel);
        Assert.Equal(6, box.Faces.Count);
    }

    [Fact]
    public void Check_selects_the_t_junctions_and_says_how_to_fix_them()
    {
        var ctx = DoorLeftovers();
        Assert.True(ctx.Commands.Execute("mesh.check"));
        Assert.Equal(SelectMode.Vertex, ctx.Sel.Mode);
        Assert.Equal([4, 5], ctx.Sel.Items.Select(i => i.A).Order());
        Assert.Contains("2 T-junctions", ctx.StatusMessage);
        Assert.Contains("Clean up mesh", ctx.StatusMessage);
        Assert.Null(ctx.Session.UndoLabel); // checking edits nothing
    }

    [Fact]
    public void Clean_up_fixes_the_mesh_in_one_step_and_then_has_nothing_to_do()
    {
        var ctx = DoorLeftovers();
        ctx.Sel.Add(ElementRef.Object(Mesh(ctx).Id));
        Assert.True(ctx.Commands.Execute("mesh.cleanUp"));
        Assert.Equal("Clean Up Mesh", ctx.Session.UndoLabel);
        Assert.Equal("Sewed 2 T-junctions, dissolved 2 stray vertices", ctx.StatusMessage);
        Assert.Empty(MeshOps.Check(Mesh(ctx)));

        ctx.Commands.Execute("edit.undo");
        Assert.Equal(2, MeshOps.Check(Mesh(ctx)).Count);
        ctx.Commands.Execute("edit.redo");
        ctx.Commands.Execute("mesh.cleanUp");
        Assert.Contains("Nothing to clean up", ctx.StatusMessage);
        Assert.Equal("Clean Up Mesh", ctx.Session.UndoLabel);
        ctx.Commands.Execute("edit.undo");
        Assert.Null(ctx.Session.UndoLabel); // only the one step was recorded

        ctx.Commands.Execute("mesh.check");
        Assert.Contains("2 T-junctions", ctx.StatusMessage);
    }
}
