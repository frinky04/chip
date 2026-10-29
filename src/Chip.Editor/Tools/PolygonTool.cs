using System.Numerics;
using Chip.Core.Geometry.Primitives;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using Chip.Editor.Features.Dimensions;
using Chip.Editor.Framework;
using Chip.Editor.Tools.Block;
using Chip.Editor.UI;
using ImGuiNET;

namespace Chip.Editor.Tools;

/// <summary>
/// Polygon draw tool (Hammer): click snapped points on a surface or the grid, close the outline with Enter (or by clicking
/// the first point), move to set the height, then click to extrude it into a closed prism. Concave outlines are fine;
/// crossing edges are refused. Shift on the first click stands the plane up toward the camera.
/// </summary>
public sealed class PolygonTool : IViewportTool
{
    public string Id => "polygon";
    public string Name => "Polygon";
    public Icon Icon => Icon.Polygon;
    public string? Hotkey => "Shift+P";
    public int Order => 12;

    enum Stage { Idle, Points, Height }

    readonly ConstructionPlane _plane = new();
    readonly List<Vector3> _points = new();
    Stage _stage;
    Vector3? _cursor;
    float _height, _grid = 0.25f;

    bool _frozen; // set by PreviewForCapture: hold the gesture still for headless captures

    public bool Busy => _stage != Stage.Idle;

    public (string Key, string Action)[] Hints => _stage switch
    {
        Stage.Idle => [("Click", "place the first point"), ("Shift+click", "on a plane facing you"), ("Ctrl", "no snap"), ("Esc", "exit tool")],
        Stage.Points => [("Click", "add a point"), ("Ctrl", "no snap"), ("Enter", "close the outline"), ("Backspace", "remove the last point"), ("Esc", "cancel")],
        _ => [("Move", "set height"), ("Ctrl", "no snap"), ("Click/Enter", "create"), ("Esc", "cancel")],
    };

    public bool Cancel(EditorContext ctx)
    {
        bool was = Busy;
        _stage = Stage.Idle;
        _points.Clear();
        _cursor = null;
        _frozen = false;
        return was;
    }

    public void Update(EditorContext ctx)
    {
        ctx.Hover = null;
        if (_frozen) return; // capture preview: ignore the (absent) mouse
        _grid = ctx.Grid;
        var ray = ctx.MouseRay();
        bool click = ctx.ViewportHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        bool enter = ImGui.IsKeyPressed(ImGuiKey.Enter, false) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false);
        switch (_stage)
        {
            case Stage.Idle:
                _cursor = ctx.ViewportHovered ? _plane.Pick(ctx, ImGui.GetIO().KeyShift, ImGui.GetIO().KeyCtrl) : null;
                if (_cursor is not { } first || !click) return;
                _points.Add(first);
                _stage = Stage.Points;
                return;

            case Stage.Points:
                _cursor = _plane.Hit(ray, ImGui.GetIO().KeyCtrl);
                if (ImGui.IsKeyPressed(ImGuiKey.Backspace))
                {
                    _points.RemoveAt(_points.Count - 1);
                    if (_points.Count == 0) Cancel(ctx);
                    return;
                }
                if (enter || (click && _points.Count >= 3 && _cursor is { } c0 && OnFirstPoint(ctx, c0)))
                {
                    Close(ctx);
                    return;
                }
                if (click && _cursor is { } c && Vector3.DistanceSquared(c, _points[^1]) > 1e-10f) _points.Add(c);
                return;

            default:
                if (_plane.Height(ray, Centroid(), ImGui.GetIO().KeyCtrl) is { } h) _height = h;
                if (!click && !enter) return;
                Create(ctx);
                return;
        }
    }

    bool OnFirstPoint(EditorContext ctx, Vector3 c)
    {
        if (Vector3.DistanceSquared(c, _points[0]) < 1e-10f) return true;
        return ctx.View.Project(_points[0], out var p) && Vector2.Distance(p, ctx.Mouse) < Theme.Px(8);
    }

    void Close(EditorContext ctx)
    {
        if (_points.Count < 3) { ctx.Status("Place at least three points before closing the outline"); return; }
        if (!Primitive.IsSimple(_points, _plane.Normal))
        {
            ctx.Status("The outline crosses itself. Press Backspace to remove points until the edges no longer cross", error: true);
            return;
        }
        ctx.ClearStatus();
        _stage = Stage.Height;
        _height = _grid;
    }

    void Create(EditorContext ctx)
    {
        float h = MathF.Abs(_height) < 1e-4f ? _grid : _height;
        var mesh = Primitive.Prism(_points, _plane.Normal * h, ctx.ActiveMaterial, ctx.Materials.MappingFor(ctx.ActiveMaterial));
        Cancel(ctx);
        if (mesh == null) return;
        mesh.Name = "polygon";
        ctx.Session.Do("Create Polygon", () =>
        {
            ctx.Doc.Add(mesh);
            ctx.Sel.ConvertTo(SelectMode.Object, ctx.Doc);
            ctx.Sel.Clear();
            ctx.Sel.Add(ElementRef.Object(mesh.Id));
        });
    }

    Vector3 Centroid()
    {
        var c = Vector3.Zero;
        foreach (var p in _points) c += p;
        return c / Math.Max(1, _points.Count);
    }

    /// <summary>Puts the tool mid-gesture with an L-shaped outline raised 2 m, so headless captures show the preview.</summary>
    public void PreviewForCapture()
    {
        _points.Clear();
        _points.AddRange([new(13, 0, 1), new(19, 0, 1), new(19, 0, 3), new(15, 0, 3), new(15, 0, 7), new(13, 0, 7)]);
        _height = 2;
        _stage = Stage.Height;
        _frozen = true;
    }

    public void Draw(EditorContext ctx, ImDrawListPtr dl, Vector2 origin)
    {
        var v = ctx.View;
        float w = Theme.Px(2);
        switch (_stage)
        {
            case Stage.Idle:
                if (_cursor is { } c) _plane.DrawCursor(dl, origin, v, c);
                return;
            case Stage.Points:
                for (int i = 0; i + 1 < _points.Count; i++) Overlay.Line(dl, origin, v, _points[i], _points[i + 1], Theme.AccentU32, w);
                if (_cursor is { } cur)
                {
                    Overlay.Line(dl, origin, v, _points[^1], cur, Theme.AccentU32, w);
                    if (_points.Count >= 2) Overlay.Line(dl, origin, v, cur, _points[0], Overlay.Faint, Theme.Px(1)); // closing edge
                    _plane.DrawCursor(dl, origin, v, cur);
                }
                DimensionDraw.Path(dl, origin, v, _cursor is { } end ? [.. _points, end] : _points);
                for (int i = 0; i < _points.Count; i++) Overlay.Dot(dl, origin, v, _points[i], hollow: i == 0 && _points.Count >= 3);
                return;
            default:
                {
                    float h = MathF.Abs(_height) < 1e-4f ? _grid : _height;
                    if (Primitive.Prism(_points, _plane.Normal * h, "", TextureMapping.Default) is { } preview)
                        Overlay.Wire(dl, origin, v, preview);
                    // The height beside the prism's upright edge at the first corner, pushed away from its middle.
                    var up = _plane.Normal * h;
                    DimensionDraw.Edge(dl, origin, v, _points[0], _points[0] + up, Centroid() + up * 0.5f,
                        DimensionDraw.ColorOf(DimensionDraw.AxisOf(_points[0], _points[0] + up)), text: Labels.Measure(MathF.Abs(h)));
                    return;
                }
        }
    }
}
