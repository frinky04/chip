using System.Numerics;
using Chip.Core.Picking;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using Chip.Editor.Viewport;
using ImGuiNET;

namespace Chip.Editor.Tools;

/// <summary>
/// The default tool: click / Shift-add / Ctrl-toggle / box select in the current mode, plus the transform gizmo
/// (mode from <see cref="EditorContext.Gizmo"/>). Shift-dragging the move gizmo in face mode extrudes.
/// </summary>
public sealed class SelectTool : IViewportTool
{
    TransformGesture? _gesture;
    bool _marqueeArmed, _marquee;
    Vector2 _pressPos;

    public string Id => "select";
    public string Name => "Select";
    public Icon Icon => Icon.Select;
    public string? Hotkey => null;
    public int Order => 0;
    public bool Busy => _gesture != null || _marqueeArmed;

    public (string Key, string Action)[] Hints => _gesture != null
        ? [("Esc", "cancel")]
        : [("LMB", "select"), ("Drag", "box select"), ("RMB", "fly"), ("MMB", "pan"), ("Alt+LMB", "orbit"), ("F1", "shortcuts")];

    public bool Cancel(EditorContext ctx)
    {
        if (_gesture != null)
        {
            _gesture.Cancel();
            _gesture = null;
            ctx.Gizmo.EndDrag();
            return true;
        }
        if (_marqueeArmed)
        {
            _marqueeArmed = _marquee = false;
            return true;
        }
        return false;
    }

    public void Update(EditorContext ctx)
    {
        var io = ImGui.GetIO();
        var mouse = ctx.Mouse;
        var gizmo = ctx.Gizmo;

        // An active gizmo drag owns the mouse until release.
        if (_gesture != null)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                var (m, rigid) = gizmo.DragTransform(ctx.View, mouse, ctx.Grid, ctx.Snap);
                _gesture.Apply(m, rigid);
            }
            else
            {
                _gesture.Commit();
                _gesture = null;
                gizmo.EndDrag();
            }
            return;
        }

        var pivot = ctx.SelectionPivot();
        if (pivot is { } pv) gizmo.UpdateHover(ctx.View, pv, mouse);
        bool overGizmo = pivot != null && gizmo.Hover != Handle.None;
        if (ctx.ViewportHovered && overGizmo && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            var kind = io.KeyShift && gizmo.Mode == GizmoMode.Translate
                ? (io.KeyCtrl ? GestureKind.PushPull : GestureKind.Extrude)
                : GestureKind.Transform;
            string label = kind switch
            {
                GestureKind.Extrude => "Extrude",
                GestureKind.PushPull => "Push/Pull",
                _ => gizmo.Mode switch { GizmoMode.Rotate => "Rotate", GizmoMode.Scale => "Scale", _ => "Move" },
            };
            _gesture = TransformGesture.Start(ctx.Session, label, kind);
            if (_gesture != null) gizmo.BeginDrag(gizmo.Hover, ctx.View, _gesture.Pivot, mouse);
            return;
        }

        ctx.Hover = ctx.ViewportHovered && !overGizmo ? ctx.PickAtMouse(Theme.Scale) : null;

        if (ctx.ViewportHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            _marqueeArmed = true;
            _marquee = false;
            _pressPos = mouse;
        }
        if (!_marqueeArmed) return;
        if (Vector2.Distance(mouse, _pressPos) > Theme.Px(4)) _marquee = true;
        if (ImGui.IsMouseDown(ImGuiMouseButton.Left)) return;

        var picked = _marquee
            ? Picker.Marquee(ctx.Doc, ctx.View, _pressPos, mouse, ctx.Sel.Mode, ctx.Backfaces)
            : ctx.Hover is { } h ? [h] : new List<ElementRef>();
        var sel = ctx.Sel;
        if (!io.KeyShift && !io.KeyCtrl) sel.Clear();
        foreach (var e in picked)
            if (io.KeyCtrl) sel.Toggle(e);
            else sel.Add(e);
        _marqueeArmed = _marquee = false;
    }

    public void Draw(EditorContext ctx, ImDrawListPtr dl, Vector2 origin)
    {
        if (ctx.SelectionPivot() is { } pivot) ctx.Gizmo.Draw(dl, origin, ctx.View, pivot);
        if (!_marquee) return;
        var a = origin + _pressPos;
        var b = origin + ctx.Mouse;
        dl.AddRectFilled(Vector2.Min(a, b), Vector2.Max(a, b), (Theme.AccentU32 & 0x00FFFFFF) | 0x22000000);
        dl.AddRect(Vector2.Min(a, b), Vector2.Max(a, b), Theme.AccentU32);
    }
}
