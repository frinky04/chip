using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Picking;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;

namespace Chip.Editor.Tools;

/// <summary>
/// Hammer-style block creation: drag the base on the hovered surface (or the ground), release,
/// move to set the height, click or Enter to create. The construction plane is axis-aligned and snapped.
/// </summary>
public sealed class BlockTool : IViewportTool
{
    public string Id => "block";
    public string Name => "Block";
    public Icon Icon => Icon.Block;
    public string? Hotkey => "Shift+B";
    public int Order => 10;

    enum Stage { Idle, Base, Height }

    Stage _stage;
    Vector3 _planePoint, _normal, _a, _b;
    float _height, _grid = 0.25f;
    Vector3? _cursor; // where a drag would start, shown before the first click

    public bool Busy => _stage != Stage.Idle;

    public (string Key, string Action)[] Hints => _stage switch
    {
        Stage.Idle => [("Drag", "draw a base on a surface or the grid"), ("Esc", "exit tool")],
        Stage.Base => [("Release", "set the base")],
        _ => [("Move", "set height"), ("Click/Enter", "create"), ("Esc", "cancel")],
    };

    public bool Cancel(EditorContext ctx)
    {
        bool wasBusy = Busy;
        _stage = Stage.Idle;
        _cursor = null;
        return wasBusy;
    }

    public void Update(EditorContext ctx)
    {
        ctx.Hover = null;
        if (Step(ctx.View, ctx.Doc, ctx.Mouse, ctx.ViewportHovered, ctx.Grid, ctx.Snap, ctx.Backfaces) is not { } box) return;
        ctx.Session.Do("Create Block", () =>
        {
            var mesh = ctx.Doc.Add(MeshOps.CreateBox(box.Min, box.Max, ctx.ActiveMaterial, TextureMapping.Default));
            ctx.Sel.ConvertTo(SelectMode.Object, ctx.Doc);
            ctx.Sel.Clear();
            ctx.Sel.Add(ElementRef.Object(mesh.Id));
        });
    }

    public void Draw(EditorContext ctx, ImDrawListPtr dl, Vector2 origin) => Draw(dl, origin, ctx.View);

    /// <summary>Snapped construction cursor: a cross along the construction plane, one grid step per arm.</summary>
    void DrawCursor(ImDrawListPtr dl, Vector2 origin, ViewInfo v, Vector3 c)
    {
        var u = MathF.Abs(_normal.Y) > 0.5f ? Vector3.UnitX : Vector3.UnitY;
        var w = Vector3.Cross(_normal, u);
        foreach (var axis in new[] { u, w })
        {
            if (!v.Project(c - axis * _grid, out var a) || !v.Project(c + axis * _grid, out var b)) continue;
            dl.AddLine(origin + a, origin + b, 0xE6100F0E, Theme.Px(4));
            dl.AddLine(origin + a, origin + b, Theme.AccentU32, Theme.Px(1.5f));
        }
        if (v.Project(c, out var p))
        {
            dl.AddCircleFilled(origin + p, Theme.Px(4), 0xE6100F0E);
            dl.AddCircleFilled(origin + p, Theme.Px(2.5f), Theme.AccentU32);
        }
    }

    /// <summary>Feeds one frame of input. Returns box corners when the user confirms a block.</summary>
    (Vector3 Min, Vector3 Max)? Step(ViewInfo view, Document doc, Vector2 mouse, bool hovered, float grid, bool snap, bool backfaces)
    {
        _grid = grid;
        var ray = view.ScreenRay(mouse);
        float S(float x) => snap ? MeshOps.Snap(x, grid) : x;
        Vector3 SnapInPlane(Vector3 p)
        {
            var q = new Vector3(S(p.X), S(p.Y), S(p.Z));
            return q - _normal * Vector3.Dot(q - _planePoint, _normal); // stay exactly on the plane
        }

        Vector3? StartPoint()
        {
            if (Picker.RaycastFace(doc, ray, backfaces) is { } hit)
            {
                _normal = TextureMapping.DominantAxis(hit.Normal);
                _planePoint = _normal * S(Vector3.Dot(hit.Point, _normal));
            }
            else
            {
                _normal = Vector3.UnitY;
                _planePoint = Vector3.Zero;
            }
            return Picker.RayPlane(ray, _planePoint, _normal, out float t0) ? SnapInPlane(ray.At(t0)) : null;
        }

        switch (_stage)
        {
            case Stage.Idle:
                _cursor = hovered ? StartPoint() : null;
                if (_cursor == null || !ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return null;
                _a = _b = _cursor.Value;
                _stage = Stage.Base;
                return null;

            case Stage.Base:
                if (Picker.RayPlane(ray, _planePoint, _normal, out float t1)) _b = SnapInPlane(ray.At(t1));
                if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
                {
                    var d = Vector3.Abs(_b - _a);
                    int flat = (d.X < 1e-3f ? 1 : 0) + (d.Y < 1e-3f ? 1 : 0) + (d.Z < 1e-3f ? 1 : 0);
                    _stage = flat > 1 ? Stage.Idle : Stage.Height;
                    _height = grid;
                }
                return null;

            default:
                {
                    // Height = closest point on the normal line through the far corner to the mouse ray.
                    var w0 = _b - ray.Origin;
                    float bd = Vector3.Dot(_normal, ray.Dir);
                    float denom = 1 - bd * bd;
                    if (denom > 1e-5f) _height = S((bd * Vector3.Dot(ray.Dir, w0) - Vector3.Dot(_normal, w0)) / denom);
                    bool confirm = (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) || ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter);
                    if (!confirm) return null;
                    _stage = Stage.Idle;
                    var (min, max) = Box();
                    return (min, max);
                }
        }
    }

    (Vector3 Min, Vector3 Max) Box()
    {
        float h = MathF.Abs(_height) < 1e-4f ? _grid : _height;
        var c = _b + _normal * h;
        var min = Vector3.Min(Vector3.Min(_a, _b), c);
        var max = Vector3.Max(Vector3.Max(_a, _b), c);
        return (min, max);
    }

    void Draw(ImDrawListPtr dl, Vector2 origin, ViewInfo v)
    {
        if (_stage == Stage.Idle)
        {
            if (_cursor is { } c) DrawCursor(dl, origin, v, c);
            return;
        }
        var (min, max) = _stage == Stage.Base ? (Vector3.Min(_a, _b), Vector3.Max(_a, _b)) : Box();
        Span<Vector3> corners = stackalloc Vector3[8];
        for (int i = 0; i < 8; i++)
            corners[i] = new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
        int[] edges = [0, 1, 2, 3, 4, 5, 6, 7, 0, 2, 1, 3, 4, 6, 5, 7, 0, 4, 1, 5, 2, 6, 3, 7];
        for (int i = 0; i < edges.Length; i += 2)
            if (v.Project(corners[edges[i]], out var p) && v.Project(corners[edges[i + 1]], out var q))
                dl.AddLine(origin + p, origin + q, Theme.AccentU32, Theme.Px(2));
        var size = max - min;
        if (!v.Project(max, out var anchor)) return;
        string text = $"{size.X:0.###} × {size.Y:0.###} × {size.Z:0.###} m";
        var ts = ImGui.CalcTextSize(text);
        var pad = Theme.Px(6, 3);
        var pos = Vector2.Clamp(anchor + new Vector2(Theme.Px(10), -ts.Y - Theme.Px(12)), new Vector2(Theme.Px(6)), v.Size - ts - Theme.Px(14, 10));
        dl.AddRectFilled(origin + pos - pad, origin + pos + ts + pad, 0xE0141416, Theme.Px(2));
        dl.AddText(origin + pos, Theme.AccentU32, text);
    }
}
