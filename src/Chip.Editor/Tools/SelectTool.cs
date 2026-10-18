using System.Numerics;
using Chip.Core.Picking;
using Chip.Core.Scene;
using Chip.Editor.Features.SelectionTools;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using Chip.Editor.Viewport;
using ImGuiNET;

namespace Chip.Editor.Tools;

/// <summary>
/// The default tool: click / Shift-add / Ctrl-toggle / box select in the current mode, plus the transform gizmo
/// (mode from <see cref="EditorContext.Gizmo"/>). Shift-dragging the move gizmo in face mode extrudes.
/// Double-click floods (connected faces, edge loop, connected vertices; with CapsLock/Mouse4/Mouse5 held, faces on the plane),
/// and Ctrl+Shift+click adds the shortest path from the active element.
/// <para>Active element: a click, double-click or path click makes the clicked element active. A box select keeps the
/// previous active element if it is still selected, otherwise there is none.</para>
/// </summary>
public sealed class SelectTool : IViewportTool
{
    TransformGesture? _gesture;
    bool _marqueeArmed, _marquee, _swallowRelease;
    Vector2 _pressPos;
    ElementRef? _lastClick;
    double _lastClickTime;
    SelectMode _mode;

    public string Id => "select";
    public string Name => "Select";
    public Icon Icon => Icon.Select;
    public string? Hotkey => null;
    public int Order => 0;
    public bool Busy => _gesture != null || _marqueeArmed;

    /// <summary>Box select ignores elements hidden behind other surfaces (off: vertices and edges select through).</summary>
    public bool VisibleOnly { get; set; }

    public (string Key, string Action)[] Hints => _gesture != null
        ? [("Esc", "cancel")]
        : [("LMB", "select"), ("Drag", "box select"), ("Dbl-click", _mode switch
        {
            SelectMode.Edge => "loop", SelectMode.Object => "select", _ => "connected",
        }), ("Ctrl+Shift+LMB", "path"), ("Alt+LMB", "orbit"), ("F1", "shortcuts")];

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
        _mode = ctx.Sel.Mode;

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

        // The release that ends a double-click must not re-apply a single click.
        if (_swallowRelease)
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) _swallowRelease = false;
            return;
        }

        // Double-click on the element the first click selected floods from it. Checked before the gizmo, which now sits
        // on that selection and would otherwise eat the second click.
        if (ctx.ViewportHovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && _lastClick is { } first &&
            ctx.Now - _lastClickTime <= io.MouseDoubleClickTime * 2 && ctx.PickAtMouse(Theme.Scale) == first)
        {
            Flood(ctx, first, io);
            _lastClick = null;
            _swallowRelease = true;
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
            _lastClick = null;
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

        var sel = ctx.Sel;
        if (_marquee) Marquee(ctx, io);
        else if (io.KeyCtrl && io.KeyShift && ctx.Hover is { } target)
        {
            bool hadStart = sel.Active != null;
            if (!SelectionOps.SelectPath(ctx.Doc, sel, target))
                ctx.Status(hadStart
                    ? "No path: the two elements aren't connected in one mesh, so only the clicked one was added"
                    : "Path select starts at the active element: click a start, then Ctrl+Shift+click the end");
        }
        else
        {
            if (!io.KeyShift && !io.KeyCtrl) sel.Clear();
            if (ctx.Hover is { } h)
            {
                if (io.KeyCtrl) sel.Toggle(h);
                else sel.Add(h);
            }
        }
        _lastClick = _marquee ? null : ctx.Hover;
        _lastClickTime = ctx.Now;
        _marqueeArmed = _marquee = false;
    }

    void Marquee(EditorContext ctx, ImGuiIOPtr io)
    {
        var sel = ctx.Sel;
        var picked = ctx.Picks.Marquee(ctx.View, _pressPos, ctx.Mouse, sel.Mode, ctx.Backfaces);
        if (VisibleOnly) picked = Visibility.FilterVisible(ctx.Doc, ctx.View, picked, sel.Mode, ctx.Backfaces, ctx.Picks);
        var active = sel.Active;
        if (!io.KeyShift && !io.KeyCtrl) sel.Clear();
        foreach (var e in picked)
            if (!io.KeyCtrl) sel.Items.Add(e);
            else if (!sel.Items.Remove(e)) sel.Items.Add(e); // Ctrl toggles
        sel.Active = active is { } a && sel.Items.Contains(a) ? a : null;
    }

    /// <summary>No modifier replaces the selection with the flood, Shift adds it, Ctrl removes it.</summary>
    static void Flood(EditorContext ctx, ElementRef clicked, ImGuiIOPtr io)
    {
        if (io.KeyCtrl && io.KeyShift) return;
        var sel = ctx.Sel;
        bool coplanar = sel.Mode == SelectMode.Face && HeldKeys.PlaneModifier();
        var flood = SelectionOps.Flood(ctx.Doc, clicked, sel.Mode, coplanar);
        if (io.KeyCtrl)
        {
            sel.Items.ExceptWith(flood);
            if (sel.Active is { } a && !sel.Items.Contains(a)) sel.Active = null;
            return;
        }
        if (!io.KeyShift) sel.Clear();
        sel.Items.UnionWith(flood);
        sel.Active = clicked;
        if (coplanar) ctx.Status($"Selected {sel.Mode.Count(flood.Count)} on the plane");
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
