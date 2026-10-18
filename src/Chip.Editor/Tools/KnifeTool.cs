using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Picking;
using Chip.Core.Scene;
using Chip.Editor.Features.CutTools;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;

namespace Chip.Editor.Tools;

/// <summary>
/// Knife: click points (snapped to vertices, edges and the grid) to draw a line across faces, then Enter splits every
/// face the line crosses, as seen from the camera; an end that stops inside a face carries on in its direction to that
/// face's border. Cuts the selected faces if there are any,
/// otherwise the meshes under the line.
/// </summary>
public sealed class KnifeTool : IViewportTool
{
    public string Id => "knife";
    public string Name => "Knife";
    public Icon Icon => Icon.Knife;
    public string? Hotkey => "C";
    public int Order => 31;

    readonly List<Vector3> _points = new();
    CutPoint? _cursor;
    readonly List<(Vector3 A, Vector3 B)> _preview = new();
    (int Count, Vector3 Last, Vector3 Eye, int Version) _previewKey;

    public bool Busy => _points.Count > 0;
    public bool HasOptions => true;

    public (string Key, string Action)[] Hints => _points.Count switch
    {
        0 => [("Click", "start a cut line"), ("Esc", "exit tool")],
        1 => [("Click", "add a point"), ("Backspace", "remove last"), ("Esc", "cancel")],
        _ => [("Click", "add a point"), ("Enter", "cut"), ("Backspace", "remove last"), ("Esc", "cancel")],
    };

    public bool Cancel(EditorContext ctx)
    {
        bool wasBusy = Busy;
        _points.Clear();
        _preview.Clear();
        _cursor = null;
        return wasBusy;
    }

    public void Update(EditorContext ctx)
    {
        ctx.Hover = null;
        _cursor = ctx.ViewportHovered ? CutSnap.At(ctx, ctx.Mouse) : null;
        if (ctx.ViewportHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && _cursor is { } c
            && (_points.Count == 0 || Vector3.Distance(_points[^1], c.Position) > 1e-4f))
            _points.Add(c.Position);
        if (Busy)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Backspace)) _points.RemoveAt(_points.Count - 1);
            if (ImGui.IsKeyPressed(ImGuiKey.Enter, false) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false))
            {
                if (_points.Count < 2) ctx.Status("Click at least two points across the faces you want to cut", error: true);
                else if (Apply(ctx, _points, ctx.View.Eye) > 0) _points.Clear();
            }
        }
        UpdatePreview(ctx);
    }

    /// <summary>
    /// Cuts along <paramref name="path"/> as seen from <paramref name="eye"/>, as one undo step. Selected faces are cut
    /// through (back faces too); otherwise only front faces of the meshes under the line. Returns the number of new faces.
    /// </summary>
    public int Apply(EditorContext ctx, IReadOnlyList<Vector3> path, Vector3 eye)
    {
        var targets = Targets(ctx, path, eye);
        // Dry run on copies so a line that misses everything leaves no empty undo step.
        int pieces = targets.Sum(t => MeshOps.Knife(t.Mesh.Clone(), path, eye, t.Faces, t.FrontOnly).NewFaces.Count);
        if (pieces == 0)
        {
            ctx.Status("The line doesn't cross any face. Draw it over the faces you want to cut.", error: true);
            return 0;
        }
        ctx.Session.Do("Knife", () =>
        {
            // The knife only appends vertices and faces, so face and vertex references stay valid. Split edges don't,
            // so edge mode selects the new cut instead.
            var sel = ctx.Sel;
            if (sel.Mode == SelectMode.Edge) sel.Clear();
            foreach (var t in targets)
            {
                var r = MeshOps.Knife(t.Mesh, path, eye, t.Faces, t.FrontOnly);
                if (sel.Mode == SelectMode.Face && t.Faces != null)
                    foreach (var f in r.NewFaces) sel.Add(ElementRef.Face(t.Mesh.Id, f));
                else if (sel.Mode == SelectMode.Edge)
                    foreach (var e in r.CutEdges) sel.Add(ElementRef.EdgeOf(t.Mesh.Id, e));
            }
        });
        ctx.Status(pieces == 1 ? "Cut 1 face in two" : $"Cut faces into {pieces} new pieces");
        return pieces;
    }

    /// <summary>Selected faces, else the visible meshes under the line (sampled along it from the eye).</summary>
    static List<(PolyMesh Mesh, List<int>? Faces, bool FrontOnly)> Targets(EditorContext ctx, IReadOnlyList<Vector3> path, Vector3 eye)
    {
        if (ctx.Sel.Mode == SelectMode.Face && !ctx.Sel.IsEmpty)
            return ctx.Sel.FaceSets(ctx.Doc).Where(kv => !kv.Key.Hidden && !kv.Key.Locked).Select(kv => (kv.Key, (List<int>?)kv.Value, false)).ToList();
        var meshes = new HashSet<PolyMesh>();
        for (int i = 0; i + 1 < path.Count; i++)
            for (int s = 0; s <= 24; s++)
            {
                var p = Vector3.Lerp(path[i], path[i + 1], s / 24f);
                var dir = p - eye;
                if (dir.LengthSquared() < 1e-12f) continue;
                if (ctx.Picks.RaycastFace(new Ray(eye, Vector3.Normalize(dir)), ctx.Backfaces, selectable: true) is { } hit) meshes.Add(hit.Mesh);
            }
        return meshes.Select(m => (m, (List<int>?)null, !ctx.Backfaces)).ToList();
    }

    /// <summary>Runs the cut on copies whenever the line, the cursor or the camera changes, to show where edges will go.</summary>
    void UpdatePreview(EditorContext ctx)
    {
        var path = new List<Vector3>(_points);
        if (_cursor is { } c && _points.Count > 0) path.Add(c.Position);
        var key = (path.Count, path.Count > 0 ? path[^1] : default, ctx.View.Eye, ctx.Session.Version);
        if (key == _previewKey) return;
        _previewKey = key;
        _preview.Clear();
        if (path.Count < 2) return;
        foreach (var t in Targets(ctx, path, ctx.View.Eye))
        {
            var copy = t.Mesh.Clone();
            var r = MeshOps.Knife(copy, path, ctx.View.Eye, t.Faces, t.FrontOnly);
            _preview.AddRange(r.CutEdges.Select(e => (copy.Positions[e.A], copy.Positions[e.B])));
        }
    }

    public void Draw(EditorContext ctx, ImDrawListPtr dl, Vector2 origin)
    {
        var v = ctx.View;
        uint dim = (Theme.AccentU32 & 0x00FFFFFF) | 0x90000000;
        for (int i = 0; i + 1 < _points.Count; i++) CutDraw.Line(dl, origin, v, _points[i], _points[i + 1], dim, Theme.Px(1.5f));
        if (_points.Count > 0 && _cursor is { } c) CutDraw.Line(dl, origin, v, _points[^1], c.Position, dim, Theme.Px(1));
        foreach (var (a, b) in _preview) CutDraw.Stroke(dl, origin, v, a, b, Theme.AccentU32, Theme.Px(2));
        foreach (var p in _points) CutDraw.Dot(dl, origin, v, p, Theme.AccentU32, Theme.Px(3.5f));
        if (_cursor is { } cur) CutDraw.Marker(dl, origin, v, cur);
    }

    public void DrawOptions(EditorContext ctx)
    {
        bool faces = ctx.Sel.Mode == SelectMode.Face && !ctx.Sel.IsEmpty;
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
        ImGui.TextWrapped(faces
            ? (ctx.Sel.Count == 1 ? "Cuts the selected face." : $"Cuts the {ctx.Sel.Count} selected faces, front and back.")
            : "Cuts the faces you see under the line.");
        ImGui.TextWrapped("Points snap to vertices, then edges, then the grid.");
        ImGui.PopStyleColor();
        if (_points.Count > 0) ImGui.Text(_points.Count == 1 ? "1 point" : $"{_points.Count} points");
    }
}
