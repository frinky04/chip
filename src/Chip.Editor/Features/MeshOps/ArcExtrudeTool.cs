using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Picking;
using Chip.Core.Scene;
using Chip.Editor.Features.CutTools;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;
using Ops = Chip.Core.Geometry.MeshOps;

namespace Chip.Editor.Features.MeshEditing;

/// <summary>
/// Arc extrude (Shift+A): sweeps the selected faces or open edges round an axis in equal steps, for corridors that turn
/// a corner, pipes, coves and arches. The sweep is live from the moment the tool opens, at the last angle and segments.
/// First the axis: point to the side to bend toward and slide the axis out along it, like dragging a gizmo arrow (onto
/// grid lines, held to the four square directions; Ctrl for free); it never goes inside the selection. Click to keep it.
/// Then the angle: point where the arc should end (rotation snap steps; Ctrl for free); click or Enter applies. A guide
/// circle with snap marks shows the path, and sizes read as the inner radius. The wheel or 1–9 sets
/// segments, Esc cancels, switching tools applies. The inspector edits the same values. One undo step.
/// </summary>
public sealed class ArcExtrudeTool : IViewportTool
{
    public const int MaxSegments = 64;

    public float AngleDegrees { get; set; } = 90;
    public int Segments { get; set; } = 8;
    /// <summary>Distance from the selection's centre to the axis, in metres.</summary>
    public float Radius { get; set; } = 1;
    float _lastInner = 1;
    public bool Smooth { get; set; } = true;

    /// <summary>Sweeps the other way out of the selection (Tab). Resets for each new selection.</summary>
    public bool Reverse
    {
        get => _reversed;
        set
        {
            if (value == _reversed) return;
            _reversed = value;
            _travel = -_travel;
        }
    }
    bool _reversed;

    enum Stage { Off, Axis, Angle }
    Stage _stage;
    bool _leaving;
    bool _free;
    SelectMode _mode;
    List<(int Mesh, List<int> Faces, List<Edge> Edges)> _targets = new();
    readonly List<Vector3> _points = new();
    Dictionary<int, PolyMesh> _originals = new();
    Vector3 _center, _travel, _bend, _u, _v;
    float _extent, _rawAngle, _prevScreenAngle, _wheel;
    Vector2 _lastMouse;
    int _made;
    (float Angle, int Segments, float Radius, Vector3 Bend, bool Smooth, bool Reverse)? _shown;

    public string Id => "arcExtrude";
    public string Name => "Arc extrude";
    public Icon Icon => Icon.Rotate;
    public string? Hotkey => null;
    public int Order => 102;
    public bool Busy => _stage != Stage.Off;
    public bool CapturesWheel => Busy;
    public bool HasOptions => true;

    /// <summary>Which way across the selection the sweep curls (a unit vector perpendicular to its travel).</summary>
    public Vector3 BendDirection
    {
        get => _bend;
        set
        {
            var d = Unit(value - _travel * Vector3.Dot(value, _travel));
            if (d != Vector3.Zero) _bend = d;
        }
    }

    static Vector3 Unit(Vector3 v) => v.LengthSquared() < 1e-16f ? Vector3.Zero : Vector3.Normalize(v);

    public ArcSweep Sweep => ArcSweep.Bend(_center, _travel, _bend, Radius, AngleDegrees * MathF.PI / 180, Segments);

    public (string Key, string Action)[] Hints => _stage == Stage.Angle
        ? [("Move", "sweep angle"), ("Ctrl", "free"), ("Wheel / 1–9", "segments"), ("Tab", "reverse"), ("Click/Enter", "apply"), ("Esc", "cancel")]
        : [("Move", "place the axis"), ("Ctrl", "free"), ("Click", "set the angle"), ("Wheel / 1–9", "segments"), ("Tab", "reverse"), ("Enter", "apply"), ("Esc", "cancel")];

    public void Activate(EditorContext ctx)
    {
        _mode = ctx.Sel.Mode;
        if (_mode is not (SelectMode.Face or SelectMode.Edge))
        {
            ctx.Status("Select faces or open edges to arc extrude", error: true);
            Leave(ctx);
            return;
        }
        _targets = ctx.Sel.Items.GroupBy(i => i.Mesh)
            .Where(g => ctx.Doc.Find(g.Key) is { Hidden: false, Locked: false })
            .Select(g => _mode == SelectMode.Face
                ? (g.Key, g.Select(i => i.A).Distinct().ToList(), new List<Edge>())
                : (g.Key, new List<int>(), g.Select(i => i.AsEdge).Distinct().ToList()))
            .ToList();
        if (_targets.Count == 0 || !Frame(ctx))
        {
            ctx.Status(_mode == SelectMode.Edge
                ? "Arc extrude needs open (boundary) edges: edges used by two faces can't be extruded"
                : "Select faces or open edges to arc extrude", error: true);
            Leave(ctx);
            return;
        }
        _originals = _targets.ToDictionary(t => t.Mesh, t => ctx.Doc.Find(t.Mesh)!.Clone());
        ctx.Session.Begin("Arc Extrude", _targets.Select(t => t.Mesh));
        ctx.Sel.Clear(); // indices go stale as soon as the preview runs; Esc restores the selection with the snapshot
        _stage = Stage.Axis;
        _shown = null;
        _lastMouse = ctx.Mouse;
        Refresh(ctx);
    }

    /// <summary>
    /// Finds the sweep's frame: the centre of the selection, the way it travels (faces: their normal; open edges: out
    /// of their face, along it) and two square directions across that.
    /// </summary>
    bool Frame(EditorContext ctx)
    {
        _points.Clear();
        _reversed = false;
        var travel = Vector3.Zero;
        var fallback = Vector3.Zero;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var (id, faces, edges) in _targets)
        {
            var m = ctx.Doc.Find(id)!;
            void Grow(int v) { min = Vector3.Min(min, m.Positions[v]); max = Vector3.Max(max, m.Positions[v]); _points.Add(m.Positions[v]); }
            if (faces.Count > 0)
            {
                travel += AwayFromMesh(m, faces);
                foreach (var f in faces) foreach (var v in m.Faces[f].Verts) Grow(v);
                continue;
            }
            var edgeFaces = Topology.EdgeFaces(m);
            foreach (var e in edges)
            {
                if (!edgeFaces.TryGetValue(e, out var fs) || fs.Count != 1) continue;
                var n = m.FaceNormal(fs[0]);
                var verts = m.Faces[fs[0]].Verts;
                int i = Array.IndexOf(verts, e.A);
                var (a, b) = verts[(i + 1) % verts.Length] == e.B ? (e.A, e.B) : (e.B, e.A);
                travel += Vector3.Cross(m.Positions[b] - m.Positions[a], n);
                fallback += n;
                Grow(e.A);
                Grow(e.B);
            }
        }
        if (min.X > max.X) return false;
        // A closed loop of open edges (a lone quad's whole border) has no single way out; sweep it off its faces instead.
        if (travel.LengthSquared() < 1e-8f) travel = fallback;
        _travel = Unit(travel);
        if (_travel == Vector3.Zero) return false;
        _center = (min + max) * 0.5f;
        _extent = MathF.Max(Vector3.Distance(min, max), 0.5f);

        // The square directions across the travel come from the two world axes least along it.
        var axes = new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ }.OrderBy(a => MathF.Abs(Vector3.Dot(a, _travel))).ToArray();
        _u = Unit(axes[0] - _travel * Vector3.Dot(axes[0], _travel));
        _v = Vector3.Normalize(Vector3.Cross(_travel, _u));
        _bend = _u;
        InnerRadius = _lastInner; // the gap carries over between selections of different widths
        return true;
    }

    /// <summary>
    /// The faces' normal, turned round when the faces they are attached to lie in front of it. A corridor's end cap that
    /// was built facing back into the corridor still sweeps on out of the end, the way a Shift+drag extrude would be
    /// pulled. A loose patch keeps its normal (<see cref="Reverse"/> flips it).
    /// </summary>
    public static Vector3 AwayFromMesh(PolyMesh m, IReadOnlyCollection<int> faces)
    {
        var n = Ops.RegionNormal(m, faces);
        var region = faces.ToHashSet();
        float side = 0;
        foreach (var (e, fs) in Topology.EdgeFaces(m))
        {
            if (fs.Count(region.Contains) != 1) continue; // only the region's border
            var mid = (m.Positions[e.A] + m.Positions[e.B]) * 0.5f;
            foreach (var f in fs)
                if (!region.Contains(f)) side += Vector3.Dot(m.FaceCenter(f) - mid, n);
        }
        return side > 1e-5f ? -n : n;
    }

    /// <summary>
    /// How far the selection reaches from its centre toward <paramref name="dir"/>. An axis closer than that would swing
    /// the far side backward through the mesh, so the axis never goes inside; at exactly this distance that edge folds.
    /// </summary>
    float Reach(Vector3 dir) => _points.Count == 0 ? 0 : _points.Max(p => Vector3.Dot(p - _center, dir));

    public bool Cancel(EditorContext ctx)
    {
        if (_stage == Stage.Off) return false;
        _stage = Stage.Off;
        ctx.Session.Cancel();
        Leave(ctx);
        return true;
    }

    /// <summary>Switching tools keeps what you set up.</summary>
    public void Deactivate(EditorContext ctx)
    {
        _leaving = true; // the switch is already happening
        Commit(ctx);
        _leaving = false;
    }

    /// <summary>Applies the previewed sweep as one undo step.</summary>
    public void Commit(EditorContext ctx)
    {
        if (_stage == Stage.Off) return;
        Refresh(ctx);
        _stage = Stage.Off;
        if (_made == 0)
        {
            ctx.Session.Cancel();
            ctx.Status("Nothing to arc extrude", error: true);
        }
        else
        {
            ctx.Session.End();
            _lastInner = InnerRadius;
            ctx.Status($"Arc extruded {_mode.Count(_targets.Sum(t => t.Faces.Count + t.Edges.Count))}, {AngleDegrees:0.#}°, " +
                       $"{Segments} segments, inner radius {Labels.Length(InnerRadius)}");
        }
        Leave(ctx);
    }

    public void Update(EditorContext ctx)
    {
        if (_stage == Stage.Off) return;
        ctx.Hover = null;
        var io = ImGui.GetIO();
        bool free = io.KeyCtrl || !ctx.Snap;

        for (int k = 1; k <= 9; k++)
            if (ImGui.IsKeyPressed(ImGuiKey._0 + k, false) || ImGui.IsKeyPressed(ImGuiKey.Keypad0 + k, false)) Segments = k;
        if (ImGui.IsKeyPressed(ImGuiKey.Tab, false)) Reverse = !Reverse;
        _wheel += ctx.ToolWheel;
        while (MathF.Abs(_wheel) >= 1)
        {
            Segments = Math.Clamp(Segments + Math.Sign(_wheel), 1, MaxSegments);
            _wheel -= Math.Sign(_wheel);
        }

        // Only motion over the viewport counts, so reaching for the inspector doesn't change the values.
        bool moved = ctx.ViewportHovered && (ctx.Mouse != _lastMouse || free != _free);
        _free = free;
        _lastMouse = ctx.Mouse;
        if (moved && _stage == Stage.Axis) PlaceAxis(ctx, free);
        if (moved && _stage == Stage.Angle) SweepAngle(ctx, free);

        bool click = ctx.ViewportHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        if (click && _stage == Stage.Axis)
        {
            _stage = Stage.Angle;
            _rawAngle = AngleDegrees * MathF.PI / 180;
            _prevScreenAngle = ScreenAngle(ctx);
        }
        else if (click || ImGui.IsKeyPressed(ImGuiKey.Enter, false) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false))
        {
            Commit(ctx);
            return;
        }
        Refresh(ctx);
    }

    /// <summary>
    /// The bend side is the square direction (any direction with Ctrl) that points at the cursor on screen, and the axis
    /// slides along it to the spot nearest the cursor's ray, like dragging a gizmo arrow. Both stay steady at any camera
    /// angle, unlike a hit on the selection's own plane, which is edge-on when you look along a corridor. Snapped, the
    /// axis lands on a grid line (on the grid step from the centre when the side isn't a world axis).
    /// </summary>
    void PlaceAxis(EditorContext ctx, bool free)
    {
        var view = ctx.View;
        if (!view.Project(_center, out var c)) return;
        var toMouse = ctx.Mouse - c;
        if (toMouse.LengthSquared() < 4) return;
        var sides = free
            ? Enumerable.Range(0, 360).Select(i => _u * MathF.Cos(i * MathF.PI / 180) + _v * MathF.Sin(i * MathF.PI / 180))
            : [_u, -_u, _v, -_v];
        Vector3? best = null;
        float bestScore = float.MinValue;
        foreach (var d in sides)
        {
            if (!view.Project(_center + d * _extent, out var tip) || Vector2.Distance(tip, c) < 8) continue; // points at the camera
            float score = Vector2.Dot(Vector2.Normalize(tip - c), Vector2.Normalize(toMouse));
            if (score > bestScore) { bestScore = score; best = d; }
        }
        if (best is not { } dir) return;
        _bend = dir;
        float t = RayLineParam(view.ScreenRay(ctx.Mouse), _center, dir);
        if (!free)
        {
            bool worldAxis = MathF.Abs(MathF.Abs(dir.X) + MathF.Abs(dir.Y) + MathF.Abs(dir.Z) - 1) < 1e-4f;
            float start = worldAxis ? Vector3.Dot(_center, dir) : 0;
            t = Ops.Snap(start + t, ctx.Grid) - start;
        }
        Radius = MathF.Max(t, Reach(dir));
    }

    /// <summary>Where along the line through <paramref name="origin"/> the ray passes closest (the gizmo's axis-drag maths).</summary>
    static float RayLineParam(Ray ray, Vector3 origin, Vector3 dir)
    {
        var w0 = origin - ray.Origin;
        float b = Vector3.Dot(dir, ray.Dir);
        float denom = 1 - b * b;
        if (denom < 1e-6f) return 0;
        return (b * Vector3.Dot(ray.Dir, w0) - Vector3.Dot(dir, w0)) / denom;
    }

    /// <summary>
    /// The arc ends where you point: the cursor's spot on the plane the sweep turns in, measured from the selection's
    /// centre round the axis, carried on past 180° rather than jumping. When that plane is edge-on, the cursor turning
    /// round the axis on screen drives it instead (the rotate gizmo's way).
    /// </summary>
    void SweepAngle(EditorContext ctx, bool free)
    {
        var sweep = Sweep;
        var ray = ctx.View.ScreenRay(ctx.Mouse);
        float screen = ScreenAngle(ctx);
        float step = screen - _prevScreenAngle;
        if (step > MathF.PI) step -= MathF.Tau;
        if (step < -MathF.PI) step += MathF.Tau;
        _prevScreenAngle = screen;
        if (MathF.Abs(Vector3.Dot(ray.Dir, sweep.AxisDir)) > 0.2f && Picker.RayPlane(ray, sweep.AxisPoint, sweep.AxisDir, out float hitT))
        {
            var from = _center - sweep.AxisPoint;
            var to = ray.At(hitT) - sweep.AxisPoint;
            float a = MathF.Atan2(Vector3.Dot(Vector3.Cross(from, to), sweep.AxisDir), Vector3.Dot(from, to));
            _rawAngle = a + MathF.Tau * MathF.Round((_rawAngle - a) / MathF.Tau); // the turn nearest the last one
        }
        else
        {
            // Screen y points down, so the sign follows the gizmo's: which way the axis faces the camera.
            float facing = Vector3.Dot(sweep.AxisDir, ctx.View.Eye - sweep.AxisPoint) >= 0 ? 1 : -1;
            _rawAngle -= step * facing;
        }
        _rawAngle = Math.Clamp(_rawAngle, 0, MathF.Tau);
        float deg = _rawAngle * 180 / MathF.PI;
        float snapStep = ctx.Gizmo.RotateSnapDegrees;
        if (!free && snapStep > 0) deg = Ops.Snap(deg, snapStep);
        AngleDegrees = Math.Clamp(deg, 1, 360);
    }

    float ScreenAngle(EditorContext ctx)
    {
        if (!ctx.View.Project(Sweep.AxisPoint, out var c)) return _prevScreenAngle;
        return MathF.Atan2(ctx.Mouse.Y - c.Y, ctx.Mouse.X - c.X);
    }

    /// <summary>Re-runs the sweep from the original meshes when a value changed, and selects what it made.</summary>
    public void Refresh(EditorContext ctx)
    {
        if (_stage == Stage.Off) return;
        AngleDegrees = Math.Clamp(AngleDegrees, 1, 360);
        Segments = Math.Clamp(Segments, 1, MaxSegments);
        Radius = MathF.Max(Radius, 0.001f);
        var key = (AngleDegrees, Segments, Radius, _bend, Smooth, _reversed);
        if (_shown == key) return;
        _shown = key;

        var sweep = Sweep;
        var sel = ctx.Sel;
        sel.Clear();
        _made = 0;
        foreach (var (id, faces, edges) in _targets)
        {
            if (ctx.Doc.Find(id) is not { } m || !_originals.TryGetValue(id, out var original)) continue;
            var copy = original.Clone();
            m.Positions = copy.Positions;
            m.Faces = copy.Faces;
            m.SoftEdges = copy.SoftEdges;
            if (faces.Count > 0)
            {
                var r = Ops.ArcExtrudeFaces(m, faces, sweep, Smooth);
                _made += r.SideFaces.Count;
                foreach (var f in faces)
                {
                    int nf = r.Collapse is { } mp ? mp.FaceMap[f] : f;
                    if (nf >= 0) sel.Add(ElementRef.Face(id, nf));
                }
            }
            else
            {
                var (ends, r) = Ops.ArcExtrudeEdges(m, edges, sweep, Smooth);
                _made += r.SideFaces.Count;
                foreach (var e in ends) sel.Add(ElementRef.EdgeOf(id, e));
            }
        }
        ctx.Session.Touch();
    }

    /// <summary>Hands back to the default tool. Guarded because switching tools calls <see cref="Deactivate"/> again.</summary>
    void Leave(EditorContext ctx)
    {
        if (_leaving || ctx.ActiveTool != this || ctx.DefaultTool == null) return;
        _leaving = true;
        ctx.SetTool(ctx.DefaultTool);
        _leaving = false;
    }

    public void Draw(EditorContext ctx, ImDrawListPtr dl, Vector2 origin)
    {
        if (_stage == Stage.Off) return;
        var sweep = Sweep;
        var view = ctx.View;
        var axisPoint = sweep.AxisPoint;
        uint faint = (Theme.AccentU32 & 0x00FFFFFF) | 0x70000000;
        void Stroke(Vector3 a, Vector3 b, uint col, float px)
        {
            CutDraw.Line(dl, origin, view, a, b, CutDraw.Shadow, Theme.Px(px + 2));
            CutDraw.Line(dl, origin, view, a, b, col, Theme.Px(px));
        }
        Vector3 On(float radians) => Vector3.Transform(_center, new ArcSweep(axisPoint, sweep.AxisDir, radians, 1).Step(1));

        // The axis, and the radius out to it from the selection's centre.
        var along = sweep.AxisDir * _extent;
        Stroke(axisPoint - along, axisPoint + along, Theme.AccentU32, 2);
        Stroke(_center, axisPoint, faint, 1.5f);

        // The path the selection's centre takes: the whole turn faint with a mark every snap step (longer every 90°),
        // the part that will be built bright.
        float swept = AngleDegrees * MathF.PI / 180;
        const int Steps = 96;
        for (int i = 0; i < Steps; i++)
        {
            float a0 = MathF.Tau * i / Steps, a1 = MathF.Tau * (i + 1) / Steps;
            CutDraw.Line(dl, origin, view, On(a0), On(a1), faint, Theme.Px(1));
        }
        int arcSteps = Math.Max(2, (int)MathF.Ceiling(Steps * swept / MathF.Tau));
        for (int i = 0; i < arcSteps; i++) Stroke(On(swept * i / arcSteps), On(swept * (i + 1) / arcSteps), Theme.AccentU32, 2);
        float snap = ctx.Gizmo.RotateSnapDegrees > 0 ? ctx.Gizmo.RotateSnapDegrees : 15;
        for (float deg = 0; deg < 360 - 1e-3f; deg += snap)
        {
            var pt = On(deg * MathF.PI / 180);
            var outward = Unit(pt - axisPoint) * Radius * (MathF.Abs(deg % 90) < 1e-3f ? 0.1f : 0.05f);
            CutDraw.Line(dl, origin, view, pt - outward, pt + outward, deg <= AngleDegrees + 1e-3f ? Theme.AccentU32 : faint, Theme.Px(1.5f));
        }
        if (_stage == Stage.Angle) Stroke(axisPoint, On(swept), faint, 1.5f);
        if (view.Project(axisPoint, out var p))
        {
            dl.AddCircleFilled(origin + p, Theme.Px(5), CutDraw.Shadow);
            dl.AddCircleFilled(origin + p, Theme.Px(3), Theme.AccentU32);
        }

        string text = $"{AngleDegrees:0.#}°, {Segments} segments, inner radius {Labels.Length(InnerRadius)}. " +
                      (_stage == Stage.Axis ? "Click to set the angle" : "Click applies");
        var ts = ImGui.CalcTextSize(text);
        var pad = Theme.Px(6, 3);
        var pos = Vector2.Clamp(ctx.Mouse + Theme.Px(16, 12), new Vector2(Theme.Px(6)), ctx.ViewportSize - ts - Theme.Px(14, 10));
        dl.AddRectFilled(origin + pos - pad, origin + pos + ts + pad, 0xE0141416, Theme.Px(2));
        dl.AddText(origin + pos, Theme.AccentU32, text);
    }

    /// <summary>Gap between the axis and the selection's nearest side: the inside wall's radius for a corridor turning a corner.</summary>
    public float InnerRadius
    {
        get => MathF.Max(Radius - Reach(_bend), 0);
        set => Radius = MathF.Max(value, 0) + Reach(_bend);
    }

    public void DrawOptions(EditorContext ctx)
    {
        RowLabel("Angle", "How far the sweep turns round the axis. Moving round the axis in the viewport snaps to the rotation step; hold Ctrl for free.");
        float angle = AngleDegrees;
        if (ValueSlider("##arcAngle", "", ref angle, 1, 360, "%.0f°", a => $"{a:0.#}°")) AngleDegrees = angle;
        RowLabel("Segments", "Steps along the arc. Mouse wheel or 1–9 while the sweep is showing.");
        int s = Segments;
        if (ValueSlider("##arcSegments", "", ref s, 1, MaxSegments)) Segments = s;
        RowLabel("Inner", "Inner radius. Gap between the axis and the selection's side nearest it, in metres: the inside wall's radius when a corridor turns a corner. 0 puts the axis on that side, which folds it into a fan.");
        ImGui.SetNextItemWidth(-1);
        float r = InnerRadius;
        if (ImGui.DragFloat("##arcRadius", ref r, 0.01f, 0, 1000, "%.3f m")) InnerRadius = r;
        RowLabel("Options");
        int option = ButtonRow("arcOptions",
            new RowButton("Smooth", "Soften the edges between steps so the curve shades round", Icon: Icon.Rotate, On: Smooth),
            new RowButton("Reverse", "Sweep the other way out of the selection  (Tab)", Icon: Icon.FlipU, On: Reverse));
        if (option == 0) Smooth = !Smooth;
        if (option == 1) Reverse = !Reverse;
        if (_stage == Stage.Off) return;
        Refresh(ctx); // inspector edits show immediately
        int act = ApplyCancel();
        if (act == 0) { Commit(ctx); return; }
        if (act == 1) Cancel(ctx);
    }
}
