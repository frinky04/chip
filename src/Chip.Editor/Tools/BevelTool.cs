using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Scene;
using Chip.Editor.Features.CutTools;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;

namespace Chip.Editor.Tools;

/// <summary>
/// Bevel the selected edges (or chamfer the selected vertices): drag in the viewport to set the width, press 1–8 while
/// dragging for segments (1 = flat chamfer, more = rounded), release to apply. The inspector applies exact values.
/// </summary>
public sealed class BevelTool : IViewportTool
{
    public string Id => "bevel";
    public string Name => "Bevel";
    public Icon Icon => Icon.Bevel;
    public string? Hotkey => "B";
    public int Order => 32;

    public const int MaxSegments = 8;

    /// <summary>Last width used (metres); the inspector's value for exact bevels.</summary>
    public float Width { get; set; } = 0.125f;
    public int Segments { get; set; } = 1;

    bool _dragging, _previewCut;
    Vector2 _press;
    float _unitsPerPixel, _shownWidth = -1;
    int _shownSegments;
    Dictionary<int, PolyMesh> _originals = new();
    List<(int Mesh, List<Edge> Edges, List<int> Verts)> _targets = new();

    public bool Busy => _dragging;
    public bool HasOptions => true;

    public (string Key, string Action)[] Hints => _dragging
        ? [("Move", "width"), ("1–8", "segments"), ("Release", "apply"), ("Esc", "cancel")]
        : [("Drag", "bevel the selected edges or vertices"), ("Esc", "exit tool")];

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

    /// <summary>Bevels the current selection with exact values as one undo step. Returns the number of new faces.</summary>
    public int Apply(EditorContext ctx, float width, int segments)
    {
        if (!HasTargets(ctx)) { ctx.Status("Select edges or vertices to bevel", error: true); return 0; }
        var targets = Collect(ctx);
        int added = 0;
        ctx.Session.Begin("Bevel");
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

    static string Describe(List<(int Mesh, List<Edge> Edges, List<int> Verts)> targets)
    {
        int e = targets.Sum(t => t.Edges.Count), v = targets.Sum(t => t.Verts.Count);
        return e > 0 ? (e == 1 ? "1 edge" : $"{e} edges") : (v == 1 ? "1 vertex" : $"{v} vertices");
    }

    public bool Cancel(EditorContext ctx)
    {
        if (!_dragging) return false;
        _dragging = false;
        ctx.Session.Cancel();
        return true;
    }

    public void Update(EditorContext ctx)
    {
        ctx.Hover = null;
        if (!_dragging)
        {
            if (!ctx.ViewportHovered || !ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return;
            if (!HasTargets(ctx)) { ctx.Status("Select edges or vertices first, then drag to bevel them", error: true); return; }
            Begin(ctx);
            return;
        }

        for (int k = 1; k <= MaxSegments; k++)
            if (ImGui.IsKeyPressed(ImGuiKey._0 + k, false) || ImGui.IsKeyPressed(ImGuiKey.Keypad0 + k, false)) Segments = k;
        float width = Vector2.Distance(ctx.Mouse, _press) * _unitsPerPixel;
        if (ctx.Snap) width = MeshOps.Snap(width, MathF.Max(ctx.Grid / 8, 1 / 128f));
        Preview(ctx, width);

        if (ImGui.IsMouseDown(ImGuiMouseButton.Left)) return;
        _dragging = false;
        if (_shownWidth <= 0 || !_previewCut) { ctx.Session.Cancel(); return; }
        Width = _shownWidth;
        ctx.Session.End();
        ctx.Status($"Bevelled {Describe(_targets)}, {Width:0.###} m, {Segments} segment{(Segments == 1 ? "" : "s")}");
        if (ctx.DefaultTool != null) ctx.SetTool(ctx.DefaultTool);
    }

    void Begin(EditorContext ctx)
    {
        _targets = Collect(ctx);
        _originals = _targets.ToDictionary(t => t.Mesh, t => ctx.Doc.Find(t.Mesh)!.Clone());
        var pivot = ctx.SelectionPivot() ?? Vector3.Zero;
        _unitsPerPixel = ctx.Camera.UnitsPerPixel(MathF.Max(Vector3.Distance(pivot, ctx.View.Eye), 0.1f), ctx.ViewportSize.Y);
        _press = ctx.Mouse;
        _shownWidth = -1;
        _previewCut = false;
        ctx.Session.Begin("Bevel");
        ctx.Sel.Clear(); // indices go stale as soon as the preview runs; Esc restores the selection with the snapshot
        _dragging = true;
    }

    /// <summary>Re-runs the bevel from the original meshes when the width or segment count changes.</summary>
    void Preview(EditorContext ctx, float width)
    {
        if (width == _shownWidth && Segments == _shownSegments) return;
        foreach (var (id, original) in _originals)
        {
            if (ctx.Doc.Find(id) is not { } m) continue;
            var copy = original.Clone();
            m.Positions = copy.Positions;
            m.Faces = copy.Faces;
            m.SoftEdges = copy.SoftEdges;
        }
        _previewCut = width > 0 && Run(ctx, _targets, width, Segments) > 0;
        _shownWidth = width;
        _shownSegments = Segments;
        ctx.Session.Touch();
    }

    public void Draw(EditorContext ctx, ImDrawListPtr dl, Vector2 origin)
    {
        if (!_dragging) return;
        var a = origin + _press;
        var b = origin + ctx.Mouse;
        dl.AddLine(a, b, CutDraw.Shadow, Theme.Px(3.5f));
        dl.AddLine(a, b, (Theme.AccentU32 & 0x00FFFFFF) | 0xA0000000, Theme.Px(1.5f));
        dl.AddCircleFilled(a, Theme.Px(3), Theme.AccentU32);
        string text = _shownWidth > 0
            ? $"{_shownWidth:0.###} m, {(Segments == 1 ? "chamfer" : $"{Segments} segments")}"
            : "Drag away to set the width";
        var ts = ImGui.CalcTextSize(text);
        var pad = Theme.Px(6, 3);
        var pos = b + new Vector2(Theme.Px(14), -ts.Y - Theme.Px(10));
        dl.AddRectFilled(pos - pad, pos + ts + pad, 0xE0141416, Theme.Px(2));
        dl.AddText(pos, Theme.AccentU32, text);
    }

    public void DrawOptions(EditorContext ctx)
    {
        ImGui.TextColored(Theme.TextDim, "Width");
        Widgets.Tip("Distance from the edge on each side, in metres");
        float w = Width;
        if (ImGui.InputFloat("##width", ref w, 0, 0, "%.3f m")) Width = Math.Clamp(w, 0.001f, 100f);
        ImGui.TextColored(Theme.TextDim, "Segments");
        Widgets.Tip("1 is a flat chamfer; more give a rounded edge. Press 1–8 while dragging.");
        int s = Segments;
        if (ImGui.SliderInt("##segments", ref s, 1, MaxSegments)) Segments = s;
        ImGui.Dummy(Theme.Px(0, 2));
        bool can = HasTargets(ctx);
        ImGui.BeginDisabled(!can);
        string label = ctx.Sel.Mode == SelectMode.Vertex ? "Chamfer selected vertices" : "Bevel selected edges";
        if (ImGui.Button(label, new Vector2(-1, 0))) Apply(ctx, Width, Segments);
        ImGui.EndDisabled();
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
        ImGui.TextWrapped(can
            ? (ctx.Sel.Mode == SelectMode.Vertex ? "Vertices are chamfered flat." : "Or drag in the viewport to set the width.")
            : "Select edges or vertices to bevel.");
        ImGui.PopStyleColor();
    }
}
