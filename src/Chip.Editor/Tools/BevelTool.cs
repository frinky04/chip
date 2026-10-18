using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Scene;
using Chip.Editor.Features.CutTools;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;

namespace Chip.Editor.Tools;

/// <summary>
/// Bevel the selected edges (or chamfer the selected vertices), Source 2 style: the bevel is live from the moment the
/// tool opens (at the last width and segments) and stays adjustable until Enter. Drag to change the width (pull away
/// from the edges to widen; snaps to the grid, Ctrl for free), the wheel or 1–9 for segments, [ ] for the grid,
/// Enter to apply, Esc to cancel. Switching tools applies too. The inspector edits the same live values.
/// </summary>
public sealed class BevelTool : IViewportTool
{
    public string Id => "bevel";
    public string Name => "Bevel";
    public Icon Icon => Icon.Bevel;
    public string? Hotkey => "B";
    public int Order => 32;

    public const int MaxSegments = 16;

    /// <summary>Width in metres: the live value while a bevel is pending, and the starting value for the next one.</summary>
    public float Width { get; set; } = 0.125f;
    public int Segments { get; set; } = 1;

    bool _pending, _dragging, _previewCut;
    Vector3 _pivot;
    Vector2 _pivotScreen;
    float _pressWidth, _pressDistance, _unitsPerPixel, _wheel;
    float _shownWidth = -1;
    int _shownSegments;
    Dictionary<int, PolyMesh> _originals = new();
    List<(int Mesh, List<Edge> Edges, List<int> Verts)> _targets = new();

    /// <summary>A bevel is showing and waiting for Enter (it is an open undo step).</summary>
    public bool Pending => _pending;
    public bool Busy => _pending;
    public bool CapturesWheel => _pending;
    public bool HasOptions => true;

    public (string Key, string Action)[] Hints => _pending
        ? [("Drag", "width"), ("Ctrl", "free width"), ("Wheel / 1–9", "segments"), ("[ ]", "grid"), ("Enter", "apply"), ("Esc", "cancel")]
        : [("Select", "edges or vertices, then drag to bevel"), ("Esc", "exit tool")];

    static bool HasTargets(EditorContext ctx) => ctx.Sel.Mode is SelectMode.Edge or SelectMode.Vertex && !ctx.Sel.IsEmpty;

    static List<(int Mesh, List<Edge> Edges, List<int> Verts)> Collect(EditorContext ctx) =>
        ctx.Sel.Items.GroupBy(i => i.Mesh)
            .Where(g => ctx.Doc.Find(g.Key) is { Hidden: false, Locked: false })
            .Select(g => ctx.Sel.Mode == SelectMode.Edge
                ? (g.Key, g.Select(i => i.AsEdge).ToList(), new List<int>())
                : (g.Key, new List<Edge>(), g.Select(i => i.A).ToList()))
            .ToList();

    /// <summary>Bevels the targets into the document's meshes. Returns the number of new faces.</summary>
    static int Run(EditorContext ctx, List<(int Mesh, List<Edge> Edges, List<int> Verts)> targets, float width, int segments)
    {
        int added = 0;
        foreach (var (id, edges, verts) in targets)
        {
            if (ctx.Doc.Find(id) is not { } m) continue;
            added += edges.Count > 0 ? MeshOps.BevelEdges(m, edges, width, segments).Count : MeshOps.BevelVertices(m, verts, width).Count;
        }
        return added;
    }

    static string Describe(List<(int Mesh, List<Edge> Edges, List<int> Verts)> targets)
    {
        int e = targets.Sum(t => t.Edges.Count), v = targets.Sum(t => t.Verts.Count);
        return e > 0 ? (e == 1 ? "1 edge" : $"{e} edges") : (v == 1 ? "1 vertex" : $"{v} vertices");
    }

    /// <summary>Bevels the current selection with exact values as one undo step, no preview (menu command). Returns the new faces.</summary>
    public int Apply(EditorContext ctx, float width, int segments)
    {
        if (_pending) { Width = width; Segments = segments; Refresh(ctx); Commit(ctx); return 1; }
        if (!HasTargets(ctx)) { ctx.Status("Select edges or vertices to bevel", error: true); return 0; }
        var targets = Collect(ctx);
        int added = 0;
        ctx.Session.Begin("Bevel", ctx.Sel.MeshIds());
        try { added = Run(ctx, targets, width, segments); }
        catch { ctx.Session.Cancel(); throw; }
        if (added == 0)
        {
            ctx.Session.Cancel();
            ctx.Status("Nothing to bevel: only edges between two faces, away from open borders, can be bevelled", error: true);
            return 0;
        }
        ctx.Sel.Clear(); // vertex indices changed
        ctx.Session.End();
        ctx.Status($"Bevelled {Describe(targets)}");
        return added;
    }

    public void Activate(EditorContext ctx)
    {
        if (HasTargets(ctx)) Begin(ctx);
        else ctx.Status("Select edges or vertices, then drag in the viewport to bevel them");
    }

    /// <summary>Opens the live bevel on the current selection at <see cref="Width"/>/<see cref="Segments"/>. False if nothing bevels.</summary>
    public bool Begin(EditorContext ctx)
    {
        if (_pending) return true;
        if (!HasTargets(ctx)) { ctx.Status("Select edges or vertices to bevel", error: true); return false; }
        _targets = Collect(ctx);
        _originals = _targets.ToDictionary(t => t.Mesh, t => ctx.Doc.Find(t.Mesh)!.Clone());
        _pivot = ctx.SelectionPivot() ?? Vector3.Zero;
        _shownWidth = -1;
        _previewCut = false;
        ctx.Session.Begin("Bevel", ctx.Sel.MeshIds());
        ctx.Sel.Clear(); // indices go stale as soon as the preview runs; Esc restores the selection with the snapshot
        _pending = true;
        Refresh(ctx);
        if (!_previewCut && Width > 0)
        {
            Cancel(ctx);
            ctx.Status("Nothing to bevel: only edges between two faces, away from open borders, can be bevelled", error: true);
            return false;
        }
        return true;
    }

    /// <summary>Applies the pending bevel as one undo step.</summary>
    public void Commit(EditorContext ctx)
    {
        if (!_pending) return;
        _pending = _dragging = false;
        if (_shownWidth <= 0 || !_previewCut) { ctx.Session.Cancel(); ctx.Status("Bevel cancelled: the width was zero"); return; }
        ctx.Session.End();
        ctx.Status($"Bevelled {Describe(_targets)}, {Labels.Length(Width)}, {SegmentText(Segments)}");
    }

    public bool Cancel(EditorContext ctx)
    {
        if (!_pending) return false;
        _pending = _dragging = false;
        ctx.Session.Cancel();
        return true;
    }

    /// <summary>Switching tools keeps what you set up.</summary>
    public void Deactivate(EditorContext ctx) => Commit(ctx);

    public void Update(EditorContext ctx)
    {
        ctx.Hover = null;
        var io = ImGui.GetIO();
        if (!_pending)
        {
            if (!ctx.ViewportHovered || !ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return;
            if (!Begin(ctx)) return;
        }

        // Segments: number keys, or the wheel (accumulated so smooth-scrolling touchpads step cleanly).
        for (int k = 1; k <= 9; k++)
            if (ImGui.IsKeyPressed(ImGuiKey._0 + k, false) || ImGui.IsKeyPressed(ImGuiKey.Keypad0 + k, false)) Segments = k;
        _wheel += ctx.ToolWheel;
        while (MathF.Abs(_wheel) >= 1)
        {
            Segments = Math.Clamp(Segments + Math.Sign(_wheel), 1, MaxSegments);
            _wheel -= Math.Sign(_wheel);
        }
        bool plain = !io.KeyCtrl && !io.KeyShift && !io.KeyAlt;
        if (plain && ImGui.IsKeyPressed(ImGuiKey.LeftBracket, false)) ctx.StepGrid(-1);
        if (plain && ImGui.IsKeyPressed(ImGuiKey.RightBracket, false)) ctx.StepGrid(1);

        // Width: pulling away from the edges widens, toward them narrows, relative to where the drag started, so a
        // new drag never jumps. Snaps to the grid unless snapping is off or Ctrl is held.
        if (!_dragging && ctx.ViewportHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) StartDrag(ctx);
        if (_dragging)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                Width = SnapWidth(ctx, _pressWidth + (Vector2.Distance(ctx.Mouse, _pivotScreen) - _pressDistance) * _unitsPerPixel, io.KeyCtrl);
            else _dragging = false;
        }
        Refresh(ctx);

        if (ImGui.IsKeyPressed(ImGuiKey.Enter, false) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false))
        {
            Commit(ctx);
            if (ctx.DefaultTool != null) ctx.SetTool(ctx.DefaultTool);
        }
    }

    /// <summary>Grid-snapped width (never negative). Snapping follows the global toggle; Ctrl holds it off.</summary>
    public static float SnapWidth(EditorContext ctx, float width, bool free)
    {
        width = MathF.Max(width, 0);
        return ctx.Snap && !free ? MeshOps.Snap(width, ctx.Grid) : width;
    }

    void StartDrag(EditorContext ctx)
    {
        _dragging = true;
        _pressWidth = Width;
        _unitsPerPixel = ctx.Camera.UnitsPerPixel(MathF.Max(Vector3.Distance(_pivot, ctx.View.Eye), 0.1f), ctx.ViewportSize.Y);
        // Measure from the edges' screen position; if it's off screen, from just beside the press point.
        _pivotScreen = ctx.View.Project(_pivot, out var s) ? s : ctx.Mouse - new Vector2(1, 0);
        _pressDistance = Vector2.Distance(ctx.Mouse, _pivotScreen);
    }

    /// <summary>Re-runs the bevel from the original meshes when the width or segment count changed.</summary>
    public void Refresh(EditorContext ctx)
    {
        if (!_pending || (Width == _shownWidth && Segments == _shownSegments)) return;
        foreach (var (id, original) in _originals)
        {
            if (ctx.Doc.Find(id) is not { } m) continue;
            var copy = original.Clone();
            m.Positions = copy.Positions;
            m.Faces = copy.Faces;
            m.SoftEdges = copy.SoftEdges;
        }
        _previewCut = Width > 0 && Run(ctx, _targets, Width, Segments) > 0;
        _shownWidth = Width;
        _shownSegments = Segments;
        ctx.Session.Touch();
    }

    static string SegmentText(int segments) => segments == 1 ? "chamfer" : $"{segments} segments";

    public void Draw(EditorContext ctx, ImDrawListPtr dl, Vector2 origin)
    {
        if (!_pending) return;
        var b = origin + ctx.Mouse;
        if (_dragging)
        {
            var a = origin + _pivotScreen;
            dl.AddLine(a, b, CutDraw.Shadow, Theme.Px(3.5f));
            dl.AddLine(a, b, (Theme.AccentU32 & 0x00FFFFFF) | 0xA0000000, Theme.Px(1.5f));
            dl.AddCircleFilled(a, Theme.Px(3), Theme.AccentU32);
        }
        bool free = ImGui.GetIO().KeyCtrl || !ctx.Snap;
        string text = Width > 0
            ? $"{Labels.Length(Width)}{(free ? "" : $" (grid {Labels.Length(ctx.Grid)})")}, {SegmentText(Segments)}. Enter applies"
            : "Drag away from the edges to set the width";
        var ts = ImGui.CalcTextSize(text);
        var pad = Theme.Px(6, 3);
        var pos = b + new Vector2(Theme.Px(14), -ts.Y - Theme.Px(10));
        dl.AddRectFilled(pos - pad, pos + ts + pad, 0xE0141416, Theme.Px(2));
        dl.AddText(pos, Theme.AccentU32, text);
    }

    public void DrawOptions(EditorContext ctx)
    {
        ImGui.TextColored(Theme.TextDim, "Width");
        Widgets.Tip("Distance from the edge on each side, in metres. Dragging snaps it to the grid; hold Ctrl for free.");
        float w = Width;
        if (ImGui.InputFloat("##width", ref w, 0, 0, "%.3f m")) Width = Math.Clamp(w, 0.001f, 100f);
        ImGui.TextColored(Theme.TextDim, "Segments");
        Widgets.Tip("1 is a flat chamfer; more give a rounded edge. Mouse wheel or 1–9 while the bevel is showing.");
        int s = Segments;
        if (ImGui.SliderInt("##segments", ref s, 1, MaxSegments)) Segments = s;
        if (_pending) Refresh(ctx); // inspector edits show immediately
        ImGui.Dummy(Theme.Px(0, 2));

        if (_pending)
        {
            if (ImGui.Button("Apply", new Vector2(ImGui.GetContentRegionAvail().X * 0.5f - Theme.Px(2), 0)))
            {
                Commit(ctx);
                if (ctx.DefaultTool != null) ctx.SetTool(ctx.DefaultTool);
            }
            Widgets.Tip("Enter");
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(-1, 0))) Cancel(ctx);
            Widgets.Tip("Esc");
            return;
        }
        bool can = HasTargets(ctx);
        ImGui.BeginDisabled(!can);
        string label = ctx.Sel.Mode == SelectMode.Vertex ? "Chamfer selected vertices" : "Bevel selected edges";
        if (ImGui.Button(label, new Vector2(-1, 0))) Begin(ctx);
        ImGui.EndDisabled();
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
        ImGui.TextWrapped(can
            ? (ctx.Sel.Mode == SelectMode.Vertex ? "Vertices are chamfered flat." : "Or drag in the viewport.")
            : "Select edges or vertices to bevel.");
        ImGui.PopStyleColor();
    }
}
