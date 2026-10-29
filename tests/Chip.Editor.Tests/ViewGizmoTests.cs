using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Picking;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using Chip.Editor.Rendering;
using Chip.Editor.Viewport;

namespace Chip.Editor.Tests;

/// <summary>Gizmo orientation, snapping, bypass, temporary pivot, drag alignment, and the renderer's dirty tracking.</summary>
public class ViewGizmoTests
{
    /// <summary>Front view centred on the origin, 1 px = 1 cm.</summary>
    static ViewInfo Front() => new Camera { Projection = Projection.Front, Position = Vector3.Zero, OrthoHeight = 10 }.View(new Vector2(1000, 1000));

    static readonly Vector2 Center = new(500, 500);

    static void Near(Vector3 expected, Vector3 actual, float tol = 1e-3f) =>
        Assert.True(Vector3.Distance(expected, actual) < tol, $"expected {expected}, got {actual}");

    [Fact]
    public void Invalid_or_offscreen_mouse_cannot_hover_a_gizmo()
    {
        var camera = new Camera { Position = new Vector3(5, 4, 5) };
        camera.LookAt(Vector3.Zero);
        var view = camera.View(new Vector2(1000, 1000));
        var gizmo = new Gizmo();
        foreach (var mouse in new[]
        {
            new Vector2(-float.MaxValue), // ImGui's hidden cursor position
            new Vector2(float.MaxValue),
            new Vector2(float.NaN, 500),
            new Vector2(500, float.PositiveInfinity),
            new Vector2(-1, 500),
        })
        {
            Assert.Equal(Handle.None, gizmo.HitTest(view, Vector3.Zero, mouse));
            Assert.Equal(Handle.None, gizmo.PivotHitTest(view, Vector3.Zero, mouse));
        }
        view.Project(Vector3.Zero, out var center);
        Assert.Equal(Handle.Center, gizmo.HitTest(view, Vector3.Zero, center));
    }

    [Fact]
    public void Axis_drag_snaps_to_the_grid_and_ctrl_bypasses_it()
    {
        var g = new Gizmo();
        var v = Front();
        g.BeginDrag(Handle.X, v, Vector3.Zero, Center);
        Near(new Vector3(1, 0, 0), g.DragTransform(v, new Vector2(612, 500), 0.25f, snap: true).M.Translation);
        g.CtrlHeld = true;
        Near(new Vector3(1.12f, 0, 0), g.DragTransform(v, new Vector2(612, 500), 0.25f, snap: true).M.Translation);
    }

    [Fact]
    public void Ctrl_bypasses_snapping_for_a_push_pull_drag_and_releasing_it_restores_snapping()
    {
        var g = new Gizmo { CtrlHeld = true, ShiftHeld = true };
        var v = Front();
        g.BeginDrag(Handle.X, v, Vector3.Zero, Center);
        Near(new Vector3(1.12f, 0, 0), g.DragTransform(v, new Vector2(612, 500), 0.25f, snap: true).M.Translation);
        g.CtrlHeld = false;
        Near(new Vector3(1, 0, 0), g.DragTransform(v, new Vector2(612, 500), 0.25f, snap: true).M.Translation);
    }

    [Fact]
    public void Rotation_uses_its_own_snap_step_and_the_temporary_pivot()
    {
        var g = new Gizmo { Mode = GizmoMode.Rotate, RotateSnapDegrees = 40, PivotHook = () => new Vector3(1, 0, 0) };
        var v = Front();
        // Pivot (1,0,0) is at (600,500). Drag a quarter turn counter-clockwise on screen.
        g.BeginDrag(Handle.Z, v, Vector3.Zero, new Vector2(700, 500));
        var (m, rigid) = g.DragTransform(v, new Vector2(600, 400), 0.25f, snap: true);
        Assert.True(rigid);
        Near(new Vector3(1, 0, 0), Vector3.Transform(new Vector3(1, 0, 0), m)); // the pivot stays put
        var moved = Vector3.Transform(new Vector3(2, 0, 0), m) - new Vector3(1, 0, 0);
        float deg = MathF.Atan2(moved.Y, moved.X) * 180 / MathF.PI;
        Assert.Equal(80, deg, 2); // 90° snapped to 40° steps
    }

    [Fact]
    public void Local_basis_scales_along_the_rotated_axis()
    {
        var g = new Gizmo { Mode = GizmoMode.Scale };
        var x = Vector3.Normalize(new Vector3(1, 1, 0));
        g.SetBasis(x, Vector3.Normalize(new Vector3(-1, 1, 0)), Vector3.UnitZ);
        var v = Front();
        v.Project(Vector3.Zero, out var c);
        v.Project(x * 0.96f, out var tip); // 96 px handle at 1 px/cm
        g.BeginDrag(Handle.X, v, Vector3.Zero, c);
        var (m, rigid) = g.DragTransform(v, c + (tip - c), 0.25f, snap: false);
        Assert.False(rigid);
        Near(x * 2, Vector3.Transform(x, m), 0.02f);
        var y = Vector3.Normalize(new Vector3(-1, 1, 0));
        Near(y, Vector3.Transform(y, m), 0.02f);
    }

    [Fact]
    public void Basis_is_frozen_mid_drag()
    {
        var g = new Gizmo();
        g.BeginDrag(Handle.X, Front(), Vector3.Zero, Center);
        g.SetBasis(Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX);
        Assert.Equal(Vector3.UnitX, g.Basis[0]);
        g.EndDrag();
        g.SetBasis(Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX);
        Assert.Equal(Vector3.UnitY, g.Basis[0]);
    }

    [Fact]
    public void Caps_lock_aligns_to_the_snap_provider_within_the_handle_constraint()
    {
        var g = new Gizmo { AlignHeld = true, SnapProvider = (_, _, exclude) => exclude ? new Vector3(2, 3, 4) : null };
        var v = Front();
        g.BeginDrag(Handle.X, v, Vector3.Zero, Center);
        Near(new Vector3(2, 0, 0), g.DragTransform(v, new Vector2(510, 500), 0.25f, true).M.Translation);
        Assert.Equal(new Vector3(2, 3, 4), g.AlignTarget);
        g.EndDrag();
        g.BeginDrag(Handle.Center, v, Vector3.Zero, Center);
        Near(new Vector3(2, 3, 4), g.DragTransform(v, new Vector2(510, 500), 0.25f, true).M.Translation);
    }

    [Fact]
    public void Pivot_drag_snaps_the_absolute_position_on_moving_axes()
    {
        var g = new Gizmo();
        var v = Front();
        var start = new Vector3(0.1f, 0.1f, 0.3f);
        v.Project(start, out var s);
        g.BeginPivotDrag(Handle.X, v, start, s);
        Assert.True(g.PivotDragging);
        var p = g.PivotDragPosition(v, s + new Vector2(37, 0), 0.25f, snap: true);
        Near(new Vector3(0.5f, 0.1f, 0.3f), p); // x 0.47 -> 0.5; y and z untouched
        g.EndPivotDrag();
        Assert.False(g.PivotDragging);
    }

    [Fact]
    public void Rotate_gizmo_pivot_dot_is_a_pivot_handle_but_not_a_rotate_handle()
    {
        var g = new Gizmo { Mode = GizmoMode.Rotate };
        var v = Front();
        Assert.Equal(Handle.None, g.HitTest(v, Vector3.Zero, Center));
        Assert.Equal(Handle.Center, g.PivotHitTest(v, Vector3.Zero, Center));
    }

    [Fact]
    public void Local_frame_follows_a_rotated_face()
    {
        var doc = new Document();
        var mesh = doc.Add(MeshOps.CreateBox(Vector3.Zero, Vector3.One, "dev_a", TextureMapping.Default));
        var rot = Matrix4x4.CreateRotationY(0.4f);
        for (int i = 0; i < mesh.Positions.Count; i++) mesh.Positions[i] = Vector3.Transform(mesh.Positions[i], rot);
        int top = Enumerable.Range(0, mesh.Faces.Count).First(f => mesh.FaceNormal(f).Y > 0.9f);
        var sel = new Selection();
        sel.ConvertTo(SelectMode.Face, doc);
        sel.Add(ElementRef.Face(mesh.Id, top));

        var (x, y, z) = GizmoFrame.Local(doc, sel)!.Value;
        Near(Vector3.UnitY, y);
        Near(Vector3.Transform(Vector3.UnitX, rot), x);
        Near(Vector3.Cross(x, y), z);
    }

    [Fact]
    public void Frame_from_normal_is_right_handed_and_keeps_world_like_signs()
    {
        var (x, y, z) = GizmoFrame.FromNormal(Vector3.Normalize(new Vector3(-0.2f, 0, -1)), null);
        Near(Vector3.Cross(x, y), z);
        Assert.True(z.Z > 0.9f); // the normal replaced +Z (flipped to point the same way)
        Assert.True(y.Y > 0.9f || x.X > 0.9f);
    }

    [Fact]
    public void Drag_align_snaps_to_corners_and_skips_the_moving_selection()
    {
        var doc = new Document();
        var moving = doc.Add(MeshOps.CreateBox(Vector3.Zero, Vector3.One, "dev_a", TextureMapping.Default));
        doc.Add(MeshOps.CreateBox(new Vector3(3, 0, 0), new Vector3(4, 1, 1), "dev_a", TextureMapping.Default));
        var v = new Camera { Projection = Projection.Front, Position = new Vector3(2, 0.5f, 0), OrthoHeight = 10 }.View(new Vector2(1000, 1000));

        v.Project(new Vector3(3.9f, 0.9f, 1), out var nearCorner);
        Near(new Vector3(4, 1, 1), DragAlign.Find(doc, v, nearCorner, null)!.Value);

        v.Project(new Vector3(0.95f, 0.95f, 1), out var overMoving);
        Near(new Vector3(1, 1, 1), DragAlign.Find(doc, v, overMoving, null)!.Value);
        var exclude = new Dictionary<PolyMesh, HashSet<int>> { [moving] = Enumerable.Range(0, moving.Positions.Count).ToHashSet() };
        var hit = DragAlign.Find(doc, v, overMoving, exclude);
        Assert.True(hit == null || hit.Value.X >= 3, $"snapped onto the moving box at {hit}");
    }

    [Fact]
    public void Mesh_hash_changes_only_with_content()
    {
        var m = MeshOps.CreateBox(Vector3.Zero, Vector3.One, "dev_a", TextureMapping.Default);
        ulong h = MeshHash.Of(m);
        Assert.Equal(h, MeshHash.Of(m.Clone()));
        var moved = m.Clone();
        moved.Positions[0] += new Vector3(0.001f, 0, 0);
        Assert.NotEqual(h, MeshHash.Of(moved));
        var retextured = m.Clone();
        retextured.Faces[2].Material = "dev_b";
        Assert.NotEqual(h, MeshHash.Of(retextured));
        var soft = m.Clone();
        soft.SoftEdges.Add(new Edge(0, 1));
        Assert.NotEqual(h, MeshHash.Of(soft));
        var shifted = m.Clone();
        shifted.Faces[0].Map.ShiftU = 0.5f;
        Assert.NotEqual(h, MeshHash.Of(shifted));
    }
}
