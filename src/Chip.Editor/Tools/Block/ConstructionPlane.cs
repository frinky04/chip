using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Geometry.Primitives;
using Chip.Core.Picking;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;

namespace Chip.Editor.Tools.Block;

/// <summary>
/// The axis-aligned, grid-snapped plane the creation tools draw on: the hovered surface, else the ground.
/// With Ctrl the plane stands up toward the camera (Scythe): it passes through the same point but uses the world axis
/// that faces the view most, excluding the surface normal, so a base can be drawn face-on in mid-air.
/// </summary>
public sealed class ConstructionPlane
{
    public Vector3 Point { get; private set; }
    public Vector3 Normal { get; private set; } = Vector3.UnitY;
    float _grid = 0.25f;
    bool _snap = true;

    float S(float x) => _snap ? MeshOps.Snap(x, _grid) : x;

    /// <summary>Snapped point on the plane, kept exactly in it.</summary>
    public Vector3 SnapInPlane(Vector3 p)
    {
        var q = new Vector3(S(p.X), S(p.Y), S(p.Z));
        return q - Normal * Vector3.Dot(q - Point, Normal);
    }

    /// <summary>Chooses the plane under the mouse and returns the snapped start point on it (null when nothing is under the cursor).</summary>
    public Vector3? Pick(EditorContext ctx, bool flipTowardCamera)
    {
        _grid = ctx.Grid;
        _snap = ctx.Snap;
        var ray = ctx.MouseRay();
        Vector3 normal, point;
        if (ctx.Picks.RaycastFace(ray, ctx.Backfaces) is { } hit)
        {
            normal = TextureMapping.DominantAxis(hit.Normal);
            point = normal * S(Vector3.Dot(hit.Point, normal));
        }
        else
        {
            normal = Vector3.UnitY;
            point = Vector3.Zero;
        }
        if (!Picker.RayPlane(ray, point, normal, out float t)) return null;
        Normal = normal;
        Point = point;
        var start = SnapInPlane(ray.At(t));
        if (!flipTowardCamera) return start;

        var toCamera = -ray.Dir;
        toCamera -= Normal * Vector3.Dot(toCamera, Normal);
        if (toCamera.LengthSquared() < 1e-8f) toCamera = Primitive.BaseAxes(Normal).U; // looking straight along the normal
        Normal = TextureMapping.DominantAxis(toCamera);
        Point = start;
        return start;
    }

    /// <summary>Where the mouse ray meets the chosen plane, snapped. Keeps the plane and snap settings from <see cref="Pick"/>.</summary>
    public Vector3? Hit(Ray ray) => Picker.RayPlane(ray, Point, Normal, out float t) ? SnapInPlane(ray.At(t)) : null;

    /// <summary>Snapped height along the normal: the point on the normal line through <paramref name="anchor"/> closest to the ray.</summary>
    public float? Height(Ray ray, Vector3 anchor)
    {
        var w0 = anchor - ray.Origin;
        float bd = Vector3.Dot(Normal, ray.Dir);
        float denom = 1 - bd * bd;
        if (denom <= 1e-5f) return null;
        return S((bd * Vector3.Dot(ray.Dir, w0) - Vector3.Dot(Normal, w0)) / denom);
    }

    /// <summary>Snapped construction cursor: a cross along the plane, one grid step per arm.</summary>
    public void DrawCursor(ImDrawListPtr dl, Vector2 origin, ViewInfo v, Vector3 c)
    {
        var (u, w) = Primitive.BaseAxes(Normal);
        foreach (var axis in new[] { u, w })
        {
            if (!v.Project(c - axis * _grid, out var a) || !v.Project(c + axis * _grid, out var b)) continue;
            dl.AddLine(origin + a, origin + b, Overlay.Shadow, Theme.Px(4));
            dl.AddLine(origin + a, origin + b, Theme.AccentU32, Theme.Px(1.5f));
        }
        Overlay.Dot(dl, origin, v, c);
    }
}
