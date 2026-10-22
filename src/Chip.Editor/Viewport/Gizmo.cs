using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Picking;
using Chip.Editor.UI;
using ImGuiNET;

namespace Chip.Editor.Viewport;

public enum GizmoMode { Translate, Rotate, Scale }

public enum Handle { None, X, Y, Z, PlaneYZ, PlaneXZ, PlaneXY, Center }

/// <summary>Which axes the gizmo uses: world, the selection's dominant face (local), or the camera (view).</summary>
public enum GizmoOrientation { World, Local, View }

/// <summary>
/// What grid snapping lines a move up with. World: the selection lands on world grid lines. Relative: it moves by whole
/// grid steps from where it started, so something off the grid stays off it by the same amount.
/// </summary>
public enum SnapSpace { World, Relative }

/// <summary>
/// Finds a point to align to under the cursor (vertex, edge midpoint, face centre). <paramref name="excludeSelection"/>
/// skips the geometry being moved so it can't snap onto itself.
/// </summary>
public delegate Vector3? SnapQuery(ViewInfo view, Vector2 mouse, bool excludeSelection);

/// <summary>
/// Transform gizmo drawn in screen space along an orthonormal <see cref="Basis"/> (world, local or view axes).
/// It produces a transform relative to the drag start; the caller applies it through a <c>TransformGesture</c>.
/// Constant on-screen size. Modifier state is pushed in by the host each frame (<see cref="CtrlHeld"/>,
/// <see cref="ShiftHeld"/>, <see cref="AlignHeld"/>) so the maths stays testable without ImGui.
/// </summary>
public sealed class Gizmo
{
    const float ScaleSnap = 0.125f;
    static float LengthPx => 96 * Theme.Scale;
    static float PickPx => 9 * Theme.Scale;

    public GizmoMode Mode = GizmoMode.Translate;
    public GizmoOrientation Orientation = GizmoOrientation.World;
    /// <summary>Rotation snap step, separate from the grid. Scythe default 15°.</summary>
    public float RotateSnapDegrees = 15f;
    /// <summary>
    /// What moves snap to (<see cref="Viewport.SnapSpace"/>). In World, the anchor lands on grid lines along each world
    /// axis being dragged; an axis that isn't a world axis (local or view gizmo on a turned selection) has no grid lines
    /// to land on and moves in steps. Rotation and scale always snap their own amounts.
    /// </summary>
    public SnapSpace SnapSpace = SnapSpace.World;

    public Handle Hover { get; private set; }
    public Handle Active { get; private set; }
    public bool Dragging => Active != Handle.None;

    /// <summary>Ctrl held: bypasses snapping mid-drag (unless the drag started as Ctrl+Shift push/pull).</summary>
    public bool CtrlHeld { get; set; }
    public bool ShiftHeld { get; set; }
    /// <summary>CapsLock held: the move gizmo aligns the pivot to the element under the cursor (<see cref="SnapProvider"/>).</summary>
    public bool AlignHeld { get; set; }

    /// <summary>Drag alignment hook, wired by the view feature. The select tool's existing drag path uses it unchanged.</summary>
    public SnapQuery? SnapProvider { get; set; }
    /// <summary>Overrides the pivot handed to <see cref="BeginDrag"/> (temporary pivot). Wired to <c>EditorContext.PivotOverride</c>.</summary>
    public Func<Vector3?>? PivotHook { get; set; }
    /// <summary>
    /// The point a World-space move puts on the grid when there's no temporary pivot: the selection's bounds' low
    /// corner (Hammer), so a box whose corners sit on the grid keeps them there even when its centre doesn't. Falls
    /// back to the pivot.
    /// </summary>
    public Func<Vector3?>? AnchorHook { get; set; }
    /// <summary>The point the last aligned drag snapped to, for feedback; null when not aligning.</summary>
    public Vector3? AlignTarget { get; private set; }

    /// <summary>
    /// What the drag in progress has done so far, for the readout: where the pivot started, how far it moved (world),
    /// the turn in degrees about the handle's axis, and the scale along the gizmo's axes.
    /// </summary>
    public readonly record struct DragAmount(Vector3 Start, Vector3 Delta, float Degrees, Vector3 Scale);
    public DragAmount Amount { get; private set; }

    /// <summary>
    /// During a snapped World-space move, where the anchor is now: the point sitting on the grid lines. Null otherwise.
    /// </summary>
    public Vector3? SnapMark => Dragging && Mode == GizmoMode.Translate && SnapSpace == SnapSpace.World && _snapped ? _anchor + Amount.Delta : null;
    bool _snapped;

    readonly Vector3[] _basis = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
    public IReadOnlyList<Vector3> Basis => _basis;

    Vector3 _pivot, _anchor;
    Vector2 _startMouse;
    float _startAxisParam;
    Vector3 _startPlaneHit;
    float _prevScreenAngle, _angle;
    bool _pushPullStart;

    // Temporary-pivot drag (MMB on the gizmo).
    public Handle PivotHandle { get; private set; }
    public bool PivotDragging => PivotHandle != Handle.None;

    /// <summary>Sets the axes (orthonormalised). Ignored mid-drag so the frame never shifts under the cursor.</summary>
    public void SetBasis(Vector3 x, Vector3 y, Vector3 z)
    {
        if (Dragging || PivotDragging) return;
        x = Vector3.Normalize(x);
        z = Vector3.Normalize(Vector3.Cross(x, y));
        y = Vector3.Cross(z, x);
        if (!float.IsFinite(x.X + y.Y + z.Z + x.Y + y.Z + z.X)) { x = Vector3.UnitX; y = Vector3.UnitY; z = Vector3.UnitZ; }
        _basis[0] = x;
        _basis[1] = y;
        _basis[2] = z;
    }

    int AxisIndex(Handle h) => h switch
    {
        Handle.X or Handle.PlaneYZ => 0,
        Handle.Y or Handle.PlaneXZ => 1,
        _ => 2,
    };

    Vector3 AxisOf(Handle h) => _basis[AxisIndex(h)];

    (Vector3, Vector3) PlaneAxes(int normalAxis) => normalAxis switch
    {
        0 => (_basis[1], _basis[2]),
        1 => (_basis[0], _basis[2]),
        _ => (_basis[0], _basis[1]),
    };

    static float WorldLength(ViewInfo v, Vector3 pivot)
    {
        var dir = Vector3.Normalize(pivot - v.Eye);
        var side = Vector3.Cross(dir, Vector3.UnitY);
        side = side.LengthSquared() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(side);
        if (!v.Project(pivot, out var a) || !v.Project(pivot + side, out var b)) return 1;
        return LengthPx / MathF.Max(Vector2.Distance(a, b), 1e-4f);
    }

    public Handle HitTest(ViewInfo v, Vector3 pivot, Vector2 mouse)
    {
        if (!v.Project(pivot, out var c)) return Handle.None;
        float len = WorldLength(v, pivot);
        if (Mode != GizmoMode.Rotate && Vector2.Distance(mouse, c) < PickPx + 2) return Handle.Center;

        if (Mode == GizmoMode.Rotate)
        {
            Handle best = Handle.None;
            float bestD = PickPx;
            var viewDir = Vector3.Normalize(pivot - v.Eye);
            for (int a = 0; a < 3; a++)
            {
                if (MathF.Abs(Vector3.Dot(_basis[a], viewDir)) < 0.02f) continue; // ring seen edge-on (2D views): a line through the pivot
                var pts = Ring(v, pivot, a, len);
                for (int i = 0; i + 1 < pts.Count; i++)
                {
                    float d = Picker.DistanceToSegment(mouse, pts[i], pts[i + 1], out _);
                    if (d < bestD) { bestD = d; best = Handle.X + a; }
                }
            }
            return best;
        }

        if (Mode == GizmoMode.Translate)
            for (int a = 0; a < 3; a++)
            {
                var (u, w) = PlaneAxes(a);
                if (PlaneQuad(v, pivot, u, w, len) is { } quad && PointInQuad(mouse, quad)) return Handle.PlaneYZ + a;
            }

        for (int a = 0; a < 3; a++)
        {
            if (!v.Project(pivot + _basis[a] * len, out var tip)) continue;
            if (Vector2.Distance(c, tip) < 12 * Theme.Scale) continue; // axis points at the camera
            if (Picker.DistanceToSegment(mouse, c, tip, out _) < PickPx) return Handle.X + a;
        }
        return Handle.None;
    }

    /// <summary>Handle for a temporary-pivot drag: any handle, or the pivot dot of the rotate gizmo.</summary>
    public Handle PivotHitTest(ViewInfo v, Vector3 pivot, Vector2 mouse)
    {
        var h = HitTest(v, pivot, mouse);
        if (h == Handle.None && v.Project(pivot, out var c) && Vector2.Distance(mouse, c) < PickPx + 2) h = Handle.Center;
        return h;
    }

    public void UpdateHover(ViewInfo v, Vector3 pivot, Vector2 mouse) => Hover = Dragging ? Hover : HitTest(v, pivot, mouse);

    public void BeginDrag(Handle h, ViewInfo v, Vector3 pivot, Vector2 mouse)
    {
        Active = h;
        var placed = PivotHook?.Invoke();
        _pivot = placed ?? pivot;
        _anchor = placed ?? AnchorHook?.Invoke() ?? _pivot;
        Amount = new DragAmount(_pivot, Vector3.Zero, 0, Vector3.One);
        _startMouse = mouse;
        _angle = 0;
        _pushPullStart = CtrlHeld && ShiftHeld && Mode == GizmoMode.Translate;
        AlignTarget = null;
        _prevScreenAngle = ScreenAngle(v, mouse);
        var ray = v.ScreenRay(mouse);
        _startAxisParam = AxisParam(ray, _pivot, AxisOf(h));
        _startPlaneHit = PlaneHit(v, ray, h, _pivot) ?? _pivot;
    }

    public void EndDrag()
    {
        Active = Handle.None;
        AlignTarget = null;
    }

    /// <summary>
    /// The transform for values typed during the drag instead of the mouse. Move: one value goes along an axis handle (or
    /// along the way a plane or centre drag is heading), two along a plane handle's axes, three along all the gizmo's
    /// axes (kept to the handle). Rotate: degrees about the handle's axis, right-hand rule, as the readout shows it.
    /// Scale: a factor along the handle (all axes on the centre). Null when the values don't fit the handle.
    /// </summary>
    public (Matrix4x4 M, bool Rigid)? Exact(IReadOnlyList<float> values)
    {
        if (!Dragging || values.Count == 0) return null;
        switch (Mode)
        {
            case GizmoMode.Translate:
            {
                Vector3 d;
                if (values.Count == 1)
                {
                    if (Active is Handle.X or Handle.Y or Handle.Z) d = AxisOf(Active) * values[0];
                    else if (Amount.Delta.LengthSquared() > 1e-10f) d = Vector3.Normalize(Amount.Delta) * values[0];
                    else return null; // no direction yet: drag a little first, or type one value per axis
                }
                else if (values.Count == 2)
                {
                    if (Active is not (Handle.PlaneYZ or Handle.PlaneXZ or Handle.PlaneXY)) return null;
                    var (u, w) = PlaneAxes(AxisIndex(Active));
                    d = u * values[0] + w * values[1];
                }
                else d = Constrain(_basis[0] * values[0] + _basis[1] * values[1] + _basis[2] * values[2], Active);
                Amount = Amount with { Delta = d };
                return (Matrix4x4.CreateTranslation(d), true);
            }
            case GizmoMode.Rotate:
                if (values.Count != 1) return null;
                Amount = Amount with { Degrees = values[0] };
                return (About(Matrix4x4.CreateFromAxisAngle(AxisOf(Active), values[0] * MathF.PI / 180)), true);
            default:
            {
                if (values.Count != 1 || MathF.Abs(values[0]) < 1e-4f) return null;
                var scale = Vector3.One;
                if (Active == Handle.Center) scale = new Vector3(values[0]);
                else scale[AxisIndex(Active)] = values[0];
                Amount = Amount with { Scale = scale };
                return (About(InBasis(Matrix4x4.CreateScale(scale))), false);
            }
        }
    }

    /// <summary>Snapping applies unless Ctrl is held (a Ctrl+Shift push/pull keeps snapping: Ctrl is part of its chord).</summary>
    bool Snapping(bool snap) => snap && !(CtrlHeld && !_pushPullStart);

    /// <summary>Transform from the drag start to now, and whether it is rigid (keeps texture lock valid).</summary>
    public (Matrix4x4 M, bool Rigid) DragTransform(ViewInfo v, Vector2 mouse, float grid, bool snap)
    {
        snap = Snapping(snap);
        _snapped = snap && !AlignHeld;
        var ray = v.ScreenRay(mouse);
        AlignTarget = null;
        switch (Mode)
        {
            case GizmoMode.Translate:
            {
                Vector3 delta;
                if (AlignHeld && SnapProvider?.Invoke(v, mouse, true) is { } target)
                {
                    AlignTarget = target;
                    delta = Constrain(target - _pivot, Active);
                }
                // A press that hasn't moved yet moves nothing (a World snap would otherwise jump on a click).
                else if (Vector2.DistanceSquared(mouse, _startMouse) < 4) delta = Vector3.Zero;
                else delta = TranslateDelta(v, ray, Active, grid, snap);
                Amount = Amount with { Delta = delta };
                return (Matrix4x4.CreateTranslation(delta), true);
            }
            case GizmoMode.Rotate:
            {
                var axis = AxisOf(Active);
                float a = ScreenAngle(v, mouse);
                float step = a - _prevScreenAngle;
                if (step > MathF.PI) step -= 2 * MathF.PI;
                if (step < -MathF.PI) step += 2 * MathF.PI;
                _prevScreenAngle = a;
                _angle += step;
                // Screen y points down, so a visually counter-clockwise drag decreases the angle.
                float facing = Vector3.Dot(axis, v.Eye - _pivot) >= 0 ? 1 : -1;
                float world = -_angle * facing;
                if (snap && RotateSnapDegrees > 0) world = MeshOps.Snap(world, RotateSnapDegrees * MathF.PI / 180f);
                Amount = Amount with { Degrees = world * 180 / MathF.PI };
                return (About(Matrix4x4.CreateFromAxisAngle(axis, world)), true);
            }
            default:
            {
                Vector3 scale;
                if (Active == Handle.Center)
                {
                    float s = 1 + ((mouse.X - _startMouse.X) - (mouse.Y - _startMouse.Y)) / (LengthPx * 1.5f);
                    if (snap) s = MeshOps.Snap(s, ScaleSnap);
                    scale = new Vector3(MathF.Max(s, ScaleSnap));
                }
                else
                {
                    var axis = AxisOf(Active);
                    v.Project(_pivot, out var c);
                    v.Project(_pivot + axis * WorldLength(v, _pivot), out var tip);
                    var dir = tip - c;
                    float s = 1 + Vector2.Dot(mouse - _startMouse, dir) / MathF.Max(dir.LengthSquared(), 1);
                    if (snap) s = MeshOps.Snap(s, ScaleSnap);
                    scale = Vector3.One;
                    scale[AxisIndex(Active)] = s;
                }
                Amount = Amount with { Scale = scale };
                return (About(InBasis(Matrix4x4.CreateScale(scale))), false);
            }
        }
    }

    /// <summary>Translation for a handle: along an axis, in a plane, or in the view plane (centre), snapped in the gizmo's frame.</summary>
    Vector3 TranslateDelta(ViewInfo v, Ray ray, Handle h, float grid, bool snap)
    {
        if (h is Handle.X or Handle.Y or Handle.Z)
        {
            var axis = AxisOf(h);
            float d = AxisParam(ray, _pivot, axis) - _startAxisParam;
            return axis * (snap ? SnapAlong(axis, d, grid) : d);
        }
        var hit = PlaneHit(v, ray, h, _pivot);
        var delta = hit is { } p ? p - _startPlaneHit : Vector3.Zero;
        if (snap) delta = SnapInBasis(delta, grid);
        return Constrain(delta, h);
    }

    /// <summary>Keeps a delta on the handle's axis or plane; the centre handle moves freely.</summary>
    Vector3 Constrain(Vector3 delta, Handle h)
    {
        if (h is Handle.X or Handle.Y or Handle.Z) { var a = AxisOf(h); return a * Vector3.Dot(delta, a); }
        if (h is Handle.PlaneYZ or Handle.PlaneXZ or Handle.PlaneXY) { var n = AxisOf(h); return delta - n * Vector3.Dot(delta, n); }
        return delta;
    }

    Vector3 SnapInBasis(Vector3 d, float grid)
    {
        var r = Vector3.Zero;
        for (int i = 0; i < 3; i++) r += _basis[i] * SnapAlong(_basis[i], Vector3.Dot(d, _basis[i]), grid);
        return r;
    }

    /// <summary>
    /// A drag of <paramref name="d"/> along <paramref name="axis"/>, snapped: in World space (on a world axis) the anchor
    /// ends on a grid line; otherwise the drag is a whole number of grid steps.
    /// </summary>
    float SnapAlong(Vector3 axis, float d, float grid)
    {
        if (SnapSpace == SnapSpace.World && IsWorldAxis(axis))
        {
            float start = Vector3.Dot(_anchor, axis);
            return MeshOps.Snap(start + d, grid) - start;
        }
        return MeshOps.Snap(d, grid);
    }

    /// <summary>Lies along X, Y or Z (either way), so world grid lines cross it.</summary>
    internal static bool IsWorldAxis(Vector3 a) => MathF.Max(MathF.Abs(a.X), MathF.Max(MathF.Abs(a.Y), MathF.Abs(a.Z))) > 0.9999f;

    /// <summary>Re-expresses a transform built on the X/Y/Z axes in the gizmo's frame (row vectors: Bᵀ M B).</summary>
    Matrix4x4 InBasis(Matrix4x4 m)
    {
        var b = new Matrix4x4(
            _basis[0].X, _basis[0].Y, _basis[0].Z, 0,
            _basis[1].X, _basis[1].Y, _basis[1].Z, 0,
            _basis[2].X, _basis[2].Y, _basis[2].Z, 0,
            0, 0, 0, 1);
        return Matrix4x4.Transpose(b) * m * b;
    }

    Matrix4x4 About(Matrix4x4 m) => Matrix4x4.CreateTranslation(-_pivot) * m * Matrix4x4.CreateTranslation(_pivot);

    // ---- Temporary pivot (MMB-drag on the gizmo) ----

    /// <summary>Starts moving the pivot with the handle under the cursor. Rings move it in the view plane.</summary>
    public void BeginPivotDrag(Handle h, ViewInfo v, Vector3 pivot, Vector2 mouse)
    {
        if (Mode == GizmoMode.Rotate) h = Handle.Center;
        PivotHandle = h;
        _pivot = pivot;
        _pushPullStart = false;
        var ray = v.ScreenRay(mouse);
        _startAxisParam = AxisParam(ray, pivot, AxisOf(h));
        _startPlaneHit = PlaneHit(v, ray, h, pivot) ?? pivot;
    }

    /// <summary>New pivot position: snapped to elements with CapsLock, else to the grid (absolute, on the moving axes).</summary>
    public Vector3 PivotDragPosition(ViewInfo v, Vector2 mouse, float grid, bool snap)
    {
        AlignTarget = null;
        if (AlignHeld && SnapProvider?.Invoke(v, mouse, false) is { } target)
        {
            AlignTarget = target;
            return _pivot + Constrain(target - _pivot, PivotHandle);
        }
        var p = _pivot + TranslateDelta(v, v.ScreenRay(mouse), PivotHandle, grid, false);
        if (!Snapping(snap)) return p;
        // Snap the absolute position, but only along the directions the handle moves in.
        var snapped = new Vector3(MeshOps.Snap(p.X, grid), MeshOps.Snap(p.Y, grid), MeshOps.Snap(p.Z, grid));
        return p + Constrain(snapped - p, PivotHandle);
    }

    public void EndPivotDrag()
    {
        PivotHandle = Handle.None;
        AlignTarget = null;
    }

    float ScreenAngle(ViewInfo v, Vector2 mouse)
    {
        v.Project(_pivot, out var c);
        return MathF.Atan2(mouse.Y - c.Y, mouse.X - c.X);
    }

    static float AxisParam(Ray ray, Vector3 origin, Vector3 axis)
    {
        var w0 = origin - ray.Origin;
        float b = Vector3.Dot(axis, ray.Dir);
        float denom = 1 - b * b;
        if (denom < 1e-6f) return 0;
        return (b * Vector3.Dot(ray.Dir, w0) - Vector3.Dot(axis, w0)) / denom;
    }

    Vector3? PlaneHit(ViewInfo v, Ray ray, Handle h, Vector3 pivot)
    {
        var n = h == Handle.Center ? Vector3.Normalize(v.Eye - pivot) : AxisOf(h);
        if (Picker.RayPlane(ray, pivot, n, out float t)) return ray.At(t);
        // Plane seen edge-on (common in 2D views): fall back to the view plane so the drag still follows the mouse.
        return Picker.RayPlane(ray, pivot, -ray.Dir, out t) ? ray.At(t) : null;
    }

    static Vector2[]? PlaneQuad(ViewInfo v, Vector3 pivot, Vector3 u, Vector3 w, float len)
    {
        float a = len * 0.25f, b = len * 0.45f;
        var corners = new[] { pivot + u * a + w * a, pivot + u * b + w * a, pivot + u * b + w * b, pivot + u * a + w * b };
        var result = new Vector2[4];
        for (int i = 0; i < 4; i++) if (!v.Project(corners[i], out result[i])) return null;
        // Edge-on quads (an ortho view looking along the plane) are slivers; don't offer them.
        float area = MathF.Abs((result[2].X - result[0].X) * (result[3].Y - result[1].Y) - (result[3].X - result[1].X) * (result[2].Y - result[0].Y));
        return area < 4 ? null : result;
    }

    static bool PointInQuad(Vector2 p, Vector2[] q)
    {
        int sign = 0;
        for (int i = 0; i < 4; i++)
        {
            var a = q[i]; var b = q[(i + 1) % 4];
            float c = (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
            int s = Math.Sign(c);
            if (s == 0) continue;
            if (sign == 0) sign = s;
            else if (s != sign) return false;
        }
        return true;
    }

    List<Vector2> Ring(ViewInfo v, Vector3 pivot, int axis, float radius)
    {
        var (u, w) = PlaneAxes(axis);
        var pts = new List<Vector2>(65);
        for (int i = 0; i <= 64; i++)
        {
            float t = i / 64f * MathF.Tau;
            if (v.Project(pivot + (u * MathF.Cos(t) + w * MathF.Sin(t)) * radius, out var s)) pts.Add(s);
        }
        return pts;
    }

    const uint Backing = 0xE6100F0E;
    static readonly string[] AxisNames = ["X", "Y", "Z"];

    bool Lit(Handle h) => Active == h || PivotHandle == h || (!Dragging && !PivotDragging && Hover == h);

    /// <summary>Every stroke sits on a dark backing so handles stay legible over orange and grey dev textures.</summary>
    public void Draw(ImDrawListPtr dl, Vector2 origin, ViewInfo v, Vector3 pivot)
    {
        if (AlignTarget is { } at && v.Project(at, out var ap)) DrawAlignMarker(dl, origin + ap);
        if (!v.Project(pivot, out var c)) return;
        float len = WorldLength(v, pivot);
        float k = Theme.Scale;
        uint Col(Handle h, int a) => Lit(h) ? Theme.HoverU32 : Theme.AxisU32[a];

        if (Mode == GizmoMode.Rotate)
        {
            for (int a = 0; a < 3; a++)
            {
                var pts = Ring(v, pivot, a, len).Select(p => p + origin).ToArray();
                if (pts.Length < 2) continue;
                float w = (Active == Handle.X + a ? 3f : 2f) * k;
                dl.AddPolyline(ref pts[0], pts.Length, Backing, ImDrawFlags.None, w + 3 * k);
                dl.AddPolyline(ref pts[0], pts.Length, Col(Handle.X + a, a), ImDrawFlags.None, w);
            }
            dl.AddCircleFilled(origin + c, 5 * k, Backing);
            dl.AddCircleFilled(origin + c, 3 * k, Theme.HoverU32);
            return;
        }

        if (Mode == GizmoMode.Translate)
            for (int a = 0; a < 3; a++)
            {
                var (u, w) = PlaneAxes(a);
                if (PlaneQuad(v, pivot, u, w, len) is not { } q) continue;
                uint col = Col(Handle.PlaneYZ + a, a);
                Vector2 q0 = origin + q[0], q1 = origin + q[1], q2 = origin + q[2], q3 = origin + q[3];
                dl.AddQuadFilled(q0, q1, q2, q3, (col & 0x00FFFFFF) | 0x70000000);
                dl.AddQuad(q0, q1, q2, q3, Backing, 3.5f * k);
                dl.AddQuad(q0, q1, q2, q3, col, 1.5f * k);
            }

        for (int a = 0; a < 3; a++)
        {
            if (!v.Project(pivot + _basis[a] * len, out var tip) || Vector2.Distance(c, tip) < 12 * k) continue;
            uint col = Col(Handle.X + a, a);
            var dir = Vector2.Normalize(tip - c);
            var perp = new Vector2(-dir.Y, dir.X);
            Vector2 s0 = origin + c, s1 = origin + tip;
            dl.AddLine(s0, s1, Backing, 5.5f * k);
            dl.AddLine(s0, s1, col, 2.5f * k);
            if (Mode == GizmoMode.Translate)
            {
                Vector2 t0 = s1 + dir * 13 * k, t1 = s1 + perp * 6 * k, t2 = s1 - perp * 6 * k;
                dl.AddTriangle(t0, t1, t2, Backing, 3f * k);
                dl.AddTriangleFilled(t0, t1, t2, col);
            }
            else
            {
                dl.AddRectFilled(s1 - new Vector2(7 * k), s1 + new Vector2(7 * k), Backing);
                dl.AddRectFilled(s1 - new Vector2(5 * k), s1 + new Vector2(5 * k), col);
            }
            var label = s1 + dir * 24 * k - ImGui.CalcTextSize(AxisNames[a]) * 0.5f;
            dl.AddText(label + new Vector2(k), Backing, AxisNames[a]);
            dl.AddText(label, col, AxisNames[a]);
        }
        uint cc = Lit(Handle.Center) ? Theme.HoverU32 : 0xFFE8E8E8;
        dl.AddRect(origin + c - new Vector2(7 * k), origin + c + new Vector2(7 * k), Backing, 0, ImDrawFlags.None, 4f * k);
        dl.AddRect(origin + c - new Vector2(6 * k), origin + c + new Vector2(6 * k), cc, 0, ImDrawFlags.None, 2f * k);
    }

    /// <summary>Diamond on the element a CapsLock drag is aligning to.</summary>
    static void DrawAlignMarker(ImDrawListPtr dl, Vector2 p)
    {
        float r = Theme.Px(7);
        Vector2 a = p + new Vector2(0, -r), b = p + new Vector2(r, 0), c = p + new Vector2(0, r), d = p + new Vector2(-r, 0);
        dl.AddQuad(a, b, c, d, Backing, Theme.Px(4));
        dl.AddQuad(a, b, c, d, Theme.HoverU32, Theme.Px(2));
    }
}
