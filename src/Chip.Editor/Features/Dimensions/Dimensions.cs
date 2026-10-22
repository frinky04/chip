using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Picking;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;

namespace Chip.Editor.Features.Dimensions;

/// <summary>
/// One measurable edge. Edges in the same <see cref="Group"/> are parallel and equally long (the two widths of a quad,
/// the four uprights of a box), so only one of them is labelled. <see cref="Axis"/> is the world axis the edge runs
/// along (0, 1, 2), or -1.
/// </summary>
public readonly record struct Dim(Vector3 A, Vector3 B, int Axis, int Group)
{
    public float Length => Vector3.Distance(A, B);
}

/// <summary>
/// A box (or a flat rectangle, when <see cref="W"/> is zero) from <see cref="Corner"/> along three edge vectors: the
/// selection's overall size.
/// </summary>
public readonly record struct Extent(Vector3 Corner, Vector3 U, Vector3 V, Vector3 W)
{
    public IEnumerable<Vector3> Sides => new[] { U, V, W }.Where(s => s.LengthSquared() > 1e-10f);
}

/// <summary>What the selection labels show: each edge, the selection's overall size, or both (overall further out).</summary>
public enum DimensionMode { Both, Edges, Overall }

/// <summary>
/// Which edges of the selection to measure (Scythe shows sizes beside the edges). Faces: the outline of each selected
/// region, plus creases inside it, so a single wall shows its width and height and a whole box its three sizes, but a
/// floor split into many coplanar faces shows only its outline. Edges: each selected edge. Two vertices: the distance
/// between them. Objects: the selection's bounding box.
/// </summary>
public static class Dimensions
{
    /// <summary>Selections bigger than this (faces or edges) get no labels: they'd be noise and cost a frame.</summary>
    public const int MaxElements = 4000;

    public static List<Dim> ForSelection(Document doc, Selection sel, (Vector3 Min, Vector3 Max)? objectBounds)
    {
        var dims = new List<Dim>();
        var groups = new Dictionary<(int, int, int, int), int>();
        void Add(Vector3 a, Vector3 b)
        {
            var d = b - a;
            float len = d.Length();
            if (len < 1e-5f) return;
            var dir = d / len;
            // Parallel either way round: flip so the largest component is positive.
            int big = MathF.Abs(dir.X) >= MathF.Abs(dir.Y) && MathF.Abs(dir.X) >= MathF.Abs(dir.Z) ? 0 : MathF.Abs(dir.Y) >= MathF.Abs(dir.Z) ? 1 : 2;
            if (dir[big] < 0) dir = -dir;
            var key = ((int)MathF.Round(dir.X * 1000), (int)MathF.Round(dir.Y * 1000), (int)MathF.Round(dir.Z * 1000), (int)MathF.Round(len * 10000));
            if (!groups.TryGetValue(key, out int g)) groups[key] = g = groups.Count;
            dims.Add(new Dim(a, b, MathF.Abs(dir[big]) > 0.99999f ? big : -1, g));
        }

        if (sel.Count == 0 || sel.Count > MaxElements) return dims;
        switch (sel.Mode)
        {
            case SelectMode.Object:
                if (objectBounds is { } box) foreach (var (a, b) in BoxEdges(box.Min, box.Max)) Add(a, b);
                break;
            case SelectMode.Vertex:
                if (sel.Count != 2) break;
                var ends = sel.Items.Select(i => doc.Find(i.Mesh) is { } m && i.A < m.Positions.Count ? m.Positions[i.A] : (Vector3?)null).ToList();
                if (ends[0] is { } p && ends[1] is { } q) Add(p, q);
                break;
            case SelectMode.Edge:
                foreach (var i in sel.Items)
                    if (doc.Find(i.Mesh) is { } m && i.B < m.Positions.Count) Add(m.Positions[i.A], m.Positions[i.B]);
                break;
            case SelectMode.Face:
                foreach (var g in sel.Items.GroupBy(i => i.Mesh))
                    if (doc.Find(g.Key) is { } m) FaceOutline(m, g.Select(i => i.A).Where(f => f < m.Faces.Count).ToHashSet(), Add);
                break;
        }
        return dims;
    }

    /// <summary>Edges of the face set used by one selected face (its outline), or by two that meet at a crease.</summary>
    static void FaceOutline(PolyMesh m, HashSet<int> faces, Action<Vector3, Vector3> add)
    {
        var uses = new Dictionary<Edge, (int Count, int First, int Second)>();
        foreach (int f in faces)
            foreach (var e in m.Faces[f].Edges())
            {
                if (e.A == e.B) continue;
                var u = uses.GetValueOrDefault(e, (0, -1, -1));
                uses[e] = (u.Item1 + 1, u.Item1 == 0 ? f : u.Item2, u.Item1 == 1 ? f : u.Item3);
            }
        foreach (var (e, (count, first, second)) in uses)
        {
            bool outline = count == 1 || (count == 2 && Vector3.Dot(m.FaceNormal(first), m.FaceNormal(second)) < 0.9999f);
            if (outline) add(m.Positions[e.A], m.Positions[e.B]);
        }
    }

    /// <summary>
    /// The selection measured as one shape: two or more faces, two or more edges, or three or more vertices. Faces lying
    /// in one sloped plane are measured in that plane (along it horizontally and up its slope); anything else by its
    /// world bounding box. Null when there's nothing to combine (one face's sizes are its edges).
    /// </summary>
    public static Extent? Overall(Document doc, Selection sel)
    {
        int need = sel.Mode switch { SelectMode.Face or SelectMode.Edge => 2, SelectMode.Vertex => 3, _ => int.MaxValue };
        if (sel.Count < need || sel.Count > MaxElements) return null;
        var points = new List<Vector3>();
        var normals = new List<Vector3>();
        foreach (var i in sel.Items)
        {
            if (doc.Find(i.Mesh) is not { } m) continue;
            switch (sel.Mode)
            {
                case SelectMode.Face when i.A < m.Faces.Count:
                    points.AddRange(m.FacePoints(i.A));
                    normals.Add(m.FaceNormal(i.A));
                    break;
                case SelectMode.Edge when i.B < m.Positions.Count:
                    points.Add(m.Positions[i.A]);
                    points.Add(m.Positions[i.B]);
                    break;
                case SelectMode.Vertex when i.A < m.Positions.Count:
                    points.Add(m.Positions[i.A]);
                    break;
            }
        }
        if (points.Count < 2) return null;

        if (normals.Count > 0 && normals.All(n => Vector3.Dot(n, normals[0]) > 0.9999f) && AxisOf(normals[0]) < 0)
        {
            var n = normals[0];
            float depth = Vector3.Dot(points[0], n);
            if (points.All(p => MathF.Abs(Vector3.Dot(p, n) - depth) < 1e-3f))
            {
                // One sloped plane: across it horizontally (or along X on a near-floor), and up it.
                var u = MathF.Abs(n.Y) < 0.99f ? Vector3.Normalize(Vector3.Cross(Vector3.UnitY, n)) : Vector3.UnitX;
                var w = Vector3.Cross(n, u);
                float u0 = points.Min(p => Vector3.Dot(p, u)), u1 = points.Max(p => Vector3.Dot(p, u));
                float w0 = points.Min(p => Vector3.Dot(p, w)), w1 = points.Max(p => Vector3.Dot(p, w));
                return new Extent(n * depth + u * u0 + w * w0, u * (u1 - u0), w * (w1 - w0), Vector3.Zero);
            }
        }
        var min = points.Aggregate(Vector3.Min);
        var max = points.Aggregate(Vector3.Max);
        var d = max - min;
        return new Extent(min, new Vector3(d.X, 0, 0), new Vector3(0, d.Y, 0), new Vector3(0, 0, d.Z));
    }

    static int AxisOf(Vector3 n)
    {
        for (int i = 0; i < 3; i++) if (MathF.Abs(n[i]) > 0.99999f) return i;
        return -1;
    }

    public static IEnumerable<(Vector3 A, Vector3 B)> BoxEdges(Vector3 min, Vector3 max)
    {
        Vector3 C(int i) => new((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
        int[] pairs = [0, 1, 2, 3, 4, 5, 6, 7, 0, 2, 1, 3, 4, 6, 5, 7, 0, 4, 1, 5, 2, 6, 3, 7];
        for (int i = 0; i < pairs.Length; i += 2) yield return (C(pairs[i]), C(pairs[i + 1]));
    }
}

/// <summary>
/// Draws measurements beside edges: the length, coloured by the world axis the edge runs along (neutral otherwise), on
/// a dark tag just outside the shape (away from <c>center</c> on screen). One label per group, on the edge nearest the
/// camera that has room for it; labels that would overlap one already drawn are skipped. Shared by the selection
/// labels and the creation tools.
/// </summary>
public static class DimensionDraw
{
    const uint Tag = 0xE0141416;

    public static uint ColorOf(int axis) => axis >= 0 ? Theme.AxisU32[axis] : ImGui.ColorConvertFloat4ToU32(Theme.Text);

    /// <summary>Labels each group of <paramref name="dims"/> once.</summary>
    public static void Groups(ImDrawListPtr dl, Vector2 origin, ViewInfo v, IReadOnlyList<Dim> dims, Vector3 center,
        List<(Vector2 Min, Vector2 Max)>? placed = null)
    {
        placed ??= new List<(Vector2 Min, Vector2 Max)>();
        var best = new Dictionary<int, (Dim Dim, float Eye)>();
        foreach (var d in dims)
        {
            if (!Fits(v, d.A, d.B)) continue;
            float eye = Vector3.DistanceSquared((d.A + d.B) * 0.5f, v.Eye);
            if (!best.TryGetValue(d.Group, out var b) || eye < b.Eye) best[d.Group] = (d, eye);
        }
        // Longest first, so when labels crowd, the big sizes win.
        foreach (var (d, _) in best.Values.OrderByDescending(b => b.Dim.Length))
            Edge(dl, origin, v, d.A, d.B, center, ColorOf(d.Axis), placed);
    }

    /// <summary>
    /// The selection's overall size as dimension lines set out beyond the edge labels (extension lines to the shape, a
    /// line with end ticks, the size framed on it), one per side of <paramref name="e"/>, on the box edge nearest the
    /// camera. A side that an edge label in <paramref name="edges"/> already measures (same direction and length) is
    /// left out, so the overall size only adds what the edges don't say.
    /// </summary>
    public static void Overall(ImDrawListPtr dl, Vector2 origin, ViewInfo v, Extent e, IReadOnlyList<Dim> edges, List<(Vector2 Min, Vector2 Max)> placed)
    {
        var sides = new[] { e.U, e.V, e.W };
        var center = e.Corner + (e.U + e.V + e.W) * 0.5f;
        for (int s = 0; s < 3; s++)
        {
            var side = sides[s];
            float len = side.Length();
            if (len < 1e-5f) continue;
            var dir = side / len;
            if (edges.Any(d => MathF.Abs(d.Length - len) < 1e-4f && MathF.Abs(Vector3.Dot(Vector3.Normalize(d.B - d.A), dir)) > 0.99999f)) continue;

            // The four (or two, when flat) box edges running this way; the nearest that fits on screen carries it.
            var o1 = sides[(s + 1) % 3];
            var o2 = sides[(s + 2) % 3];
            (Vector3 A, Vector3 B)? pick = null;
            float best = float.MaxValue;
            foreach (var off in new[] { Vector3.Zero, o1, o2, o1 + o2 }.Distinct())
            {
                var a = e.Corner + off;
                var b = a + side;
                if (!Fits(v, a, b)) continue;
                float eye = Vector3.DistanceSquared((a + b) * 0.5f, v.Eye);
                if (eye < best) { best = eye; pick = (a, b); }
            }
            if (pick is { } p) DimensionLine(dl, origin, v, p.A, p.B, center, ColorOf(AxisOf(p.A, p.B)), placed);
        }
    }

    static void DimensionLine(ImDrawListPtr dl, Vector2 origin, ViewInfo v, Vector3 a, Vector3 b, Vector3 center, uint color,
        List<(Vector2 Min, Vector2 Max)> placed)
    {
        if (!v.Project(a, out var p) || !v.Project(b, out var q)) return;
        var along = Vector2.Normalize(q - p);
        var n = new Vector2(-along.Y, along.X);
        var mid = (p + q) * 0.5f;
        if (v.Project(center, out var c) && Vector2.Dot(n, mid - c) < 0) n = -n;

        string text = Labels.Measure(Vector3.Distance(a, b));
        var ts = ImGui.CalcTextSize(text);
        var half = ts * 0.5f + Theme.Px(5, 2);
        // Out past the edge labels (which sit about a label's depth from the edge).
        float off = Theme.Px(14) + 2 * (MathF.Abs(n.X) * half.X + MathF.Abs(n.Y) * half.Y);
        var p2 = p + n * off;
        var q2 = q + n * off;
        var pos = (p2 + q2) * 0.5f;
        var min = pos - half;
        var max = pos + half;
        foreach (var (pm, px) in placed)
            if (min.X < px.X && max.X > pm.X && min.Y < px.Y && max.Y > pm.Y) return;
        placed.Add((min, max));

        uint line = (color & 0x00FFFFFF) | 0xB0000000, faint = (color & 0x00FFFFFF) | 0x60000000;
        float w = Theme.Px(1);
        dl.AddLine(origin + p + n * Theme.Px(3), origin + p2 + n * Theme.Px(4), faint, w);
        dl.AddLine(origin + q + n * Theme.Px(3), origin + q2 + n * Theme.Px(4), faint, w);
        dl.AddLine(origin + p2, origin + q2, line, w);
        var tick = (n + along) * Theme.Px(3.5f);
        dl.AddLine(origin + p2 - tick, origin + p2 + tick, line, Theme.Px(1.5f));
        dl.AddLine(origin + q2 - tick, origin + q2 + tick, line, Theme.Px(1.5f));
        dl.AddRectFilled(origin + min, origin + max, Tag, Theme.Px(2));
        dl.AddRect(origin + min, origin + max, line, Theme.Px(2), ImDrawFlags.None, w);
        dl.AddText(origin + pos - ts * 0.5f, color, text);
    }

    /// <summary>The three sizes of an axis-aligned box (flat sides skipped), each on its edge nearest the camera.</summary>
    public static void Box(ImDrawListPtr dl, Vector2 origin, ViewInfo v, Vector3 min, Vector3 max)
    {
        var dims = new List<Dim>();
        foreach (var (a, b) in Dimensions.BoxEdges(min, max))
        {
            var d = b - a;
            int axis = d.X != 0 ? 0 : d.Y != 0 ? 1 : 2;
            if (d.LengthSquared() > 1e-10f) dims.Add(new Dim(a, b, axis, axis));
        }
        Groups(dl, origin, v, dims, (min + max) * 0.5f);
    }

    /// <summary>
    /// Every segment of a path (a polygon outline, a knife line), labelled beside it, outward from the path's middle.
    /// The newest segment wins when labels crowd.
    /// </summary>
    public static void Path(ImDrawListPtr dl, Vector2 origin, ViewInfo v, IReadOnlyList<Vector3> points)
    {
        if (points.Count < 2) return;
        var center = points.Aggregate(Vector3.Zero, (s, p) => s + p) / points.Count;
        var placed = new List<(Vector2 Min, Vector2 Max)>();
        for (int i = points.Count - 2; i >= 0; i--)
            if (Fits(v, points[i], points[i + 1]))
                Edge(dl, origin, v, points[i], points[i + 1], center, ColorOf(AxisOf(points[i], points[i + 1])), placed);
    }

    /// <summary>The world axis a segment runs along (within about a quarter degree), or -1.</summary>
    public static int AxisOf(Vector3 a, Vector3 b)
    {
        var d = b - a;
        float len = d.Length();
        if (len < 1e-6f) return -1;
        d /= len;
        for (int i = 0; i < 3; i++) if (MathF.Abs(d[i]) > 0.99999f) return i;
        return -1;
    }

    /// <summary>Long enough on screen to be worth a label.</summary>
    static bool Fits(ViewInfo v, Vector3 a, Vector3 b) =>
        v.Project(a, out var p) && v.Project(b, out var q) && Vector2.Distance(p, q) >= Theme.Px(18);

    /// <summary>
    /// One edge's length beside it, pushed off the edge away from <paramref name="center"/>'s spot on screen. With
    /// <paramref name="placed"/>, skipped if it would overlap a label already there.
    /// </summary>
    public static void Edge(ImDrawListPtr dl, Vector2 origin, ViewInfo v, Vector3 a, Vector3 b, Vector3 center, uint color,
        List<(Vector2 Min, Vector2 Max)>? placed = null, string? text = null)
    {
        if (!v.Project(a, out var p) || !v.Project(b, out var q)) return;
        text ??= Labels.Measure(Vector3.Distance(a, b));
        var mid = (p + q) * 0.5f;
        var along = q - p;
        if (along.LengthSquared() < 1e-6f) return;
        along = Vector2.Normalize(along);
        var n = new Vector2(-along.Y, along.X);
        var away = v.Project(center, out var c) ? mid - c : new Vector2(0, -1);
        if (Vector2.Dot(n, away) < 0 || (away.LengthSquared() < 1 && n.Y > 0)) n = -n;

        var ts = ImGui.CalcTextSize(text);
        var pad = Theme.Px(5, 2);
        var half = ts * 0.5f + pad;
        float push = Theme.Px(5) + MathF.Abs(n.X) * half.X + MathF.Abs(n.Y) * half.Y;
        var pos = Vector2.Clamp(mid + n * push, half + Theme.Px(2, 2), Vector2.Max(half + Theme.Px(2, 2), v.Size - half - Theme.Px(2, 2)));
        var min = pos - half;
        var max = pos + half;
        if (placed != null)
        {
            foreach (var (pm, px) in placed)
                if (min.X < px.X && max.X > pm.X && min.Y < px.Y && max.Y > pm.Y) return;
            placed.Add((min, max));
        }
        dl.AddRectFilled(origin + min, origin + max, Tag, Theme.Px(2));
        dl.AddText(origin + pos - ts * 0.5f, color, text);
    }

    /// <summary>A tag of coloured pieces (the drag readout), anchored beside a point and kept on screen.</summary>
    public static void Runs(ImDrawListPtr dl, Vector2 origin, ViewInfo v, Vector3 anchor, IReadOnlyList<(string Text, uint Color)> runs, Vector2 offset)
    {
        if (!v.Project(anchor, out var a)) return;
        float gap = Theme.Px(6);
        var size = new Vector2(runs.Sum(r => ImGui.CalcTextSize(r.Text).X) + gap * (runs.Count - 1), ImGui.GetTextLineHeight());
        var pad = Theme.Px(7, 4);
        var pos = Vector2.Clamp(a + offset, pad + Theme.Px(2, 2), Vector2.Max(pad + Theme.Px(2, 2), v.Size - size - pad - Theme.Px(2, 2)));
        dl.AddRectFilled(origin + pos - pad, origin + pos + size + pad, Tag, Theme.Px(3));
        float x = pos.X;
        foreach (var (text, color) in runs)
        {
            dl.AddText(origin + new Vector2(x, pos.Y), color, text);
            x += ImGui.CalcTextSize(text).X + gap;
        }
    }
}
