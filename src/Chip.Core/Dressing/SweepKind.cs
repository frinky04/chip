using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Texturing;

namespace Chip.Core.Dressing;

/// <summary>
/// <c>sweep</c> (dress): runs a profile along edges of matching faces: baseboards where walls meet floors, crown moulding
/// at ceilings, pipes in corners, beads on outside corners. Pieces from neighbouring meshes join with mitres, so a trim
/// round a room built from separate wall boxes is one continuous band.
/// </summary>
public sealed class SweepKind : RuleKind
{
    /// <summary>How close geometry must be to count as touching (metres).</summary>
    public const float Tolerance = 0.001f;

    public override string Name => "sweep";
    public override Stage Stage => Stage.Dress;
    public override string Summary => "Run a profile along edges of matching faces: baseboards, crown moulding, pipes.";

    public static readonly string[] EdgeKinds = ["contact", "boundary", "convex", "concave", "border"];

    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        new("faces", ParamType.Faces, null, "Faces whose edges get the profile (the profile stands out from these faces).", Required: true),
        new("edges", ParamType.Choice, "contact", "contact: where the faces sit on an \"onto\" face of any mesh (wall on floor). boundary: open edges. convex / concave: outside / inside corners. border: the outline of the matching faces.", Choices: EdgeKinds),
        new("onto", ParamType.Faces, FaceSelector.Up, "For contact: the faces they sit on. Default: up-facing faces (floors).", Required: false),
        new("minDeg", ParamType.Number, 30f, "For convex / concave: only corners at this angle or more (degrees).", 0, 180),
        new("profile", ParamType.Profile, Profile.Default, "Cross-section: { \"rect\": [depth, height] }, { \"round\": diameter } or { \"points\": [[x, y], ...] }; x out of the face, y along the surface it sits on (metres)."),
        new("offset", ParamType.Pair, Vector2.Zero, "Moves the profile [out, along] from the corner (metres)."),
        new("material", ParamType.Text, null, "Material for the sweep. Default: the material of the face it runs along."),
    ];

    /// <summary>One straight run of an edge to sweep along, with the frame the profile sits in.</summary>
    internal readonly record struct Segment(Vector3 A, Vector3 B, Vector3 Out, Vector3 Up, int Face, string Material, TextureMapping Map)
    {
        public Vector3 Dir => Vector3.Normalize(B - A);
        public float Length => Vector3.Distance(A, B);
    }

    /// <summary>
    /// The segments of <paramref name="m"/> this rule sweeps. <paramref name="neighbours"/> (meshes touching
    /// <paramref name="m"/>'s bounds, itself included) supply the surfaces contact edges sit on. Returns the number of
    /// faces matched too, for the zero-match warning.
    /// </summary>
    internal static (List<Segment> Segments, int Matched) Segments(PolyMesh m, Rule rule, IReadOnlyList<PolyMesh> neighbours)
    {
        var faces = rule.Get<FaceSelector>("faces");
        string kind = rule.Text("edges")!;
        string skip = rule.SkipTag;
        var normals = new Vector3[m.Faces.Count];
        var matched = new bool[m.Faces.Count];
        int count = 0;
        for (int f = 0; f < m.Faces.Count; f++)
        {
            normals[f] = m.FaceNormal(f);
            matched[f] = normals[f] != Vector3.Zero && !m.Faces[f].Tags.Contains(skip) && faces.Matches(m, f, normals[f]);
            if (matched[f]) count++;
        }
        var result = new List<Segment>();
        if (count == 0) return (result, 0);

        if (kind == "contact")
        {
            var onto = rule.Get<FaceSelector>("onto");
            var surfaces = Surfaces(neighbours, onto);
            var raw = new List<Segment>();
            for (int f = 0; f < m.Faces.Count; f++)
                if (matched[f]) ContactSegments(m, f, normals[f], surfaces, raw);
            // Walls overlap at corners: the stretch of a wall's foot that runs on behind the next wall is hidden.
            var solids = raw.Count == 0 ? [] : neighbours.Where(n => n != m).Select(Solid.Of).OfType<Solid>().ToList();
            foreach (var s in raw) result.AddRange(Visible(s, solids));
            return (result, count);
        }

        var edgeFaces = Topology.EdgeFaces(m);
        float minCos = MathF.Cos(rule.Number("minDeg") * MathF.PI / 180f);
        for (int f = 0; f < m.Faces.Count; f++)
        {
            if (!matched[f]) continue;
            var face = m.Faces[f];
            var centre = m.FaceCenter(f);
            for (int i = 0; i < face.Verts.Length; i++)
            {
                int a = face.Verts[i], b = face.Verts[(i + 1) % face.Verts.Length];
                var pa = m.Positions[a];
                var pb = m.Positions[b];
                if (Vector3.DistanceSquared(pa, pb) < Tolerance * Tolerance) continue;
                var t = Vector3.Normalize(pb - pa);
                var others = edgeFaces.TryGetValue(new Edge(a, b), out var fs) ? fs.Where(o => o != f).ToList() : [];
                Vector3 up;
                switch (kind)
                {
                    case "boundary" when others.Count == 0:
                    case "border" when others.Count == 0 || !matched[others[0]]:
                        up = InPlaneAway(normals[f], t, centre, (pa + pb) * 0.5f);
                        break;
                    case "convex" or "concave" when others.Count == 1:
                    {
                        int g = others[0];
                        if (matched[g] && g < f) continue; // both sides match: sweep the edge once
                        if (Vector3.Dot(normals[f], normals[g]) > minCos + 1e-5f) continue;
                        float side = Vector3.Dot(m.FaceCenter(g) - centre, normals[f]);
                        if (kind == "convex" ? side >= -1e-5f : side <= 1e-5f) continue;
                        up = normals[g];
                        break;
                    }
                    default:
                        continue;
                }
                if (Frame(normals[f], up, t) is not var (o, u)) continue;
                result.Add(new Segment(pa, pb, o, u, f, face.Material, face.Map));
            }
        }
        return (result, count);
    }

    /// <summary>A closed neighbour mesh as triangles, for "is this point inside another solid".</summary>
    sealed class Solid
    {
        public readonly List<(Vector3 A, Vector3 B, Vector3 C)> Tris = new();
        public Vector3 Min, Max;

        public static Solid? Of(PolyMesh m)
        {
            var edges = Topology.EdgeFaces(m);
            if (edges.Count == 0 || edges.Values.Any(fs => fs.Count != 2)) return null; // open meshes have no inside
            var s = new Solid();
            (s.Min, s.Max) = m.Bounds();
            var tri = new List<int>();
            for (int f = 0; f < m.Faces.Count; f++)
            {
                tri.Clear();
                Triangulator.Triangulate(m, f, tri);
                var v = m.Faces[f].Verts;
                for (int i = 0; i + 2 < tri.Count; i += 3)
                    s.Tris.Add((m.Positions[v[tri[i]]], m.Positions[v[tri[i + 1]]], m.Positions[v[tri[i + 2]]]));
            }
            return s;
        }

        /// <summary>Parameter ranges of the line <paramref name="p"/> + t·<paramref name="d"/> that are inside (by crossing parity).</summary>
        public IEnumerable<(float, float)> InsideSpans(Vector3 p, Vector3 d)
        {
            var hits = new List<float>();
            foreach (var (a, b, c) in Tris)
            {
                var e1 = b - a;
                var e2 = c - a;
                var h = Vector3.Cross(d, e2);
                float det = Vector3.Dot(e1, h);
                if (MathF.Abs(det) < 1e-12f) continue;
                float inv = 1 / det;
                var s = p - a;
                float u = Vector3.Dot(s, h) * inv;
                if (u < 0 || u > 1) continue;
                var q = Vector3.Cross(s, e1);
                float v = Vector3.Dot(d, q) * inv;
                if (v < 0 || u + v > 1) continue;
                hits.Add(Vector3.Dot(e2, q) * inv);
            }
            hits.Sort();
            for (int i = hits.Count - 1; i > 0; i--) if (hits[i] - hits[i - 1] < 1e-6f) hits.RemoveAt(i); // shared triangle edge
            for (int i = 0; i + 1 < hits.Count; i += 2) yield return (hits[i], hits[i + 1]);
        }
    }

    /// <summary>
    /// The parts of <paramref name="s"/> whose trim would be in the open: a point just off the edge (2 mm out and up) must
    /// not be inside another solid.
    /// </summary>
    static IEnumerable<Segment> Visible(Segment s, List<Solid> solids)
    {
        var d = s.B - s.A;
        var probe = s.A + (s.Out + s.Up) * 0.002f;
        var lo = Vector3.Min(s.A, s.B) - new Vector3(0.01f);
        var hi = Vector3.Max(s.A, s.B) + new Vector3(0.01f);
        var hidden = new List<(float, float)>();
        foreach (var solid in solids)
        {
            if (hi.X < solid.Min.X || lo.X > solid.Max.X || hi.Y < solid.Min.Y || lo.Y > solid.Max.Y || hi.Z < solid.Min.Z || lo.Z > solid.Max.Z) continue;
            foreach (var (t0, t1) in solid.InsideSpans(probe, d))
                if (t1 > 0 && t0 < 1) hidden.Add((MathF.Max(t0, 0), MathF.Min(t1, 1)));
        }
        if (hidden.Count == 0) { yield return s; yield break; }
        hidden.Sort((x, y) => x.Item1.CompareTo(y.Item1));
        float minT = Tolerance / MathF.Max(d.Length(), 1e-6f);
        float at = 0;
        foreach (var (h0, h1) in hidden)
        {
            if (h0 - at > minT) yield return s with { A = s.A + d * at, B = s.A + d * h0 };
            at = MathF.Max(at, h1);
        }
        if (1 - at > minT) yield return s with { A = s.A + d * at, B = s.B };
    }

    /// <summary>Out and up made perpendicular to the edge (null if degenerate).</summary>
    static (Vector3 Out, Vector3 Up)? Frame(Vector3 normal, Vector3 up, Vector3 t)
    {
        var u = up - Vector3.Dot(up, t) * t;
        var o = normal - Vector3.Dot(normal, t) * t;
        if (u.LengthSquared() < 1e-8f || o.LengthSquared() < 1e-8f) return null;
        return (Vector3.Normalize(o), Vector3.Normalize(u));
    }

    /// <summary>The direction in the face's plane, across the edge, pointing away from the face.</summary>
    static Vector3 InPlaneAway(Vector3 normal, Vector3 t, Vector3 centre, Vector3 mid)
    {
        var u = Vector3.Normalize(Vector3.Cross(normal, t));
        return Vector3.Dot(u, centre - mid) > 0 ? -u : u;
    }

    /// <summary>A face that contact edges can sit on, flattened to its plane once.</summary>
    sealed record Surface(int MeshId, Vector3 Normal, Vector3 Origin, Vector3 U, Vector3 V, Vector2[] Polygon, Vector3 Min, Vector3 Max);

    static List<Surface> Surfaces(IReadOnlyList<PolyMesh> meshes, FaceSelector onto)
    {
        var list = new List<Surface>();
        foreach (var m in meshes)
            for (int g = 0; g < m.Faces.Count; g++)
            {
                var n = m.FaceNormal(g);
                if (n == Vector3.Zero || !onto.Matches(m, g, n)) continue;
                var origin = m.Positions[m.Faces[g].Verts[0]];
                var u = Vector3.Normalize(Vector3.Cross(MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX, n));
                var v = Vector3.Cross(n, u);
                var pts = m.Faces[g].Verts.Select(i => m.Positions[i]).ToArray();
                var poly = pts.Select(p => new Vector2(Vector3.Dot(p - origin, u), Vector3.Dot(p - origin, v))).ToArray();
                var min = pts.Aggregate(Vector3.Min) - new Vector3(Tolerance);
                var max = pts.Aggregate(Vector3.Max) + new Vector3(Tolerance);
                list.Add(new Surface(m.Id, n, origin, u, v, poly, min, max));
            }
        return list;
    }

    /// <summary>
    /// Every stretch of face <paramref name="f"/>'s edges lying on a surface it stands on (in that surface's plane, inside its
    /// outline, with the face above it). Stretches over several surfaces (a floor split in tiles) merge.
    /// </summary>
    static void ContactSegments(PolyMesh m, int f, Vector3 normal, List<Surface> surfaces, List<Segment> result)
    {
        var face = m.Faces[f];
        var centre = m.FaceCenter(f);
        for (int i = 0; i < face.Verts.Length; i++)
        {
            var a = m.Positions[face.Verts[i]];
            var b = m.Positions[face.Verts[(i + 1) % face.Verts.Length]];
            float len = Vector3.Distance(a, b);
            if (len < Tolerance) continue;
            var lo = Vector3.Min(a, b);
            var hi = Vector3.Max(a, b);
            var spans = new List<(float T0, float T1, Vector3 Up)>();
            foreach (var s in surfaces)
            {
                if (hi.X < s.Min.X || lo.X > s.Max.X || hi.Y < s.Min.Y || lo.Y > s.Max.Y || hi.Z < s.Min.Z || lo.Z > s.Max.Z) continue;
                if (MathF.Abs(Vector3.Dot(normal, s.Normal)) > 0.95f) continue; // coplanar neighbour, not a surface to stand on
                if (MathF.Abs(Vector3.Dot(a - s.Origin, s.Normal)) > Tolerance || MathF.Abs(Vector3.Dot(b - s.Origin, s.Normal)) > Tolerance) continue;
                if (Vector3.Dot(centre - s.Origin, s.Normal) <= Tolerance) continue; // the face must stand on the surface, not hang under it
                var a2 = new Vector2(Vector3.Dot(a - s.Origin, s.U), Vector3.Dot(a - s.Origin, s.V));
                var b2 = new Vector2(Vector3.Dot(b - s.Origin, s.U), Vector3.Dot(b - s.Origin, s.V));
                foreach (var (t0, t1) in Inside(a2, b2, s.Polygon, Tolerance / len))
                    spans.Add((t0, t1, s.Normal));
            }
            if (spans.Count == 0) continue;
            spans.Sort((x, y) => x.T0.CompareTo(y.T0));
            var t = (b - a) / len;
            float minT = Tolerance / len;
            int k = 0;
            while (k < spans.Count)
            {
                var (s0, s1, up) = spans[k++];
                while (k < spans.Count && spans[k].T0 <= s1 + minT && Vector3.Dot(spans[k].Up, up) > 0.999f) s1 = MathF.Max(s1, spans[k++].T1);
                if (s1 - s0 < minT) continue;
                if (Frame(normal, up, t) is not var (o, u)) continue;
                result.Add(new Segment(Vector3.Lerp(a, b, s0), Vector3.Lerp(a, b, s1), o, u, f, face.Material, face.Map));
            }
        }
    }

    /// <summary>Parameter ranges of segment a→b (0..1) that lie inside or on the polygon (within <paramref name="eps"/>).</summary>
    static IEnumerable<(float, float)> Inside(Vector2 a, Vector2 b, Vector2[] poly, float eps)
    {
        var ts = new List<float> { 0, 1 };
        var d = b - a;
        for (int i = 0; i < poly.Length; i++)
        {
            var p = poly[i];
            var q = poly[(i + 1) % poly.Length];
            var e = q - p;
            float den = d.X * e.Y - d.Y * e.X;
            if (MathF.Abs(den) < 1e-12f) continue; // parallel (including lying along the outline): midpoint tests decide
            var w = p - a;
            float t = (w.X * e.Y - w.Y * e.X) / den;
            float s = (w.X * d.Y - w.Y * d.X) / den;
            if (t > 0 && t < 1 && s >= -1e-6f && s <= 1 + 1e-6f) ts.Add(t);
        }
        ts.Sort();
        float tol = eps * d.Length();
        float start = -1;
        for (int i = 0; i + 1 < ts.Count; i++)
        {
            if (ts[i + 1] - ts[i] < 1e-7f) continue;
            bool inside = InsideOrOn(a + d * ((ts[i] + ts[i + 1]) * 0.5f), poly, MathF.Max(tol, SweepKind.Tolerance));
            if (inside && start < 0) start = ts[i];
            if (!inside && start >= 0) { yield return (start, ts[i]); start = -1; }
        }
        if (start >= 0) yield return (start, 1);
    }

    static bool InsideOrOn(Vector2 p, Vector2[] poly, float tol)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            var a = poly[i];
            var b = poly[j];
            var ab = b - a;
            float t = Math.Clamp(Vector2.Dot(p - a, ab) / MathF.Max(ab.LengthSquared(), 1e-12f), 0, 1);
            if (Vector2.DistanceSquared(p, a + ab * t) <= tol * tol) return true;
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    /// <summary>The frame of the segment joined at one end, if any: its direction pointing away from the joint.</summary>
    internal readonly record struct Joint(Vector3 Away, Vector3 Out, Vector3 Up);

    /// <summary>
    /// Sweeps the rule's profile along <paramref name="segments"/> (all from one source mesh). Each end joins
    /// <c>joints[i].Start</c> / <c>.End</c> with a mitre, or gets a cap when there's nothing to join.
    /// </summary>
    internal static PolyMesh Build(Rule rule, List<Segment> segments, (Joint? Start, Joint? End)[] joints, string name)
    {
        var profile = rule.Get<Profile>("profile");
        var offset = rule.Get<Vector2>("offset");
        string? material = rule.Text("material");
        bool surfaceSides = rule.Text("edges") is "contact" or "concave";
        var pts = profile.Points.Select(p => p + offset).ToArray();
        int n = pts.Length;
        float area = 0;
        for (int i = 0; i < n; i++) area += pts[i].X * pts[(i + 1) % n].Y - pts[(i + 1) % n].X * pts[i].Y;
        float reach = MathF.Max(profile.Reach + offset.Length(), 0.01f);

        var m = new PolyMesh { Name = name };
        for (int si = 0; si < segments.Count; si++)
        {
            var s = segments[si];
            var dir = s.Dir;
            Vector3 Off(Vector2 p, Vector3 o, Vector3 u) => p.X * o + p.Y * u;
            Vector3[] Ring(Vector3 at, Vector3 away, Joint? joint)
            {
                var ring = new Vector3[n];
                for (int p = 0; p < n; p++)
                {
                    var self = at + Off(pts[p], s.Out, s.Up);
                    ring[p] = joint is { } j ? Mitre(self, away, at + Off(pts[p], j.Out, j.Up), j.Away, reach) : self;
                }
                return ring;
            }
            var r0 = Ring(s.A, dir, joints[si].Start);
            var r1 = Ring(s.B, -dir, joints[si].End);
            int b0 = m.Positions.Count;
            m.Positions.AddRange(r0);
            m.Positions.AddRange(r1);
            int firstFace = m.Faces.Count;
            string mat = material ?? s.Material;
            for (int p = 0; p < n; p++)
            {
                int q = (p + 1) % n;
                if (surfaceSides && ((MathF.Abs(pts[p].X) < 1e-5f && MathF.Abs(pts[q].X) < 1e-5f) || (MathF.Abs(pts[p].Y) < 1e-5f && MathF.Abs(pts[q].Y) < 1e-5f)))
                    continue; // lies against the wall or floor: never seen
                var e = pts[q] - pts[p];
                var out2 = area > 0 ? new Vector2(e.Y, -e.X) : new Vector2(-e.Y, e.X);
                AddOriented(m, [b0 + p, b0 + q, b0 + n + q, b0 + n + p], out2.X * s.Out + out2.Y * s.Up, mat, s.Map);
            }
            if (joints[si].Start == null) AddOriented(m, Enumerable.Range(b0, n).ToArray(), -dir, mat, s.Map);
            if (joints[si].End == null) AddOriented(m, Enumerable.Range(b0 + n, n).ToArray(), dir, mat, s.Map);
            MeshOps.AlignToGrid(m, Enumerable.Range(firstFace, m.Faces.Count - firstFace), s.Map);
        }
        MeshOps.AutoSmooth(m, 50f); // round profiles shade smooth; rect corners (90°) stay hard
        return m;
    }

    static void AddOriented(PolyMesh m, int[] verts, Vector3 outward, string material, TextureMapping map)
    {
        m.Faces.Add(new Face(verts, material, map));
        if (Vector3.Dot(m.FaceNormal(m.Faces.Count - 1), outward) < 0) Array.Reverse(m.Faces[^1].Verts);
    }

    /// <summary>
    /// Where a profile point's line along this segment meets the same point's line along the joined segment (closest
    /// approach), so the two pieces meet without gaps or overlaps. Capped so near-folded joints don't spike.
    /// </summary>
    static Vector3 Mitre(Vector3 p1, Vector3 d1, Vector3 p2, Vector3 d2, float reach)
    {
        float b = Vector3.Dot(d1, d2);
        float den = 1 - b * b;
        if (den < 1e-6f) return (p1 + p2) * 0.5f;
        var w = p1 - p2;
        float d = Vector3.Dot(d1, w), e = Vector3.Dot(d2, w);
        float s = (b * e - d) / den;
        float r = (e - b * d) / den;
        var hit = (p1 + d1 * s + p2 + d2 * r) * 0.5f;
        return Vector3.Distance(hit, p1) > reach * 4 ? p1 : hit;
    }
}
