using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Picking;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;
using Ops = Chip.Core.Geometry.MeshOps;

namespace Chip.Editor.Features.MeshEditing;

/// <summary>
/// Interactive offset (Shift+O): extrudes the selected faces along their own normals with mitred corners, so a strip of
/// wall faces becomes a baseboard or trim of even thickness. Starting it builds the topology once and previews the last
/// distance used; moving the mouse right pushes out, left pulls in (grid-snapped; Ctrl for free), click or Enter applies,
/// Esc undoes. One undo step. A transient tool like inset: not on the tool strip, and it hands back to the default tool.
/// </summary>
public sealed class OffsetTool : IViewportTool
{
    List<(PolyMesh Mesh, OffsetResult Offset)>? _parts;
    bool _leaving;
    Vector2 _lastMouse;
    Vector3 _pivot;
    float _amount, _raw;
    int _faces;
    /// <summary>Distance last applied, reused as the starting distance next time.</summary>
    float _remembered;

    public string Id => "offset";
    public string Name => "Offset";
    public Icon Icon => Icon.Face;
    public string? Hotkey => null;
    public int Order => 101;
    public bool Busy => _parts != null;
    public float Amount => _amount;

    public (string Key, string Action)[] Hints =>
        [("Move", "set distance"), ("Ctrl", "free distance"), ("Click/Enter", "apply"), ("Esc", "cancel")];

    public void Activate(EditorContext ctx)
    {
        var sets = ctx.Sel.FaceSets(ctx.Doc).Where(kv => !kv.Key.Locked).ToDictionary();
        if (sets.Count == 0 || ctx.SelectionPivot() is not { } pivot)
        {
            ctx.Status("Select faces to offset", error: true);
            Leave(ctx);
            return;
        }
        ctx.Session.Begin(Name, sets.Keys.Select(m => m.Id));
        _parts = sets.Select(kv => (kv.Key, Ops.Offset(kv.Key, kv.Value))).ToList();
        _faces = sets.Sum(kv => kv.Value.Count);
        _pivot = pivot;
        _lastMouse = ctx.Mouse;
        _raw = _remembered != 0 ? _remembered : ctx.Grid;
        SetAmount(ctx, _raw);
    }

    public bool Cancel(EditorContext ctx)
    {
        if (_parts == null) return false;
        _parts = null;
        ctx.Session.Cancel();
        Leave(ctx);
        return true;
    }

    public void Update(EditorContext ctx)
    {
        if (_parts == null) return;
        ctx.Hover = null;
        // Only motion over the viewport counts, so reaching for the inspector field doesn't change the distance.
        float dx = ctx.Mouse.X - _lastMouse.X;
        _lastMouse = ctx.Mouse;
        if (ctx.ViewportHovered && dx != 0)
        {
            _raw += dx * UnitsPerPixel(ctx);
            SetAmount(ctx, Snapped(ctx, _raw));
        }
        bool confirm = (ctx.ViewportHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            || ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter);
        if (confirm) Commit(ctx);
    }

    /// <summary>Grid-snapped distance. Snapping follows the global toggle; Ctrl holds it off (as in bevel).</summary>
    static float Snapped(EditorContext ctx, float d) => ctx.Snap && !ImGui.GetIO().KeyCtrl ? Ops.Snap(d, ctx.Grid) : d;

    /// <summary>Applies the previewed distance as one undo step (a zero distance is discarded).</summary>
    public void Commit(EditorContext ctx)
    {
        if (_parts == null) return;
        if (MathF.Abs(_amount) < 1e-4f)
        {
            Cancel(ctx);
            return;
        }
        _parts = null;
        _remembered = _amount;
        ctx.Session.End();
        ctx.Status($"Offset {(_faces == 1 ? "1 face" : $"{_faces} faces")} by {Labels.Length(_amount)}");
        Leave(ctx);
    }

    /// <summary>Sets the previewed distance; negative pulls the faces in.</summary>
    public void SetAmount(EditorContext ctx, float amount)
    {
        if (_parts == null) return;
        _amount = amount;
        foreach (var (mesh, offset) in _parts) offset.Apply(mesh, _amount);
        ctx.Session.Touch();
    }

    /// <summary>World size of one screen pixel at the selection, from the view itself (works in any projection).</summary>
    float UnitsPerPixel(EditorContext ctx)
    {
        var view = ctx.View;
        float fallback = ctx.Grid / 16;
        if (!view.Project(_pivot, out var p)) return fallback;
        var normal = view.Eye - _pivot;
        if (normal.LengthSquared() < 1e-10f) return fallback;
        normal = Vector3.Normalize(normal);
        var r0 = view.ScreenRay(p);
        var r1 = view.ScreenRay(p + Vector2.UnitY);
        if (!Picker.RayPlane(r0, _pivot, normal, out float t0) || !Picker.RayPlane(r1, _pivot, normal, out float t1)) return fallback;
        return MathF.Max(Vector3.Distance(r0.At(t0), r1.At(t1)), 1e-5f);
    }

    /// <summary>Hands back to the default tool. Guarded because switching tools calls <see cref="Cancel"/> again.</summary>
    void Leave(EditorContext ctx)
    {
        if (_leaving || ctx.ActiveTool != this || ctx.DefaultTool == null) return;
        _leaving = true;
        ctx.SetTool(ctx.DefaultTool);
        _leaving = false;
    }

    public void Draw(EditorContext ctx, ImDrawListPtr dl, Vector2 origin)
    {
        if (_parts == null) return;
        string text = $"{Name} {Labels.Length(_amount)}";
        var ts = ImGui.CalcTextSize(text);
        var pad = Theme.Px(6, 3);
        var pos = Vector2.Clamp(ctx.Mouse + Theme.Px(16, 12), new Vector2(Theme.Px(6)), ctx.ViewportSize - ts - Theme.Px(14, 10));
        dl.AddRectFilled(origin + pos - pad, origin + pos + ts + pad, 0xE0141416, Theme.Px(2));
        dl.AddText(origin + pos, Theme.AccentU32, text);
    }

    public bool HasOptions => true;

    public void DrawOptions(EditorContext ctx)
    {
        if (_parts == null) return;
        RowLabel("Amount", "How far each face moves along its own normal, in metres. Negative pulls the faces in. Drag or type, then click the viewport or press Enter to apply.");
        ImGui.SetNextItemWidth(-1);
        float a = _amount;
        if (ImGui.DragFloat("##offsetDistance", ref a, 0.005f, -1000, 1000, "%.3f m"))
        {
            _raw = a;
            SetAmount(ctx, a);
        }
        ImGui.TextColored(Theme.TextDim, _amount >= 0 ? "Faces push out; corners stay mitred" : "Faces pull in; corners stay mitred");
    }
}
