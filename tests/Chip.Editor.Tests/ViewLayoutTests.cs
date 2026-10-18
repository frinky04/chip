using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using Chip.Editor.Features.View;
using Chip.Editor.Viewport;

namespace Chip.Editor.Tests;

/// <summary>Panes, layouts, projections, bookmarks and the view commands on a headless context.</summary>
public class ViewLayoutTests
{
    static Document Level()
    {
        var doc = new Document();
        doc.Add(MeshOps.CreateBox(Vector3.Zero, new Vector3(10, 1, 10), "dev_a", TextureMapping.Default));
        return doc;
    }

    [Fact]
    public void Four_pane_rects_tile_the_area_without_overlap()
    {
        var origin = new Vector2(10, 20);
        var size = new Vector2(801, 601);
        var rects = ViewportLayout.Rects(LayoutKind.Four, origin, size, 2);
        Assert.Equal(4, rects.Length);
        float area = rects.Sum(r => r.Size.X * r.Size.Y);
        Assert.True(area <= size.X * size.Y && area > size.X * size.Y * 0.99f);
        for (int i = 0; i < 4; i++)
            for (int j = i + 1; j < 4; j++)
            {
                var (a, b) = (rects[i], rects[j]);
                bool overlap = a.Origin.X < b.Origin.X + b.Size.X && b.Origin.X < a.Origin.X + a.Size.X
                    && a.Origin.Y < b.Origin.Y + b.Size.Y && b.Origin.Y < a.Origin.Y + a.Size.Y;
                Assert.False(overlap, $"panes {i} and {j} overlap");
            }
        Assert.Equal(origin.X + size.X, rects[1].Origin.X + rects[1].Size.X, 3);
        Assert.Equal(origin.Y + size.Y, rects[3].Origin.Y + rects[3].Size.Y, 3);
    }

    [Fact]
    public void Pane_hit_testing_finds_the_pane_under_the_cursor()
    {
        var ctx = RegistryTests.Headless(Level());
        var layout = ViewportLayout.Of(ctx);
        layout.SetKind(LayoutKind.Four);
        layout.Arrange(Vector2.Zero, new Vector2(800, 600), 2);
        Assert.Equal(0, layout.PaneAt(new Vector2(100, 100))!.Index);
        Assert.Equal(1, layout.PaneAt(new Vector2(700, 100))!.Index);
        Assert.Equal(2, layout.PaneAt(new Vector2(100, 500))!.Index);
        Assert.Equal(3, layout.PaneAt(new Vector2(700, 500))!.Index);
        Assert.Null(layout.PaneAt(new Vector2(399.5f, 100))); // the gap
        Assert.Null(layout.PaneAt(new Vector2(900, 100)));

        layout.SetKind(LayoutKind.Single);
        Assert.Equal(0, layout.PaneAt(new Vector2(700, 500))?.Index ?? 0);
    }

    [Fact]
    public void Activating_a_pane_makes_its_camera_the_context_camera()
    {
        var ctx = RegistryTests.Headless(Level());
        var layout = ViewportLayout.Of(ctx);
        var perspective = ctx.Camera;
        Assert.Same(perspective, layout.Panes[0].Camera);
        layout.SetKind(LayoutKind.Four);
        layout.Activate(layout.Panes[2]);
        Assert.Same(layout.Panes[2].Camera, ctx.Camera);
        Assert.Equal(Projection.Front, ctx.Camera.Projection);

        layout.SetKind(LayoutKind.Two); // pane 2 is no longer visible
        Assert.Same(perspective, ctx.Camera);
    }

    [Fact]
    public void Ortho_panes_frame_the_level_when_first_shown()
    {
        var ctx = RegistryTests.Headless(Level());
        var layout = ViewportLayout.Of(ctx);
        layout.SetKind(LayoutKind.Four);
        layout.Arrange(Vector2.Zero, new Vector2(800, 600), 2);
        var top = layout.Panes[1].Camera;
        Assert.Equal(new Vector3(5, 0, 5), top.Position);
        Assert.True(top.OrthoHeight >= 10);
    }

    [Fact]
    public void Switching_projection_remembers_each_camera()
    {
        var ctx = RegistryTests.Headless(Level());
        var layout = ViewportLayout.Of(ctx);
        var pane = layout.Active;
        pane.Camera.Position = new Vector3(1, 2, 3);
        pane.Camera.Yaw = 0.5f;
        Assert.True(ctx.Commands.Execute("view.top"));
        Assert.Equal(Projection.Top, ctx.Camera.Projection);
        ctx.Camera.OrthoHeight = 42;
        ctx.Commands.Execute("view.perspective");
        Assert.Equal(new Vector3(1, 2, 3), ctx.Camera.Position);
        Assert.Equal(0.5f, ctx.Camera.Yaw);
        ctx.Commands.Execute("view.top");
        Assert.Equal(42, ctx.Camera.OrthoHeight);
    }

    [Fact]
    public void Shading_cycles_and_layout_commands_apply()
    {
        var ctx = RegistryTests.Headless();
        var layout = ViewportLayout.Of(ctx);
        Assert.Equal(ViewMode.Lit, layout.Active.Mode);
        ctx.Commands.Execute("view.cycleMode");
        Assert.Equal(ViewMode.Unlit, layout.Active.Mode);
        ctx.Commands.Execute("view.modeUvChecker");
        ctx.Commands.Execute("view.cycleMode");
        Assert.Equal(ViewMode.Lit, layout.Active.Mode);
        ctx.Commands.Execute("view.layoutFour");
        Assert.Equal(LayoutKind.Four, layout.Kind);
        ctx.Commands.Execute("view.boundaryEdges");
        Assert.False(ViewSettings.Of(ctx).BoundaryEdges);
        ctx.Commands.Execute("view.rotationSnap45");
        Assert.Equal(45, ctx.Gizmo.RotateSnapDegrees);
        ctx.Commands.Execute("tool.axesLocal");
        Assert.Equal(GizmoOrientation.Local, ctx.Gizmo.Orientation);
    }

    [Fact]
    public void Bookmarks_store_and_recall_including_projection()
    {
        var ctx = RegistryTests.Headless(Level());
        ctx.Camera.Position = new Vector3(4, 5, 6);
        ctx.Commands.Execute("view.bookmarkStore3");
        ctx.Commands.Execute("view.side");
        ctx.Camera.Position = new Vector3(0, 9, 9);
        ctx.Commands.Execute("view.bookmarkStore7");

        ctx.Commands.Execute("view.bookmark3");
        Assert.Equal(Projection.Perspective, ctx.Camera.Projection);
        Assert.Equal(new Vector3(4, 5, 6), ctx.Camera.Position);
        ctx.Commands.Execute("view.bookmark7");
        Assert.Equal(Projection.Side, ctx.Camera.Projection);
        Assert.Equal(new Vector3(0, 9, 9), ctx.Camera.Position);

        ctx.Commands.Execute("view.bookmark5");
        Assert.Contains("empty", ctx.StatusMessage);
    }

    [Fact]
    public void Bookmarks_round_trip_through_json()
    {
        var b = new CameraBookmarks();
        b.Set(1, new CameraState(Projection.Perspective, new Vector3(1.5f, -2, 3.25f), 0.7f, -0.3f, 20));
        b.Set(9, new CameraState(Projection.Top, new Vector3(10, 0, -4), 0, 0, 64));
        var back = CameraBookmarks.FromJson(b.ToJson());
        Assert.Equal(2, back.Count);
        Assert.Equal(b.Get(1), back.Get(1));
        Assert.Equal(b.Get(9), back.Get(9));
        Assert.Null(back.Get(5));
    }

    [Fact]
    public void Bookmark_json_skips_bad_entries()
    {
        const string json = """
            {"1": {"projection": "Front", "position": [1, 2, 3], "yaw": 0, "pitch": 0, "orthoHeight": 8},
             "12": {"projection": "Top", "position": [0, 0, 0]},
             "2": {"projection": "Sideways", "position": [0, 0, 0]},
             "3": {"projection": "Top", "position": [0, 0]}}
            """;
        var b = CameraBookmarks.FromJson(json);
        Assert.Equal(1, b.Count);
        Assert.Equal(Projection.Front, b.Get(1)!.Value.Projection);
    }

    [Fact]
    public void Bookmark_store_persists_per_level()
    {
        var file = Path.Combine(Path.GetTempPath(), $"chip-bookmarks-{Guid.NewGuid():N}.json");
        var old = BookmarkStore.FilePath;
        BookmarkStore.FilePath = file;
        try
        {
            var b = new CameraBookmarks();
            b.Set(2, new CameraState(Projection.Side, new Vector3(1, 2, 3), 0, 0, 30));
            BookmarkStore.Save("C:/levels/a.chip", b);
            BookmarkStore.Save("C:/levels/b.chip", new CameraBookmarks());
            Assert.Equal(b.Get(2), BookmarkStore.Load("C:/levels/a.chip").Get(2));
            Assert.Equal(0, BookmarkStore.Load("C:/levels/b.chip").Count);
            Assert.Equal(0, BookmarkStore.Load(null).Count);
        }
        finally
        {
            BookmarkStore.FilePath = old;
            File.Delete(file);
        }
    }

    [Fact]
    public void Selection_pivot_honours_the_temporary_pivot()
    {
        var ctx = RegistryTests.Headless(Level());
        Assert.Null(ctx.SelectionPivot());
        ctx.PivotOverride = new Vector3(1, 1, 1);
        Assert.Null(ctx.SelectionPivot()); // nothing selected: no pivot at all
        ctx.Sel.Add(ElementRef.Object(ctx.Doc.Meshes[0].Id));
        Assert.Equal(new Vector3(1, 1, 1), ctx.SelectionPivot());
        ctx.PivotOverride = null;
        Assert.Equal(new Vector3(5, 0.5f, 5), ctx.SelectionPivot());
    }
}
