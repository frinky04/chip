using System.Numerics;
using Chip.Core.Picking;
using Chip.Core.Scene;
using Chip.Editor.Features.Dimensions;
using Chip.Editor.Features.SelectionTools;
using Chip.Editor.Features.View;
using Chip.Editor.Tools.Block;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using Chip.Editor.Viewport;
using ImGuiNET;

namespace Chip.Editor.Tools;

/// <summary>
/// The default tool: click / Shift-add / Ctrl-toggle, Scythe-style paint select on drag (everything the cursor sweeps
/// over; Shift adds, Ctrl removes), Ctrl+Alt+drag box select (Unreal's marquee; Shift adds), plus the transform gizmo
/// (mode from <see cref="EditorContext.Gizmo"/>). Shift-dragging any gizmo extrudes faces or open edges first.
/// Double-click floods (connected faces, edge loop, connected vertices; with CapsLock/Mouse4/Mouse5 held, faces on the plane),
/// and Ctrl+Shift+click adds the shortest path from the active element.
/// <para>Active element: a click, double-click or path click makes the clicked element active. A box select keeps the
/// previous active element if it is still selected, otherwise there is none.</para>
/// </summary>
public sealed class SelectTool : IViewportTool
{
    TransformGesture? _gesture;
    /// <summary>An exact value being typed during a gizmo drag, or null while the mouse steers.</summary>
    string? _typed;
    bool _marqueeArmed, _marquee, _painting, _swallowRelease;
    enum PaintOp { Replace, Add, Remove }
    PaintOp _paintOp;
    Vector2 _pressPos, _lastPaintPos;
    ElementRef? _pressHover;
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

    public (string Key, string Action)[] Hints => _typed != null
        ? [("Enter", "apply"), ("Backspace", "edit"), ("Esc", "cancel")]
        : _gesture != null
        ? [("Type", "an exact value"), ("Esc", "cancel")]
        : [("LMB", "select"), ("Drag", "paint select"), ("Ctrl+Alt+drag", "box"), ("Dbl-click", _mode switch
        {
            SelectMode.Edge => "loop", SelectMode.Object => "select", _ => "connected",
        }), ("Ctrl+Shift+LMB", "path"), ("Alt+LMB", "orbit"), ("F1", "shortcuts")];

    public bool Cancel(EditorContext ctx)
    {
        _typed = null;
        if (_gesture != null)
        {
            _gesture.Cancel();
            _gesture = null;
            ctx.Gizmo.EndDrag();
            return true;
        }
        if (_marqueeArmed)
        {
            _marqueeArmed = _marquee = _painting = false;
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

        // An active gizmo drag owns the mouse until release, or until Enter once a value is being typed.
        if (_gesture != null)
        {
            Type(io);
            if (_typed != null)
            {
                TypedDrag(ctx);
                return;
            }
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
            // Shift extrudes with any gizmo: rotating around a temporary pivot (MMB) sweeps an arc, one segment per drag.
            // Push/pull moves along a normal, so it only exists on the move gizmo.
            var kind = !io.KeyShift ? GestureKind.Transform
                : io.KeyCtrl && gizmo.Mode == GizmoMode.Translate ? GestureKind.PushPull
                : GestureKind.Extrude;
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
            _marquee = _painting = false;
            _pressPos = mouse;
            _pressHover = ctx.Hover;
        }
        if (!_marqueeArmed) return;
        // Past a few pixels the press becomes a drag: Ctrl+Alt boxes, anything else paints.
        if (!_marquee && !_painting && Vector2.Distance(mouse, _pressPos) > Theme.Px(4))
        {
            if (io.KeyCtrl && io.KeyAlt) _marquee = true;
            else BeginPaint(ctx, io);
        }
        if (_painting) Paint(ctx, mouse);
        if (ImGui.IsMouseDown(ImGuiMouseButton.Left)) return;

        var sel = ctx.Sel;
        if (_painting) { /* already applied while dragging */ }
        else if (_marquee) Marquee(ctx, io);
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
        _lastClick = _marquee || _painting ? null : ctx.Hover;
        _lastClickTime = ctx.Now;
        _marqueeArmed = _marquee = _painting = false;
    }

    /// <summary>
    /// Typing during a drag: a digit, point or minus starts an exact value (the mouse stops steering, and letting go no
    /// longer ends the drag); Backspace edits it, and emptying it hands control back to the mouse.
    /// </summary>
    void Type(ImGuiIOPtr io)
    {
        for (int i = 0; i < io.InputQueueCharacters.Size; i++)
        {
            char c = (char)io.InputQueueCharacters[i];
            bool starts = char.IsDigit(c) || c is '.' or '-';
            if (_typed == null && !starts) continue;
            if (starts || c is ' ' or ',' or ';' or '%' or '°' or 'c' or 'm' or 'd' or 'e' or 'g' or 'x' or 'C' or 'M' or 'X') _typed = (_typed ?? "") + c;
        }
        if (_typed != null && ImGui.IsKeyPressed(ImGuiKey.Backspace))
            _typed = _typed.Length > 1 ? _typed[..^1] : null;
    }

    /// <summary>Previews the typed value; Enter applies it and ends the drag.</summary>
    void TypedDrag(EditorContext ctx)
    {
        var gizmo = ctx.Gizmo;
        var values = NumericEntry.Parse(_typed!, DragReadout.UnitFor(gizmo.Mode));
        var exact = values != null ? gizmo.Exact(values) : null;
        if (exact is { } e) _gesture!.Apply(e.M, e.Rigid);
        if (!ImGui.IsKeyPressed(ImGuiKey.Enter, false) && !ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false)) return;
        if (exact == null)
        {
            ctx.Status(gizmo.Mode switch
            {
                GizmoMode.Rotate => "Type an angle in degrees, like 90 or -45",
                GizmoMode.Scale => "Type a scale, like 2, 0.5 or 150%",
                _ => values is { Length: 1 } ? "Drag a little first so the move has a direction, or type a value for each axis: 1 0 -2"
                    : "Type a distance, like 1.5, 150cm, or one value per axis: 1 0 -2",
            }, error: true);
            return;
        }
        _gesture!.Commit();
        _gesture = null;
        gizmo.EndDrag();
        _typed = null;
        _swallowRelease = ImGui.IsMouseDown(ImGuiMouseButton.Left); // the button still held from the drag does nothing
    }

    /// <summary>Paint select starts: no modifier replaces the selection, Shift adds to it, Ctrl removes from it.</summary>
    void BeginPaint(EditorContext ctx, ImGuiIOPtr io)
    {
        _painting = true;
        _paintOp = io.KeyCtrl && !io.KeyShift ? PaintOp.Remove : io.KeyShift ? PaintOp.Add : PaintOp.Replace;
        if (_paintOp == PaintOp.Replace) ctx.Sel.Clear();
        _lastPaintPos = _pressPos;
        if (_pressHover is { } first) PaintElement(ctx.Sel, first);
    }

    /// <summary>
    /// Applies everything under the cursor's path since last frame, sampled every few pixels so a quick sweep doesn't
    /// skip the faces it crossed.
    /// </summary>
    void Paint(EditorContext ctx, Vector2 mouse)
    {
        float step = Theme.Px(6);
        float dist = Vector2.Distance(_lastPaintPos, mouse);
        int samples = Math.Clamp((int)MathF.Ceiling(dist / step), 1, 64);
        for (int i = 1; i <= samples; i++)
        {
            var p = Vector2.Lerp(_lastPaintPos, mouse, i / (float)samples);
            if (ctx.Picks.Pick(ctx.View, p, ctx.Sel.Mode, Theme.Scale, ctx.Backfaces) is { } e) PaintElement(ctx.Sel, e);
        }
        _lastPaintPos = mouse;
    }

    void PaintElement(Selection sel, ElementRef e)
    {
        switch (_paintOp)
        {
            case PaintOp.Remove:
                if (sel.Items.Remove(e) && sel.Active == e) sel.Active = null;
                break;
            default:
                if (sel.Items.Add(e) && sel.Active == null) sel.Active = e; // the first painted element leads a fresh selection
                break;
        }
    }

    void Marquee(EditorContext ctx, ImGuiIOPtr io)
    {
        var sel = ctx.Sel;
        var picked = ctx.Picks.Marquee(ctx.View, _pressPos, ctx.Mouse, sel.Mode, ctx.Backfaces);
        if (VisibleOnly) picked = Visibility.FilterVisible(ctx.Doc, ctx.View, picked, sel.Mode, ctx.Backfaces, ctx.Picks);
        var active = sel.Active;
        // Ctrl+Alt+drag replaces the selection; with Shift it adds.
        if (!io.KeyShift) sel.Clear();
        foreach (var e in picked) sel.Items.Add(e);
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
        if (ViewSettings.Of(ctx) is { ShowDimensions: true } view && !ctx.Sel.IsEmpty && ctx.SelectionBounds() is { } bounds)
            DrawDimensions(ctx, dl, origin, view.DimensionMode, WithReferenceModels(ctx, bounds));
        if (ctx.SelectionPivot() is { } pivot) ctx.Gizmo.Draw(dl, origin, ctx.View, pivot);
        if (_gesture != null && ctx.Gizmo.Dragging) DrawReadout(ctx, dl, origin);
        if (!_marquee) return;
        var a = origin + _pressPos;
        var b = origin + ctx.Mouse;
        dl.AddRectFilled(Vector2.Min(a, b), Vector2.Max(a, b), (Theme.AccentU32 & 0x00FFFFFF) | 0x22000000);
        dl.AddRect(Vector2.Min(a, b), Vector2.Max(a, b), Theme.AccentU32);
    }

    /// <summary>
    /// The selection's sizes: edge labels, and the overall size further out where it says something the edges don't
    /// (two stacked wall faces: their combined height). Overall alone still falls back to the edges for one face or an
    /// object, whose overall size is its edges.
    /// </summary>
    /// <summary>Selected reference models are measured by their model's box, not their origin (a door shows its size).</summary>
    static (Vector3 Min, Vector3 Max) WithReferenceModels(EditorContext ctx, (Vector3 Min, Vector3 Max) bounds)
    {
        foreach (var e in ctx.Sel.Entities(ctx.Doc))
            if (ReferenceModels.Get(e).Model != null)
                foreach (var c in EntityOps.BoxCorners(e))
                    bounds = (Vector3.Min(bounds.Min, c), Vector3.Max(bounds.Max, c));
        return bounds;
    }

    static void DrawDimensions(EditorContext ctx, ImDrawListPtr dl, Vector2 origin, DimensionMode mode, (Vector3 Min, Vector3 Max) bounds)
    {
        var edges = Dimensions.ForSelection(ctx.Doc, ctx.Sel, bounds);
        var overall = Dimensions.Overall(ctx.Doc, ctx.Sel);
        var placed = new List<(Vector2 Min, Vector2 Max)>();
        if (mode != DimensionMode.Edges && overall is { } e)
            DimensionDraw.Overall(dl, origin, ctx.View, e, mode == DimensionMode.Overall ? [] : edges, placed);
        if (mode != DimensionMode.Overall || overall == null)
            DimensionDraw.Groups(dl, origin, ctx.View, edges, (bounds.Min + bounds.Max) * 0.5f, placed);
    }

    /// <summary>
    /// How far the drag has gone, beside the gizmo: per-axis distance (or angle, or scale), a faint line back to where a
    /// move started, a mark where a World-space move lands on the grid, and the value being typed.
    /// </summary>
    void DrawReadout(EditorContext ctx, ImDrawListPtr dl, Vector2 origin)
    {
        var g = ctx.Gizmo;
        var v = ctx.View;
        var amount = g.Amount;
        var now = amount.Start + amount.Delta;
        if (g.Mode == GizmoMode.Translate && amount.Delta.LengthSquared() > 1e-10f)
        {
            Overlay.Line(dl, origin, v, amount.Start, now, Overlay.Faint, Theme.Px(1.5f));
            Overlay.Dot(dl, origin, v, amount.Start, hollow: true);
        }
        if (g.SnapMark is { } mark && v.Project(mark, out var m))
        {
            float r = Theme.Px(4);
            var c = origin + m;
            dl.AddQuadFilled(c + new Vector2(0, -r - 1.5f), c + new Vector2(r + 1.5f, 0), c + new Vector2(0, r + 1.5f), c + new Vector2(-r - 1.5f, 0), Overlay.Shadow);
            dl.AddQuadFilled(c + new Vector2(0, -r), c + new Vector2(r, 0), c + new Vector2(0, r), c + new Vector2(-r, 0), Theme.AccentU32);
        }

        uint dim = ImGui.ColorConvertFloat4ToU32(Theme.TextDim), text = ImGui.ColorConvertFloat4ToU32(Theme.Text);
        var runs = DragReadout.Describe(g.Mode, g.Active, g.Basis, amount, _gesture!.Kind, _gesture.StartBounds.Max - _gesture.StartBounds.Min)
            .Select((r, i) => (r.Text, i == 0 ? dim : r.Axis >= 0 ? Theme.AxisU32[r.Axis] : text)).ToList();
        if (_typed != null)
        {
            bool valid = NumericEntry.Parse(_typed, DragReadout.UnitFor(g.Mode)) != null;
            runs = [runs[0], ($"{_typed}|", valid ? Theme.AccentU32 : Theme.AxisU32[0]), ("Enter applies", dim)];
        }
        var at = g.Mode == GizmoMode.Translate ? now : amount.Start;
        DimensionDraw.Runs(dl, origin, v, at, runs, Theme.Px(22, 22));
    }
}
