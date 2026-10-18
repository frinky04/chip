using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Picking;
using Chip.Editor.UI;
using ImGuiNET;

namespace Chip.Editor.Viewport;

public enum GizmoMode { Translate, Rotate, Scale }

public enum Handle { None, X, Y, Z, PlaneYZ, PlaneXZ, PlaneXY, Center }

/// <summary>
/// World-axis transform gizmo drawn in screen space. It produces a transform relative to the drag start;
/// the caller applies it through a <c>TransformGesture</c>. Constant on-screen size.
/// </summary>
public sealed class Gizmo
{
    const float RotateSnapDeg = 15f, ScaleSnap = 0.125f;
    static float LengthPx => 96 * Theme.Scale;
    static float PickPx => 9 * Theme.Scale;
    static readonly Vector3[] Axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];

    public GizmoMode Mode = GizmoMode.Translate;
    public Handle Hover { get; private set; }
    public Handle Active { get; private set; }
    public bool Dragging => Active != Handle.None;

    Vector3 _pivot;
    Vector2 _startMouse;
    float _startAxisParam;
    Vector3 _startPlaneHit;
    float _prevScreenAngle, _angle;

    static Vector3 AxisOf(Handle h) => h switch
    {
        Handle.X or Handle.PlaneYZ => Vector3.UnitX,
        Handle.Y or Handle.PlaneXZ => Vector3.UnitY,
        _ => Vector3.UnitZ,
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
            for (int a = 0; a < 3; a++)
            {
                var pts = Ring(v, pivot, Axes[a], len);
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
            if (!v.Project(pivot + Axes[a] * len, out var tip)) continue;
            if (Vector2.Distance(c, tip) < 12 * Theme.Scale) continue; // axis points at the camera
            if (Picker.DistanceToSegment(mouse, c, tip, out _) < PickPx) return Handle.X + a;
        }
        return Handle.None;
    }

    public void UpdateHover(ViewInfo v, Vector3 pivot, Vector2 mouse) => Hover = Dragging ? Hover : HitTest(v, pivot, mouse);

    public void BeginDrag(Handle h, ViewInfo v, Vector3 pivot, Vector2 mouse)
    {
        Active = h;
        _pivot = pivot;
        _startMouse = mouse;
        _angle = 0;
        _prevScreenAngle = ScreenAngle(v, mouse);
        var ray = v.ScreenRay(mouse);
        _startAxisParam = AxisParam(ray, pivot, AxisOf(h));
        _startPlaneHit = PlaneHit(v, ray, h, pivot) ?? pivot;
    }

    public void EndDrag() => Active = Handle.None;

    /// <summary>Transform from the drag start to now, and whether it is rigid (keeps texture lock valid).</summary>
    public (Matrix4x4 M, bool Rigid) DragTransform(ViewInfo v, Vector2 mouse, float grid, bool snap)
    {
        var ray = v.ScreenRay(mouse);
        switch (Mode)
        {
            case GizmoMode.Translate:
            {
                Vector3 delta;
                if (Active is Handle.X or Handle.Y or Handle.Z)
                {
                    var axis = AxisOf(Active);
                    float d = AxisParam(ray, _pivot, axis) - _startAxisParam;
                    delta = axis * (snap ? MeshOps.Snap(d, grid) : d);
                }
                else
                {
                    var hit = PlaneHit(v, ray, Active, _pivot);
                    delta = hit is { } h ? h - _startPlaneHit : Vector3.Zero;
                    if (snap) delta = new Vector3(MeshOps.Snap(delta.X, grid), MeshOps.Snap(delta.Y, grid), MeshOps.Snap(delta.Z, grid));
                    if (Active != Handle.Center) delta -= AxisOf(Active) * Vector3.Dot(delta, AxisOf(Active));
                }
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
                if (snap) world = MeshOps.Snap(world, RotateSnapDeg * MathF.PI / 180f);
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
                    scale = Vector3.One + axis * (s - 1);
                }
                return (About(Matrix4x4.CreateScale(scale)), false);
            }
        }
    }

    Matrix4x4 About(Matrix4x4 m) => Matrix4x4.CreateTranslation(-_pivot) * m * Matrix4x4.CreateTranslation(_pivot);

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

    static Vector3? PlaneHit(ViewInfo v, Ray ray, Handle h, Vector3 pivot)
    {
        var n = h == Handle.Center ? Vector3.Normalize(v.Eye - pivot) : AxisOf(h);
        return Picker.RayPlane(ray, pivot, n, out float t) ? ray.At(t) : null;
    }

    static (Vector3, Vector3) PlaneAxes(int normalAxis) => normalAxis switch
    {
        0 => (Vector3.UnitY, Vector3.UnitZ),
        1 => (Vector3.UnitX, Vector3.UnitZ),
        _ => (Vector3.UnitX, Vector3.UnitY),
    };

    static Vector2[]? PlaneQuad(ViewInfo v, Vector3 pivot, Vector3 u, Vector3 w, float len)
    {
        float a = len * 0.25f, b = len * 0.45f;
        var corners = new[] { pivot + u * a + w * a, pivot + u * b + w * a, pivot + u * b + w * b, pivot + u * a + w * b };
        var result = new Vector2[4];
        for (int i = 0; i < 4; i++) if (!v.Project(corners[i], out result[i])) return null;
        return result;
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

    static List<Vector2> Ring(ViewInfo v, Vector3 pivot, Vector3 axis, float radius)
    {
        var (u, w) = PlaneAxes(axis == Vector3.UnitX ? 0 : axis == Vector3.UnitY ? 1 : 2);
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

    /// <summary>Every stroke sits on a dark backing so handles stay legible over orange and grey dev textures.</summary>
    public void Draw(ImDrawListPtr dl, Vector2 origin, ViewInfo v, Vector3 pivot)
    {
        if (!v.Project(pivot, out var c)) return;
        float len = WorldLength(v, pivot);
        float k = Theme.Scale;
        uint Col(Handle h, int a) => (Active == h || (!Dragging && Hover == h)) ? Theme.HoverU32 : Theme.AxisU32[a];

        if (Mode == GizmoMode.Rotate)
        {
            for (int a = 0; a < 3; a++)
            {
                var pts = Ring(v, pivot, Axes[a], len).Select(p => p + origin).ToArray();
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
            if (!v.Project(pivot + Axes[a] * len, out var tip) || Vector2.Distance(c, tip) < 12 * k) continue;
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
        uint cc = Active == Handle.Center || (!Dragging && Hover == Handle.Center) ? Theme.HoverU32 : 0xFFE8E8E8;
        dl.AddRect(origin + c - new Vector2(7 * k), origin + c + new Vector2(7 * k), Backing, 0, ImDrawFlags.None, 4f * k);
        dl.AddRect(origin + c - new Vector2(6 * k), origin + c + new Vector2(6 * k), cc, 0, ImDrawFlags.None, 2f * k);
    }
}
