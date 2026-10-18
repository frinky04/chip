using System.Numerics;
using Chip.Editor.Viewport;

namespace Chip.Editor.Tests;

/// <summary>Camera and orthographic projection maths (no GL).</summary>
public class ViewCameraTests
{
    static void Near(Vector2 expected, Vector2 actual, float tol = 0.01f) =>
        Assert.True(Vector2.Distance(expected, actual) < tol, $"expected {expected}, got {actual}");

    static void Near(Vector3 expected, Vector3 actual, float tol = 1e-3f) =>
        Assert.True(Vector3.Distance(expected, actual) < tol, $"expected {expected}, got {actual}");

    [Fact]
    public void Front_view_maps_world_xy_to_screen_linearly()
    {
        var cam = new Camera { Projection = Projection.Front, Position = new Vector3(2, 1, 0), OrthoHeight = 10 };
        var v = cam.View(new Vector2(1000, 500)); // 20 m wide, 50 px per metre
        Assert.True(v.IsOrthographic);
        Assert.True(v.Project(new Vector3(2, 1, 0), out var c));
        Near(new Vector2(500, 250), c);
        Assert.True(v.Project(new Vector3(7, 1, -3), out var p)); // depth doesn't move it
        Near(new Vector2(750, 250), p);
        Assert.True(v.Project(new Vector3(2, 3, 5), out var q));
        Near(new Vector2(500, 150), q); // +Y is up the screen
    }

    [Fact]
    public void Top_view_looks_down_with_minus_z_at_the_top()
    {
        var cam = new Camera { Projection = Projection.Top, Position = Vector3.Zero, OrthoHeight = 10 };
        var v = cam.View(new Vector2(500, 500));
        Assert.True(v.Project(new Vector3(0, 0, -1), out var s));
        Near(new Vector2(250, 200), s);
        Assert.True(v.Project(new Vector3(1, 0, 0), out var x));
        Near(new Vector2(300, 250), x);
        Near(-Vector3.UnitY, cam.Forward);
    }

    [Theory]
    [InlineData(Projection.Top)]
    [InlineData(Projection.Front)]
    [InlineData(Projection.Side)]
    public void Ortho_bases_are_right_handed_and_orthonormal(Projection p)
    {
        var cam = new Camera { Projection = p };
        Near(cam.Right, Vector3.Cross(cam.Forward, cam.Up));
        Assert.Equal(0, Vector3.Dot(cam.Forward, cam.Up), 5);
        Assert.Equal(1, cam.Right.Length(), 5);
    }

    [Fact]
    public void Ortho_rays_are_parallel_and_start_under_the_pixel()
    {
        var cam = new Camera { Projection = Projection.Front, Position = Vector3.Zero, OrthoHeight = 10 };
        var v = cam.View(new Vector2(1000, 1000));
        var a = v.ScreenRay(new Vector2(500, 500));
        var b = v.ScreenRay(new Vector2(750, 250));
        Near(-Vector3.UnitZ, a.Dir);
        Near(-Vector3.UnitZ, b.Dir);
        Near(new Vector3(2.5f, 2.5f, 0), b.Origin with { Z = 0 }, 1e-2f);
        Assert.True(b.Origin.Z > 100); // in front of the level, near the eye
    }

    [Fact]
    public void Perspective_rays_still_start_at_the_eye()
    {
        var cam = new Camera { Position = new Vector3(1, 2, 3) };
        cam.LookAt(Vector3.Zero);
        var v = cam.View(new Vector2(800, 600));
        Assert.False(v.IsOrthographic);
        Near(cam.Position, v.ScreenRay(new Vector2(10, 590)).Origin);
    }

    [Fact]
    public void Ortho_zoom_keeps_the_anchor_under_the_cursor()
    {
        var cam = new Camera { Projection = Projection.Front, Position = Vector3.Zero, OrthoHeight = 10 };
        var size = new Vector2(1000, 800);
        var anchor = new Vector3(3, 2, 0);
        cam.View(size).Project(anchor, out var before);
        cam.Zoom(0.5f, anchor);
        Assert.Equal(5, cam.OrthoHeight, 4);
        cam.View(size).Project(anchor, out var after);
        Near(before, after, 0.05f);
    }

    [Fact]
    public void Ortho_frame_fits_the_box_in_both_directions()
    {
        var cam = new Camera { Projection = Projection.Top };
        cam.View(new Vector2(1000, 500));
        cam.Frame(new Vector3(-10, 0, -1), new Vector3(10, 5, 1)); // 20 m wide on a 2:1 view
        Assert.True(cam.OrthoHeight * 2 >= 20);
        Near(Vector3.Zero, cam.Position with { Y = 0 });
        Assert.Equal(0, cam.Position.Y, 4); // ortho cameras keep depth at the origin plane
    }

    [Fact]
    public void Focus_on_keeps_a_workable_distance()
    {
        var cam = new Camera { Position = new Vector3(0, 0, 100) };
        cam.LookAt(Vector3.Zero);
        cam.FocusOn(new Vector3(5, 0, 0));
        Assert.Equal(12, Vector3.Distance(cam.Position, new Vector3(5, 0, 0)), 3);
        Assert.Equal(new Vector3(5, 0, 0), cam.FocusTarget);
    }

    [Fact]
    public void Units_per_pixel_is_constant_in_ortho()
    {
        var cam = new Camera { Projection = Projection.Side, OrthoHeight = 12 };
        Assert.Equal(cam.UnitsPerPixel(1, 600), cam.UnitsPerPixel(500, 600));
        Assert.Equal(0.02f, cam.UnitsPerPixel(0, 600), 5);
    }
}
