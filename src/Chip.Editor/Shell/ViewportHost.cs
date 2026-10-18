using System.Numerics;
using Chip.Core.Picking;
using Chip.Core.Texturing;
using Chip.Editor.Features.View;
using Chip.Editor.Rendering;
using Chip.Editor.UI;
using Chip.Editor.Viewport;
using ImGuiNET;
using Silk.NET.Input;

namespace Chip.Editor;

/// <summary>
/// Viewport host (stream G): lays out the panes, makes the hovered pane active (its view, mouse and rectangle become the
/// context's, so tools work unchanged in any pane), routes input to mouse bindings, the temporary-pivot drag, navigation
/// and the active tool, then renders every pane and draws the HUD.
/// </summary>
public sealed partial class EditorApp
{
    int _selSignature;
    bool _wasGizmoDragging;
    GizmoMode _dragMode;
    Vector3? _dragStartCenter;
    GridPlane? _construction;

    void DrawViewport(Vector2 pos, Vector2 size, float dt)
    {
        ImGui.SetNextWindowPos(pos);
        ImGui.SetNextWindowSize(size);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.Begin("##viewport", FixedPanel | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        DrawViewportHeader(size.X);

        var layout = ViewportLayout.Of(Ctx);
        var origin = ImGui.GetCursorScreenPos();
        var area = Vector2.Max(ImGui.GetContentRegionAvail(), Vector2.One);
        layout.Arrange(origin, area, layout.Kind == LayoutKind.Single ? 0 : Theme.Px(2));

        // The pane under the mouse becomes active unless something owns the mouse (a drag, flying, a tool mid-operation).
        var mousePos = ImGui.GetIO().MousePos;
        bool windowHovered = ImGui.IsWindowHovered() && !ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId);
        if (ShotMouse() is { } fake) { mousePos = origin + fake * area; windowHovered = true; }
        else if (_captureFbo != 0) windowHovered = false; // captures never depend on where the real cursor happens to be
        if (_bench != null) { mousePos = origin + (_bench.Mouse ?? new Vector2(-1)) * area; windowHovered = _bench.Mouse != null; }
        var hoveredPane = windowHovered ? layout.PaneAt(mousePos) : null;
        bool mouseHeld = ImGui.IsMouseDown(ImGuiMouseButton.Left) || ImGui.IsMouseDown(ImGuiMouseButton.Right) || ImGui.IsMouseDown(ImGuiMouseButton.Middle);
        bool locked = Ctx.Interacting || _nav.Active || Ctx.Gizmo.PivotDragging || mouseHeld;
        if (!locked && hoveredPane != null && hoveredPane != layout.Active) layout.Activate(hoveredPane);
        var active = layout.Active;
        bool overLabel = hoveredPane != null && layout.Kind != LayoutKind.Single && PaneLabelRect(hoveredPane).Contains(mousePos);

        Ctx.ViewportOrigin = active.Origin;
        Ctx.ViewportSize = active.Size;
        Ctx.Mouse = mousePos - active.Origin;
        Ctx.ViewportHovered = hoveredPane == active && !overLabel;
        Ctx.View = active.View;

        SyncGizmo();
        if (_bench != null) _bench.UpdateViewport(Ctx);
        else UpdateViewport(dt);
        _bench?.Mark("input");
        Ctx.View = active.View;
        TrackPivotAfterDrag();
        _construction = ConstructionPlane();

        _renderer.BeginFrame(Ctx.Session, Ctx.Hover, ViewSettings.Of(Ctx).ShowNormals);
        _bench?.Mark("sync");
        foreach (var pane in layout.Visible)
        {
            var settings = new RenderSettings(pane.Mode, Ctx.Grid, ViewSettings.Of(Ctx).BoundaryEdges, _construction);
            uint tex = _renderer.Render(_renderer.Target(pane.Index), pane == active ? Ctx.View : pane.View, pane.Camera, Ctx.Doc, settings);
            ImGui.SetCursorScreenPos(pane.Origin);
            ImGui.Image((nint)tex, pane.Size, new Vector2(0, 1), new Vector2(1, 0));
        }

        _bench?.Mark("render");
        var dl = ImGui.GetWindowDrawList();
        foreach (var pane in layout.Visible) DrawPaneChrome(dl, layout, pane);

        dl.PushClipRect(active.Origin, active.Origin + active.Size, true);
        Ctx.ActiveTool?.Draw(Ctx, dl, active.Origin);
        DrawViewportHud(dl, active.Origin, active.Size);
        dl.PopClipRect();
        ImGui.End();
        ImGui.PopStyleVar(2);
        _bench?.Mark("hud");
    }

    /// <summary>
    /// Mouse bindings first (they carry modifiers), then MMB on the gizmo (temporary pivot), then camera navigation,
    /// then the active tool.
    /// </summary>
    void UpdateViewport(float dt)
    {
        if (Ctx.Gizmo.PivotDragging) { UpdatePivotDrag(); return; }
        // A tool that wants the wheel (bevel segments) gets it before the camera zooms.
        var io = ImGui.GetIO();
        Ctx.ToolWheel = 0;
        if (Ctx.ViewportHovered && Ctx.ActiveTool is { CapturesWheel: true } && io.MouseWheel != 0)
        {
            Ctx.ToolWheel = io.MouseWheel;
            io.MouseWheel = 0;
        }
        bool busy = Ctx.Interacting;
        if (!Ctx.ViewportHovered && !busy && !_nav.Active) { Ctx.Hover = null; return; }
        bool free = Ctx.ViewportHovered && !busy && !_nav.Active;
        if (free && TryMouseBinding()) return;
        if (free && TryBeginPivotDrag()) return;
        if (_nav.Update(Ctx, dt)) { Ctx.Hover = null; return; }
        Ctx.ActiveTool?.Update(Ctx);
    }

    bool TryMouseBinding()
    {
        var io = ImGui.GetIO();
        foreach (var b in Ctx.MouseBindings)
        {
            if (!ImGui.IsMouseClicked(b.Button) || io.KeyCtrl != b.Ctrl || io.KeyShift != b.Shift || io.KeyAlt != b.Alt) continue;
            b.Execute(Ctx);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Headless captures only: <c>CHIP_SHOT_MOUSE=0.5,0.4</c> places the cursor at that fraction of the viewport area,
    /// so hover-driven UI (construction grid, hover highlight, pane activation) can be proven with <c>--shot</c>.
    /// </summary>
    Vector2? ShotMouse()
    {
        if (_captureFbo == 0 || Environment.GetEnvironmentVariable("CHIP_SHOT_MOUSE") is not { } v) return null;
        var parts = v.Split(',');
        return parts.Length == 2 && float.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out float x)
            && float.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out float y) ? new Vector2(x, y) : null;
    }

    // ---- Gizmo: orientation, modifiers, temporary pivot ----

    /// <summary>Pushes modifier state and the orientation frame into the gizmo, and drops the temporary pivot on a new selection.</summary>
    void SyncGizmo()
    {
        var gizmo = Ctx.Gizmo;
        var io = ImGui.GetIO();
        gizmo.CtrlHeld = io.KeyCtrl;
        gizmo.ShiftHeld = io.KeyShift;
        gizmo.AlignHeld = _input.Keyboards.Count > 0 && _input.Keyboards[0].IsKeyPressed(Key.CapsLock);
        var (x, y, z) = GizmoFrame.For(gizmo.Orientation, Ctx.Doc, Ctx.Sel, Ctx.Camera);
        gizmo.SetBasis(x, y, z);

        int sig = SelectionSignature();
        if (sig != _selSignature)
        {
            _selSignature = sig;
            if (!gizmo.PivotDragging) Ctx.PivotOverride = null;
        }
    }

    int SelectionSignature()
    {
        var sel = Ctx.Sel;
        int h = 0;
        foreach (var e in sel.Items) h ^= e.GetHashCode() * 16777619;
        return HashCode.Combine(sel.Mode, sel.Count, h);
    }

    bool TryBeginPivotDrag()
    {
        var io = ImGui.GetIO();
        if (!ImGui.IsMouseClicked(ImGuiMouseButton.Middle) || io.KeyCtrl || io.KeyShift || io.KeyAlt) return false;
        if (Ctx.ActiveTool != Ctx.DefaultTool || Ctx.SelectionPivot() is not { } pivot) return false;
        var h = Ctx.Gizmo.PivotHitTest(Ctx.View, pivot, Ctx.Mouse);
        if (h == Handle.None) return false;
        Ctx.Gizmo.BeginPivotDrag(h, Ctx.View, pivot, Ctx.Mouse);
        return true;
    }

    void UpdatePivotDrag()
    {
        var gizmo = Ctx.Gizmo;
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Middle) || Ctx.Sel.IsEmpty)
        {
            gizmo.EndPivotDrag();
            if (Ctx.PivotOverride != null) Ctx.Status("Temporary pivot set. It resets when the selection changes");
            return;
        }
        Ctx.PivotOverride = gizmo.PivotDragPosition(Ctx.View, Ctx.Mouse, Ctx.Grid, Ctx.Snap);
    }

    /// <summary>A temporary pivot travels with the selection when it is moved (rotate and scale keep it fixed by definition).</summary>
    void TrackPivotAfterDrag()
    {
        var gizmo = Ctx.Gizmo;
        if (gizmo.Dragging && !_wasGizmoDragging)
        {
            _dragMode = gizmo.Mode;
            _dragStartCenter = BoundsCenter();
        }
        else if (!gizmo.Dragging && _wasGizmoDragging && _dragMode == GizmoMode.Translate
                 && Ctx.PivotOverride is { } p && _dragStartCenter is { } start && BoundsCenter() is { } end)
        {
            Ctx.PivotOverride = p + (end - start);
        }
        _wasGizmoDragging = gizmo.Dragging;
    }

    Vector3? BoundsCenter() => Ctx.SelectionBounds() is { } b ? (b.Min + b.Max) * 0.5f : null;

    /// <summary>
    /// While a creation tool is active, the grid follows the hovered face: its dominant-axis plane through the snapped hit
    /// (the same plane the block tool builds on). Frozen while the tool is mid-operation.
    /// </summary>
    GridPlane? ConstructionPlane()
    {
        if (Ctx.ActiveTool is not { } tool || !ViewSettings.Of(Ctx).ConstructionTools.Contains(tool.Id)) return null;
        if (tool.Busy) return _construction;
        if (!Ctx.ViewportHovered || Ctx.RaycastFace() is not { } hit) return null;
        var n = TextureMapping.DominantAxis(hit.Normal);
        float depth = Vector3.Dot(hit.Point, n);
        if (Ctx.Snap) depth = MathF.Round(depth / Ctx.Grid) * Ctx.Grid;
        return new GridPlane(n * depth, n, hit.Point);
    }

    // ---- Chrome and HUD ----

    /// <summary>The clickable view name in a pane's corner (multi-pane layouts only).</summary>
    static (Vector2 Min, Vector2 Max) PaneLabelBox(ViewportPane pane)
    {
        string text = PaneLabelText(pane);
        var size = ImGui.CalcTextSize(text) + Theme.Px(16, 6) + new Vector2(Theme.Px(12), 0);
        var min = pane.Origin + Theme.Px(6, 6);
        return (min, min + size);
    }

    static Rect PaneLabelRect(ViewportPane pane)
    {
        var (min, max) = PaneLabelBox(pane);
        return new Rect(min, max);
    }

    readonly record struct Rect(Vector2 Min, Vector2 Max)
    {
        public bool Contains(Vector2 p) => p.X >= Min.X && p.Y >= Min.Y && p.X < Max.X && p.Y < Max.Y;
    }

    static string PaneLabelText(ViewportPane pane) =>
        pane.Mode == ViewMode.Lit ? pane.Name : $"{pane.Name}  {ViewportPane.ModeInline(pane.Mode)}";

    void DrawPaneChrome(ImDrawListPtr dl, ViewportLayout layout, ViewportPane pane)
    {
        dl.PushClipRect(pane.Origin, pane.Origin + pane.Size, true);
        DrawAxisIndicator(dl, pane.Camera, pane.Origin + new Vector2(Theme.Px(40), pane.Size.Y - Theme.Px(40)));
        if (layout.Kind != LayoutKind.Single)
        {
            bool isActive = pane == layout.Active;
            var (min, max) = PaneLabelBox(pane);
            ImGui.SetCursorScreenPos(min);
            bool clicked = ImGui.InvisibleButton($"##panelabel{pane.Index}", max - min);
            bool hovered = ImGui.IsItemHovered();
            var fill = hovered ? Theme.FrameHover : Theme.Panel with { W = 0.85f };
            dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(fill), Theme.Px(3));
            var text = PaneLabelText(pane);
            var col = ImGui.ColorConvertFloat4ToU32(isActive || hovered ? Theme.Text : Theme.TextDim);
            dl.AddText(min + Theme.Px(8, 3), col, text);
            Chevron(dl, new Vector2(max.X - Theme.Px(11), (min.Y + max.Y) * 0.5f), col);
            if (clicked)
            {
                layout.Activate(pane);
                ImGui.OpenPopup($"##panemenu{pane.Index}");
            }
            if (ImGui.BeginPopup($"##panemenu{pane.Index}"))
            {
                ViewMenuItems(layout, pane);
                ImGui.EndPopup();
            }
            if (isActive)
                dl.AddRect(pane.Origin, pane.Origin + pane.Size, ImGui.ColorConvertFloat4ToU32(Theme.TextDim with { W = 0.45f }), 0, ImDrawFlags.None, Theme.Px(1));
        }
        dl.PopClipRect();
    }

    void DrawViewportHud(ImDrawListPtr dl, Vector2 origin, Vector2 size)
    {
        var p = origin + Theme.Px(12, 10);
        if (ViewportLayout.Of(Ctx).Kind != LayoutKind.Single) p.Y += Theme.Px(30);
        uint dim = ImGui.ColorConvertFloat4ToU32(Theme.TextDim);
        if (_nav.Flying) dl.AddText(p, Theme.AccentU32, $"Flying at {Ctx.Camera.FlySpeed:0.#} m/s, scroll to change speed");
        else if (Ctx.Gizmo.PivotDragging) dl.AddText(p, ImGui.ColorConvertFloat4ToU32(Theme.Text), "Moving the pivot. Hold CapsLock to snap to vertices, edges and faces");
        else if (Ctx.Gizmo.Dragging && Ctx.Gizmo.AlignHeld && Ctx.Gizmo.Mode == GizmoMode.Translate)
            dl.AddText(p, ImGui.ColorConvertFloat4ToU32(Theme.Text), "Aligning the pivot to the vertex, edge or face under the cursor");

        if (Ctx.Doc.Meshes.Count == 0 && Ctx.ActiveTool == Ctx.DefaultTool)
        {
            var center = origin + size * 0.5f;
            const string line1 = "Press Shift+B to draw your first block";
            const string line2 = "Drag a base on the grid, release, then move up to set its height";
            var s1 = ImGui.CalcTextSize(line1);
            var s2 = ImGui.CalcTextSize(line2);
            float lh = ImGui.GetTextLineHeight();
            var half = new Vector2(MathF.Max(s1.X, s2.X) * 0.5f + Theme.Px(20), 0);
            dl.AddRectFilled(center - half - new Vector2(0, lh * 2.2f), center + half + new Vector2(0, lh * 2f), ImGui.ColorConvertFloat4ToU32(Theme.Panel with { W = 0.92f }), Theme.Px(3));
            dl.AddText(Fonts.Strong, Fonts.BodySize, center - new Vector2(s1.X * 0.5f, lh * 1.4f), ImGui.ColorConvertFloat4ToU32(Theme.Text), line1);
            dl.AddText(center - new Vector2(s2.X * 0.5f, -lh * 0.3f), dim, line2);
        }
    }

    /// <summary>World-axis orientation in the corner: farther axes drawn first and dimmed; axes pointing at the viewer hidden.</summary>
    static void DrawAxisIndicator(ImDrawListPtr dl, Camera cam, Vector2 center)
    {
        float len = Theme.Px(24);
        Span<(int Index, Vector2 Direction, float Depth)> axes = stackalloc (int, Vector2, float)[3];
        for (int i = 0; i < axes.Length; i++)
        {
            var axis = i == 0 ? Vector3.UnitX : i == 1 ? Vector3.UnitY : Vector3.UnitZ;
            axes[i] = (i, new Vector2(Vector3.Dot(axis, cam.Right), -Vector3.Dot(axis, cam.Up)), Vector3.Dot(axis, cam.Forward));
        }
        axes.Sort(static (a, b) =>
        {
            int depth = b.Depth.CompareTo(a.Depth);
            return depth != 0 ? depth : a.Index.CompareTo(b.Index); // preserve axis order at depth ties
        });
        dl.AddCircleFilled(center, len + Theme.Px(12), ImGui.ColorConvertFloat4ToU32(Theme.Bg with { W = 0.45f }));
        foreach (var (i, dir, depth) in axes)
        {
            if (dir.LengthSquared() < 0.01f) continue; // looking straight down this axis (2D views)
            uint col = Theme.AxisU32[i];
            if (depth > 0.2f) col = (col & 0x00FFFFFF) | 0x80000000; // pointing away from the viewer
            var tip = center + dir * len;
            dl.AddLine(center, tip, col, Theme.Px(2));
            string name = i == 0 ? "X" : i == 1 ? "Y" : "Z";
            var ts = ImGui.CalcTextSize(name);
            dl.AddText(center + dir * (len + Theme.Px(8)) - ts * 0.5f, col, name);
        }
    }

    static void Chevron(ImDrawListPtr dl, Vector2 c, uint col)
    {
        float w = Theme.Px(4), h = Theme.Px(2.5f);
        dl.AddTriangleFilled(c + new Vector2(-w, -h), c + new Vector2(w, -h), c + new Vector2(0, h + Theme.Px(1)), col);
    }
}
