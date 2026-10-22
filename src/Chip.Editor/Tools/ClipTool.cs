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
/// Hammer's clipping tool: click (or drag) two points to draw a line; the clip plane runs through that line,
/// perpendicular to the surface it was drawn on (or along the view). Tab cycles keep front / back / both, Enter clips
/// the selected faces in face mode (as Scythe does), else the selected meshes, or every visible mesh the plane crosses
/// when nothing is selected. Cuts are capped.
/// </summary>
public sealed class ClipTool : IViewportTool
{
    public string Id => "clip";
    public string Name => "Clip";
    public Icon Icon => Icon.Clip;
    public string? Hotkey => "Shift+X";
    public int Order => 30;

    enum Stage { Idle, Second, Ready }

    Stage _stage;
    Vector3 _a, _b, _surfaceNormal;
    Vector2 _pressMouse;
    CutPoint? _cursor;

    public ClipKeep Keep { get; set; } = ClipKeep.Front;
    /// <summary>Plane contains the view direction instead of the clicked surface's normal.</summary>
    public bool AlongView { get; set; }
    /// <summary>Caps use the active material instead of the cut face's.</summary>
    public bool CapWithActive { get; set; }

    public bool Busy => _stage != Stage.Idle;
    public bool HasOptions => true;

    public (string Key, string Action)[] Hints => _stage switch
    {
        Stage.Idle => [("Click", "start the cut line"), ("Drag", "draw it in one go"), ("Esc", "exit tool")],
        Stage.Second => [("Click", "end the cut line"), ("Esc", "cancel")],
        _ => [("Enter", "clip"), ("Tab", $"keep {KeepName(NextKeep(Keep))}"), ("Click", "redraw the line"), ("Esc", "cancel")],
    };

    public bool Cancel(EditorContext ctx)
    {
        bool wasBusy = Busy;
        _stage = Stage.Idle;
        _cursor = null;
        return wasBusy;
    }

    public void Update(EditorContext ctx)
    {
        ctx.Hover = null;
        _cursor = ctx.ViewportHovered ? CutSnap.At(ctx, ctx.Mouse) : null;
        bool click = ctx.ViewportHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left);

        switch (_stage)
        {
            case Stage.Idle:
            case Stage.Ready when click:
                if (click && _cursor is { } start)
                {
                    _a = _b = start.Position;
                    _surfaceNormal = start.Normal;
                    _pressMouse = ctx.Mouse;
                    _stage = Stage.Second;
                    return;
                }
                break;
            case Stage.Second:
                if (_cursor is { } c) _b = Constrain(c.Position);
                bool dragged = ImGui.IsMouseReleased(ImGuiMouseButton.Left) && Vector2.Distance(ctx.Mouse, _pressMouse) > Theme.Px(4);
                if ((dragged || click) && Vector3.Distance(_a, _b) > 1e-4f) _stage = Stage.Ready;
                break;
        }

        if (_stage == Stage.Ready)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Tab, false)) Keep = NextKeep(Keep);
            if ((ImGui.IsKeyPressed(ImGuiKey.Enter, false) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false)) && Plane(ctx.View.Eye) is { } pl)
                if (Apply(ctx, pl.Point, pl.Normal) > 0) _stage = Stage.Idle;
        }
    }

    /// <summary>In surface mode the line stays on the plane of the first click.</summary>
    Vector3 Constrain(Vector3 p) => AlongView ? p : p - _surfaceNormal * Vector3.Dot(p - _a, _surfaceNormal);

    (Vector3 Point, Vector3 Normal)? Plane(Vector3 eye)
    {
        var line = _b - _a;
        var across = AlongView ? (_a + _b) * 0.5f - eye : _surfaceNormal;
        var n = Vector3.Cross(line, across);
        return n.LengthSquared() < 1e-12f ? null : (_a, Vector3.Normalize(n));
    }

    /// <summary>
    /// Clips the target meshes with a plane as one undo step. Meshes the plane doesn't cross are left alone.
    /// Returns how many meshes were cut.
    /// </summary>
    public int Apply(EditorContext ctx, Vector3 point, Vector3 normal)
    {
        string? cap = CapWithActive ? ctx.ActiveMaterial : null;
        if (OnFaces(ctx)) return ApplyToFaces(ctx, point, normal, cap);
        var cuts = new List<(PolyMesh Mesh, PolyMesh Front, PolyMesh Back)>();
        foreach (var m in CutSnap.TargetMeshes(ctx))
            if (MeshOps.Split(m, point, normal, cap) is ({ } f, { } b)) cuts.Add((m, f, b));
        if (cuts.Count == 0)
        {
            ctx.Status(ctx.Sel.IsEmpty ? "The clip plane doesn't cross any mesh. Draw the line across one."
                : "The clip plane doesn't cross the selected meshes. Draw the line across them or clear the selection.", error: true);
            return 0;
        }

        ctx.Session.Do("Clip", () =>
        {
            var pieces = new List<int>();
            foreach (var (m, front, back) in cuts)
            {
                Assign(m, Keep == ClipKeep.Back ? back : front);
                pieces.Add(m.Id);
                if (Keep == ClipKeep.Both)
                {
                    back.Name = m.Name;
                    pieces.Add(ctx.Doc.Add(back).Id);
                }
            }
            // Face, edge and vertex indices changed; objects are the only references that survive.
            bool objects = ctx.Sel.Mode == SelectMode.Object && !ctx.Sel.IsEmpty;
            ctx.Sel.Clear();
            if (objects) foreach (var id in pieces) ctx.Sel.Add(ElementRef.Object(id));
        });
        ctx.Status(cuts.Count == 1 ? "Clipped 1 mesh" : $"Clipped {cuts.Count} meshes");
        return cuts.Count;
    }

    /// <summary>Face mode with faces selected: Scythe clips just those faces, not their whole meshes.</summary>
    static bool OnFaces(EditorContext ctx) => ctx.Sel.Mode == SelectMode.Face && !ctx.Sel.IsEmpty;

    /// <summary>What the plane acts on: the selected faces in face mode (Faces set), else whole meshes (Faces null).</summary>
    static List<(PolyMesh Mesh, List<int>? Faces)> Targets(EditorContext ctx) => OnFaces(ctx)
        ? ctx.Sel.FaceSets(ctx.Doc).Where(p => !p.Key.Hidden && !p.Key.Locked).Select(p => (p.Key, (List<int>?)p.Value)).ToList()
        : CutSnap.TargetMeshes(ctx).Select(m => (m, (List<int>?)null)).ToList();

    int ApplyToFaces(EditorContext ctx, Vector3 point, Vector3 normal, string? cap)
    {
        var cuts = new List<(PolyMesh Mesh, PolyMesh Result, List<int> Faces)>();
        foreach (var (m, faces) in Targets(ctx))
        {
            var result = m.Clone();
            if (MeshOps.ClipFaces(result, faces!, point, normal, Keep, cap) is { } added) cuts.Add((m, result, added));
        }
        if (cuts.Count == 0)
        {
            ctx.Status("The clip plane doesn't cross the selected faces. Draw the line across them or clear the selection.", error: true);
            return 0;
        }

        ctx.Session.Do("Clip", cuts.Select(c => c.Mesh.Id), () =>
        {
            ctx.Sel.Clear();
            foreach (var (m, result, added) in cuts)
            {
                Assign(m, result);
                foreach (var f in added) ctx.Sel.Add(ElementRef.Face(m.Id, f));
            }
        });
        ctx.Status(Keep == ClipKeep.Both ? "Split the selected faces in two" : "Clipped the selected faces");
        return cuts.Count;
    }

    static void Assign(PolyMesh target, PolyMesh from)
    {
        target.Positions = from.Positions;
        target.Faces = from.Faces;
        target.SoftEdges = from.SoftEdges;
    }

    static ClipKeep NextKeep(ClipKeep k) => (ClipKeep)(((int)k + 1) % 3);
    static string KeepName(ClipKeep k) => k switch { ClipKeep.Front => "front", ClipKeep.Back => "back", _ => "both" };

    public void Draw(EditorContext ctx, ImDrawListPtr dl, Vector2 origin)
    {
        var v = ctx.View;
        if (_stage == Stage.Idle || _stage == Stage.Second)
            if (_cursor is { } c) CutDraw.Marker(dl, origin, v, c);
        if (_stage == Stage.Idle) return;

        if (Plane(v.Eye) is { } pl)
        {
            var targets = Targets(ctx);
            DrawPlane(dl, origin, v, targets, pl.Point, pl.Normal);
            foreach (var (m, faces) in targets) DrawSection(dl, origin, v, m, faces, pl.Point, pl.Normal);
            if (_stage == Stage.Ready) DrawKeepArrows(dl, origin, v, pl.Normal);
        }
        CutDraw.Stroke(dl, origin, v, _a, _b, Theme.AccentU32, Theme.Px(2));
        CutDraw.Dot(dl, origin, v, _a, Theme.AccentU32, Theme.Px(3.5f));
        CutDraw.Dot(dl, origin, v, _b, Theme.AccentU32, Theme.Px(3.5f));
    }

    /// <summary>The plane, trimmed to the targets' bounds, as a translucent sheet.</summary>
    static void DrawPlane(ImDrawListPtr dl, Vector2 origin, ViewInfo v, List<(PolyMesh Mesh, List<int>? Faces)> targets, Vector3 p0, Vector3 n)
    {
        var corners = targets.SelectMany(t => t.Faces == null ? t.Mesh.Positions : t.Faces.SelectMany(f => t.Mesh.Faces[f].Verts).Select(i => t.Mesh.Positions[i])).ToList();
        if (corners.Count == 0) return;
        var min = corners.Aggregate(Vector3.Min) - new Vector3(0.25f);
        var max = corners.Aggregate(Vector3.Max) + new Vector3(0.25f);
        var pts = new List<Vector3>();
        for (int i = 0; i < 8; i++)
            for (int axis = 0; axis < 3; axis++)
            {
                if ((i & (1 << axis)) != 0) continue;
                var a = Corner(min, max, i);
                var b = Corner(min, max, i | (1 << axis));
                float da = Vector3.Dot(a - p0, n), db = Vector3.Dot(b - p0, n);
                if ((da > 0) == (db > 0) || MathF.Abs(da - db) < 1e-9f) continue;
                pts.Add(Vector3.Lerp(a, b, da / (da - db)));
            }
        if (pts.Count < 3) return;
        var c = pts.Aggregate(Vector3.Zero, (s, p) => s + p) / pts.Count;
        var u = Vector3.Normalize(pts[0] - c);
        var w = Vector3.Cross(n, u);
        pts.Sort((a, b) => MathF.Atan2(Vector3.Dot(a - c, w), Vector3.Dot(a - c, u)).CompareTo(MathF.Atan2(Vector3.Dot(b - c, w), Vector3.Dot(b - c, u))));
        var screen = new Vector2[pts.Count];
        for (int i = 0; i < pts.Count; i++)
        {
            if (!v.Project(pts[i], out var s)) return;
            screen[i] = origin + s;
        }
        dl.AddConvexPolyFilled(ref screen[0], screen.Length, (Theme.AccentU32 & 0x00FFFFFF) | 0x26000000);
        for (int i = 0; i < screen.Length; i++)
            dl.AddLine(screen[i], screen[(i + 1) % screen.Length], (Theme.AccentU32 & 0x00FFFFFF) | 0x80000000, Theme.Px(1));
    }

    static Vector3 Corner(Vector3 min, Vector3 max, int i) =>
        new((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);

    /// <summary>Where the plane cuts the mesh surface (or just <paramref name="faces"/>): the outline of the cap.</summary>
    static void DrawSection(ImDrawListPtr dl, Vector2 origin, ViewInfo v, PolyMesh m, List<int>? faces, Vector3 p0, Vector3 n)
    {
        var d = m.Positions.Select(p => Vector3.Dot(p - p0, n)).ToArray();
        if (d.All(x => x > 0) || d.All(x => x < 0)) return;
        var hits = new List<Vector3>();
        foreach (int f in faces ?? Enumerable.Range(0, m.Faces.Count))
        {
            var verts = m.Faces[f].Verts;
            hits.Clear();
            for (int i = 0; i < verts.Length; i++)
            {
                int a = verts[i], b = verts[(i + 1) % verts.Length];
                if ((d[a] > 0) == (d[b] > 0)) continue;
                hits.Add(Vector3.Lerp(m.Positions[a], m.Positions[b], d[a] / (d[a] - d[b])));
            }
            if (hits.Count < 2) continue;
            var dir = Vector3.Cross(n, m.FaceNormal(f));
            hits.Sort((x, y) => Vector3.Dot(x, dir).CompareTo(Vector3.Dot(y, dir)));
            for (int i = 0; i + 1 < hits.Count; i += 2) CutDraw.Stroke(dl, origin, v, hits[i], hits[i + 1], Theme.AccentU32, Theme.Px(2));
        }
    }

    /// <summary>Arrows from the middle of the line toward the side(s) that survive.</summary>
    void DrawKeepArrows(ImDrawListPtr dl, Vector2 origin, ViewInfo v, Vector3 n)
    {
        var mid = (_a + _b) * 0.5f;
        float len = Math.Clamp(Vector3.Distance(_a, _b) * 0.25f, 0.25f, 2f);
        var dirs = Keep switch { ClipKeep.Front => new[] { n }, ClipKeep.Back => [-n], _ => [n, -n] };
        foreach (var dir in dirs)
        {
            var tip = mid + dir * len;
            CutDraw.Stroke(dl, origin, v, mid, tip, Theme.AccentU32, Theme.Px(2));
            var side = Vector3.Normalize(_b - _a) * len * 0.2f;
            CutDraw.Stroke(dl, origin, v, tip, tip - dir * len * 0.3f + side, Theme.AccentU32, Theme.Px(2));
            CutDraw.Stroke(dl, origin, v, tip, tip - dir * len * 0.3f - side, Theme.AccentU32, Theme.Px(2));
        }
        CutDraw.Label(dl, origin, v, mid + dirs[0] * len, Keep == ClipKeep.Both ? "Split in two" : $"Keep {KeepName(Keep)}", Theme.AccentU32);
    }

    public void DrawOptions(EditorContext ctx)
    {
        ImGui.TextColored(Theme.TextDim, "Keep");
        int keep = Widgets.SegmentedControl("keep",
        [
            (Icon.KeepFront, "Front", "Keep the side the arrow points to (Tab cycles)"),
            (Icon.KeepBack, "Back", "Keep the other side (Tab cycles)"),
            (Icon.KeepBoth, "Both", "Split in two: meshes into two meshes, selected faces into two unwelded halves (Tab cycles)"),
        ], (int)Keep);
        if (keep >= 0) Keep = (ClipKeep)keep;

        ImGui.TextColored(Theme.TextDim, "Plane");
        int plane = Widgets.SegmentedControl("plane",
        [
            (Icon.Face, "Surface", "Perpendicular to the surface you draw the line on"),
            (Icon.Eye, "View", "Contains the view direction: cuts along the line as you see it"),
        ], AlongView ? 1 : 0);
        if (plane >= 0) AlongView = plane == 1;

        ImGui.TextColored(Theme.TextDim, "Caps");
        if (Widgets.Toggle("cap", Icon.Face, "Use active material", $"Cap cuts with {ctx.ActiveMaterial} instead of the cut face's material", CapWithActive))
            CapWithActive = !CapWithActive;

        ImGui.Dummy(Theme.Px(0, 2));
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
        ImGui.TextWrapped(ctx.Sel.IsEmpty ? "Clips every visible mesh the plane crosses."
            : OnFaces(ctx) ? "Clips only the selected faces." : "Clips the selected meshes.");
        ImGui.PopStyleColor();
    }
}
