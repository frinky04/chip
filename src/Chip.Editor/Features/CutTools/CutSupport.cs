using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Picking;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;

namespace Chip.Editor.Features.CutTools;

public enum CutSnapKind { Vertex, Edge, Surface, Ground }

/// <summary>A point placed by a cut tool, with the surface normal it was placed on.</summary>
public readonly record struct CutPoint(Vector3 Position, Vector3 Normal, CutSnapKind Kind);

/// <summary>Point placement and target lookup shared by the clip and knife tools.</summary>
public static class CutSnap
{
    /// <summary>
    /// Where a click would land: a vertex near the cursor, else a point on a nearby edge (on a grid line when snapping),
    /// else a grid point on the hovered surface, else a grid point on the ground. Null if the cursor sees nothing.
    /// </summary>
    public static CutPoint? At(EditorContext ctx, Vector2 mouse)
    {
        var view = ctx.View;
        var ray = view.ScreenRay(mouse);
        var hit = ctx.Picks.RaycastFace(ray, ctx.Backfaces);
        var normal = hit?.Normal ?? Vector3.UnitY;

        if (ctx.Picks.Pick(view, mouse, SelectMode.Vertex, Theme.Scale, ctx.Backfaces) is { } v && ctx.Doc.Find(v.Mesh) is { } vm)
            return new CutPoint(vm.Positions[v.A], normal, CutSnapKind.Vertex);

        if (ctx.Picks.Pick(view, mouse, SelectMode.Edge, Theme.Scale, ctx.Backfaces) is { } e && ctx.Doc.Find(e.Mesh) is { } em)
        {
            var a = em.Positions[e.A];
            var b = em.Positions[e.B];
            float t = ClosestOnSegment(ray, a, b);
            if (ctx.Snap) t = SnapAlong(a, b, t, ctx.Grid);
            return new CutPoint(Vector3.Lerp(a, b, t), normal, CutSnapKind.Edge);
        }

        if (hit is { } h)
        {
            var p = h.Point;
            if (ctx.Snap)
            {
                // Snap on the face's dominant grid plane, then slide back onto the face along that axis.
                var axis = TextureMapping.DominantAxis(h.Normal);
                var q = MeshOps.Snap(p, ctx.Grid);
                float along = Vector3.Dot(axis, h.Normal);
                p = MathF.Abs(along) > 1e-4f ? q + axis * (Vector3.Dot(p - q, h.Normal) / along) : p;
            }
            return new CutPoint(p, h.Normal, CutSnapKind.Surface);
        }

        if (!Picker.RayPlane(ray, Vector3.Zero, Vector3.UnitY, out float tg)) return null;
        var g = ray.At(tg);
        if (ctx.Snap) g = new Vector3(MeshOps.Snap(g.X, ctx.Grid), 0, MeshOps.Snap(g.Z, ctx.Grid));
        return new CutPoint(g, Vector3.UnitY, CutSnapKind.Ground);
    }

    /// <summary>Parameter on segment a–b closest to the ray.</summary>
    public static float ClosestOnSegment(Ray r, Vector3 a, Vector3 b)
    {
        var u = b - a;
        var w0 = a - r.Origin;
        float uu = Vector3.Dot(u, u), ud = Vector3.Dot(u, r.Dir), dd = Vector3.Dot(r.Dir, r.Dir);
        float uw = Vector3.Dot(u, w0), dw = Vector3.Dot(r.Dir, w0);
        float den = uu * dd - ud * ud;
        return den < 1e-9f ? 0 : Math.Clamp((ud * dw - dd * uw) / den, 0, 1);
    }

    /// <summary>Moves <paramref name="t"/> to where the edge crosses a grid line on its dominant axis (or an end).</summary>
    public static float SnapAlong(Vector3 a, Vector3 b, float t, float grid)
    {
        var d = b - a;
        int axis = MathF.Abs(d.X) >= MathF.Abs(d.Y) && MathF.Abs(d.X) >= MathF.Abs(d.Z) ? 0 : MathF.Abs(d.Y) >= MathF.Abs(d.Z) ? 1 : 2;
        float da = Comp(d, axis);
        if (MathF.Abs(da) < 1e-6f) return t;
        float target = MeshOps.Snap(Comp(a, axis) + da * t, grid);
        return Math.Clamp((target - Comp(a, axis)) / da, 0, 1);
    }

    static float Comp(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    /// <summary>Visible, unlocked meshes the cut applies to: the selected ones, or all of them when nothing is selected.</summary>
    public static List<PolyMesh> TargetMeshes(EditorContext ctx)
    {
        var ids = ctx.Sel.MeshIds();
        return ctx.Doc.Meshes.Where(m => !m.Hidden && !m.Locked && (ids.Count == 0 || ids.Contains(m.Id))).ToList();
    }
}

/// <summary>Overlay drawing in world space for the cut tools.</summary>
public static class CutDraw
{
    public const uint Shadow = 0xE6100F0E;

    public static void Line(ImDrawListPtr dl, Vector2 origin, ViewInfo v, Vector3 a, Vector3 b, uint col, float thickness)
    {
        if (!ClipToView(v, ref a, ref b) || !v.Project(a, out var p) || !v.Project(b, out var q)) return;
        dl.AddLine(origin + p, origin + q, col, thickness);
    }

    /// <summary>A line with a dark halo so it reads over any surface.</summary>
    public static void Stroke(ImDrawListPtr dl, Vector2 origin, ViewInfo v, Vector3 a, Vector3 b, uint col, float thickness)
    {
        Line(dl, origin, v, a, b, Shadow, thickness + Theme.Px(2.5f));
        Line(dl, origin, v, a, b, col, thickness);
    }

    public static void Dot(ImDrawListPtr dl, Vector2 origin, ViewInfo v, Vector3 p, uint col, float radius)
    {
        if (!v.Project(p, out var s)) return;
        dl.AddCircleFilled(origin + s, radius + Theme.Px(1.5f), Shadow);
        dl.AddCircleFilled(origin + s, radius, col);
    }

    /// <summary>Snap marker: square on a vertex, ring on an edge, cross on a surface or the ground.</summary>
    public static void Marker(ImDrawListPtr dl, Vector2 origin, ViewInfo v, CutPoint c)
    {
        if (!v.Project(c.Position, out var s)) return;
        var p = origin + s;
        float r = Theme.Px(5);
        switch (c.Kind)
        {
            case CutSnapKind.Vertex:
                dl.AddRectFilled(p - new Vector2(r + 1.5f), p + new Vector2(r + 1.5f), Shadow);
                dl.AddRectFilled(p - new Vector2(r), p + new Vector2(r), Theme.AccentU32);
                break;
            case CutSnapKind.Edge:
                dl.AddCircle(p, r + Theme.Px(1), Shadow, 16, Theme.Px(4));
                dl.AddCircle(p, r + Theme.Px(1), Theme.AccentU32, 16, Theme.Px(2));
                break;
            default:
                foreach (var d in new[] { new Vector2(r * 1.6f, 0), new Vector2(0, r * 1.6f) })
                {
                    dl.AddLine(p - d, p + d, Shadow, Theme.Px(4));
                    dl.AddLine(p - d, p + d, Theme.AccentU32, Theme.Px(1.5f));
                }
                break;
        }
    }

    public static void Label(ImDrawListPtr dl, Vector2 origin, ViewInfo v, Vector3 at, string text, uint col)
    {
        if (!v.Project(at, out var s)) return;
        var ts = ImGui.CalcTextSize(text);
        var pad = Theme.Px(6, 3);
        var pos = Vector2.Clamp(s + new Vector2(Theme.Px(10), -ts.Y - Theme.Px(10)), new Vector2(Theme.Px(6)), v.Size - ts - Theme.Px(14, 10));
        dl.AddRectFilled(origin + pos - pad, origin + pos + ts + pad, 0xE0141416, Theme.Px(2));
        dl.AddText(origin + pos, col, text);
    }

    /// <summary>Trims a segment to the part in front of the camera so lines through the eye still draw.</summary>
    static bool ClipToView(ViewInfo v, ref Vector3 a, ref Vector3 b)
    {
        var ca = Vector4.Transform(new Vector4(a, 1), v.ViewProj);
        var cb = Vector4.Transform(new Vector4(b, 1), v.ViewProj);
        const float near = 1e-3f;
        if (ca.W < near && cb.W < near) return false;
        if (ca.W < near) a = Vector3.Lerp(a, b, (near - ca.W) / (cb.W - ca.W));
        else if (cb.W < near) b = Vector3.Lerp(b, a, (near - cb.W) / (ca.W - cb.W));
        return true;
    }
}
