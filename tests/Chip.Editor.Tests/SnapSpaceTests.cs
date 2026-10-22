using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Picking;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using Chip.Editor.Framework;
using Chip.Editor.Settings;
using Chip.Editor.Viewport;

namespace Chip.Editor.Tests;

/// <summary>World and relative snap space: gizmo moves, nudges, the toggle, the grid steps and the saved default.</summary>
public class SnapSpaceTests
{
    /// <summary>Front view centred on the origin, 1 px = 1 cm: screen x is world +X, screen up is world +Y.</summary>
    static ViewInfo Front() => new Camera { Projection = Projection.Front, Position = Vector3.Zero, OrthoHeight = 10 }.View(new Vector2(1000, 1000));

    static readonly Vector2 Center = new(500, 500);

    static void Near(Vector3 expected, Vector3 actual, float tol = 1e-3f) =>
        Assert.True(Vector3.Distance(expected, actual) < tol, $"expected {expected}, got {actual}");

    static Vector3 Drag(Gizmo g, Handle h, Vector2 to)
    {
        var v = Front();
        g.BeginDrag(h, v, Vector3.Zero, Center);
        var t = g.DragTransform(v, to, 0.25f, snap: true).M.Translation;
        g.EndDrag();
        return t;
    }

    [Fact]
    public void World_space_lands_the_low_corner_on_a_grid_line_and_relative_keeps_its_offset()
    {
        // The selection's low corner sits at x = 0.13; a 1 m drag right.
        var g = new Gizmo { AnchorHook = () => new Vector3(0.13f, 0, 0) };
        Assert.Equal(SnapSpace.World, g.SnapSpace);
        Near(new Vector3(1.12f, 0, 0), Drag(g, Handle.X, new Vector2(600, 500))); // 0.13 + 1.12 = 1.25
        g.SnapSpace = SnapSpace.Relative;
        Near(new Vector3(1, 0, 0), Drag(g, Handle.X, new Vector2(600, 500))); // 0.13 + 1 = 1.13, still off the grid
    }

    [Fact]
    public void A_plane_drag_snaps_both_its_axes_and_leaves_the_third_alone()
    {
        var g = new Gizmo { AnchorHook = () => new Vector3(0.13f, 0.07f, 0.31f) };
        var t = Drag(g, Handle.PlaneXY, new Vector2(600, 400));
        Near(new Vector3(1.12f, 0.93f, 0), t); // lands on (1.25, 1.0); z untouched though 0.31 is off the grid
    }

    [Fact]
    public void A_temporary_pivot_is_what_lands_on_the_grid()
    {
        var g = new Gizmo { PivotHook = () => new Vector3(0.2f, 0, 0), AnchorHook = () => new Vector3(0.13f, 0, 0) };
        Near(new Vector3(1.05f, 0, 0), Drag(g, Handle.X, new Vector2(600, 500))); // the pivot: 0.2 + 1.05 = 1.25
    }

    [Fact]
    public void An_axis_off_the_world_grid_moves_in_whole_steps()
    {
        var g = new Gizmo { AnchorHook = () => new Vector3(0.13f, 0, 0) };
        var x = Vector3.Normalize(new Vector3(1, 1, 0));
        g.SetBasis(x, Vector3.Normalize(new Vector3(-1, 1, 0)), Vector3.UnitZ);
        var t = Drag(g, Handle.X, new Vector2(571, 429)); // about 1 m along the diagonal
        float along = Vector3.Dot(t, x);
        Assert.Equal(MathF.Round(along / 0.25f) * 0.25f, along, 3);
        Assert.Equal(1f, along, 3);
    }

    [Fact]
    public void Pressing_the_gizmo_without_moving_moves_nothing()
    {
        var g = new Gizmo { AnchorHook = () => new Vector3(0.13f, 0.07f, 0) };
        Assert.Equal(Vector3.Zero, Drag(g, Handle.Center, Center));
        Assert.Equal(Vector3.Zero, Drag(g, Handle.X, Center + new Vector2(1, 0)));
    }

    [Fact]
    public void Steps_below_25_cm_are_round_numbers_with_majors_they_divide()
    {
        Assert.Equal([0.01f, 0.05f, 0.1f, 0.25f, 0.5f, 1, 2, 4, 8, 16], EditorContext.GridSizes);
        Assert.Equal(0.1f, EditorContext.MajorGrid(0.01f));
        Assert.Equal(0.25f, EditorContext.MajorGrid(0.05f));
        Assert.Equal(0.5f, EditorContext.MajorGrid(0.1f));
        Assert.Equal(1f, EditorContext.MajorGrid(0.25f));
        Assert.Equal(64f, EditorContext.MajorGrid(16));

        var ctx = RegistryTests.Headless();
        ctx.Grid = 0.25f;
        ctx.Commands.Execute("view.gridSmaller");
        Assert.Equal(0.1f, ctx.Grid);
        ctx.Commands.Execute("view.gridSmaller");
        Assert.Equal(0.05f, ctx.Grid);
    }

    /// <summary>A box from x = 0.13 to 1.13, viewed from the front so nudging right is +X.</summary>
    static EditorContext OffGridBox()
    {
        var doc = new Document();
        doc.Add(MeshOps.CreateBox(new Vector3(0.13f, 0, 0), new Vector3(1.13f, 1, 1), "dev_a", TextureMapping.Default));
        var ctx = RegistryTests.Headless(doc);
        ctx.Camera.Position = new Vector3(0.5f, 0.5f, 10);
        ctx.Camera.LookAt(new Vector3(0.5f, 0.5f, 0));
        ctx.Sel.Add(ElementRef.Object(doc.Meshes[0].Id));
        ctx.Grid = 0.25f;
        return ctx;
    }

    [Fact]
    public void Nudging_in_world_space_goes_to_the_next_grid_line_then_whole_steps()
    {
        var ctx = OffGridBox();
        ctx.Commands.Execute("edit.nudgeRight");
        Assert.Equal(0.25f, ctx.SelectionBounds()!.Value.Min.X, 4);
        ctx.Commands.Execute("edit.nudgeRight");
        Assert.Equal(0.5f, ctx.SelectionBounds()!.Value.Min.X, 4);
        ctx.Commands.Execute("edit.nudgeLeft");
        ctx.Commands.Execute("edit.nudgeLeft");
        Assert.Equal(0f, ctx.SelectionBounds()!.Value.Min.X, 4);
    }

    [Fact]
    public void Nudging_in_relative_space_keeps_the_offset()
    {
        var ctx = OffGridBox();
        ctx.Commands.Execute("view.snapSpace");
        Assert.Equal(SnapSpace.Relative, ctx.SnapSpace);
        Assert.Contains("Relative", ctx.StatusMessage);
        ctx.Commands.Execute("edit.nudgeRight");
        Assert.Equal(0.38f, ctx.SelectionBounds()!.Value.Min.X, 4);
    }

    [Fact]
    public void The_toggle_is_on_backslash_and_flips_the_gizmo()
    {
        var ctx = RegistryTests.Headless();
        var cmd = ctx.Commands.Find("view.snapSpace")!;
        Assert.Equal(Hotkey.Parse("\\"), cmd.Hotkeys.Single());
        Assert.True(cmd.IsChecked!());
        ctx.Commands.Execute("view.snapSpace");
        Assert.Equal(SnapSpace.Relative, ctx.Gizmo.SnapSpace);
        Assert.False(cmd.IsChecked!());
    }

    [Fact]
    public void Old_power_of_two_defaults_and_unknown_spaces_are_repaired()
    {
        var s = new AppSettings { GridDefault = 0.125f, SnapSpace = "Sideways" }.Normalize();
        Assert.Equal(0.25f, s.GridDefault);
        Assert.Equal("World", s.SnapSpace);
        Assert.Equal("Relative", new AppSettings { SnapSpace = "Relative" }.Normalize().SnapSpace);
    }
}
