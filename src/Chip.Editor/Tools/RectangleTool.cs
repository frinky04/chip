using System.Numerics;
using Chip.Core.Geometry.Primitives;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using Chip.Editor.Framework;
using Chip.Editor.Tools.Block;
using Chip.Editor.UI;
using ImGuiNET;

namespace Chip.Editor.Tools;

/// <summary>
/// Add Rectangle (Scythe): drag a rectangle on a surface or the grid, like a block base, and release to create a single
/// one-sided quad. Its four open edges are the start of edge-extrude workflows. Ctrl stands the plane up toward the camera.
/// </summary>
public sealed class RectangleTool : IViewportTool
{
    public string Id => "rectangle";
    public string Name => "Rectangle";
    public Icon Icon => Icon.Rectangle;
    public string? Hotkey => "Shift+R";
    public int Order => 11;

    readonly ConstructionPlane _plane = new();
    bool _dragging;
    Vector3 _a, _b, _facing;
    Vector3? _cursor;

    public bool Busy => _dragging;

    public (string Key, string Action)[] Hints => _dragging
        ? [("Release", "create the rectangle"), ("Esc", "cancel")]
        : [("Drag", "draw a rectangle on a surface or the grid"), ("Ctrl+drag", "on a plane facing you"), ("Esc", "exit tool")];

    public bool Cancel(EditorContext ctx)
    {
        bool was = _dragging;
        _dragging = false;
        _cursor = null;
        return was;
    }

    public void Update(EditorContext ctx)
    {
        ctx.Hover = null;
        var ray = ctx.MouseRay();
        if (!_dragging)
        {
            _cursor = ctx.ViewportHovered ? _plane.Pick(ctx, ImGui.GetIO().KeyCtrl) : null;
            if (_cursor == null || !ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return;
            _a = _b = _cursor.Value;
            // Face the side of the plane the camera is on, so the new quad is visible.
            _facing = Vector3.Dot(_plane.Normal, ray.Dir) > 0 ? -_plane.Normal : _plane.Normal;
            _dragging = true;
            return;
        }
        if (_plane.Hit(ray) is { } p) _b = p;
        if (ImGui.IsMouseDown(ImGuiMouseButton.Left)) return;
        _dragging = false;
        if (Primitive.Rectangle(_a, _b, _facing, ctx.ActiveMaterial, ctx.Materials.MappingFor(ctx.ActiveMaterial)) is not { } mesh) return;
        ctx.Session.Do("Create Rectangle", () =>
        {
            ctx.Doc.Add(mesh);
            ctx.Sel.ConvertTo(SelectMode.Object, ctx.Doc);
            ctx.Sel.Clear();
            ctx.Sel.Add(ElementRef.Object(mesh.Id));
        });
    }

    public void Draw(EditorContext ctx, ImDrawListPtr dl, Vector2 origin)
    {
        var v = ctx.View;
        if (!_dragging)
        {
            if (_cursor is { } c) _plane.DrawCursor(dl, origin, v, c);
            return;
        }
        var (min, max) = (Vector3.Min(_a, _b), Vector3.Max(_a, _b));
        Overlay.Box(dl, origin, v, min, max, Theme.AccentU32, Theme.Px(2));
        var center = (min + max) * 0.5f;
        float len = MathF.Max(ctx.Grid, MathF.Min(1, Vector3.Distance(min, max) * 0.2f));
        Overlay.Line(dl, origin, v, center, center + _facing * len, Overlay.Faint, Theme.Px(1.5f)); // which side the face points to
        var d = max - min;
        var (u, w) = Primitive.BaseAxes(_plane.Normal);
        Overlay.Label(dl, origin, v, max, $"{MathF.Abs(Vector3.Dot(d, u)):0.###} × {MathF.Abs(Vector3.Dot(d, w)):0.###} m");
    }
}
