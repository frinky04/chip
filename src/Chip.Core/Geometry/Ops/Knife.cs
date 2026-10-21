using System.Numerics;

namespace Chip.Core.Geometry;

/// <summary>What a knife cut produced: faces appended after the originals, and the new edges along the cut.</summary>
public sealed record KnifeResult(List<int> NewFaces, List<Edge> CutEdges);

public static partial class MeshOps
{
    /// <summary>
    /// Knife cut as seen from <paramref name="eye"/>: the polyline <paramref name="path"/> is projected onto each face
    /// along rays from the eye, and each face splits into every region the line divides it into. The line may cross
    /// itself, double back or close a loop: a stroke round a square and on to the border cuts the square out as its own
    /// face. Where the line starts or ends inside a face without meeting the rest of the cut, that end carries on in its
    /// direction to the first border or cut line it meets (Scythe / Source 2; a face can't hold a dangling edge). The
    /// extension stays within that face. A closed loop that touches nothing gets two straight bridges out to its
    /// surroundings, since faces can't have holes. Crossed edges get one shared vertex, inserted into every face using
    /// that edge, so a closed mesh stays closed. Split faces keep index, material and mapping; extra pieces are appended.
    /// A face that crosses itself is untangled by the same cut.
    /// </summary>
    /// <param name="faces">Faces to cut; null means all faces.</param>
    /// <param name="frontFacingOnly">Skip faces pointing away from the eye (the knife cuts what you see).</param>
    public static KnifeResult Knife(PolyMesh m, IReadOnlyList<Vector3> path, Vector3 eye, IEnumerable<int>? faces = null, bool frontFacingOnly = true)
    {
        var result = new KnifeResult(new List<int>(), new List<Edge>());
        if (path.Count < 2) return result;

        var points = new EdgePoints(m);
        var pieces = new List<(int Face, List<int[]> Loops, Face Original, Vector3[] Corners)>();
        foreach (int f in (faces ?? Enumerable.Range(0, m.Faces.Count)).Distinct().Order().ToList())
        {
            var face = m.Faces[f];
            if (face.Verts.Length < 3) continue;
            if (frontFacingOnly && Vector3.Dot(m.FaceNormal(f), m.FaceCenter(f) - eye) >= 0) continue;
            var frame = new FaceFrame(m, f);
            var loop = frame.Loop(m, f);
            double eps = 10 * FaceFrame.Epsilon(loop, frame.Origin);
            var cuts = KnifeCuts(frame, loop, path, eye, eps);
            if (cuts.Count == 0) continue;
            if (PlanarSplit.Split(loop, cuts, eps) is not { Regions.Count: > 1 } split) continue;
            if (PieceLoops(m, f, frame, split, points) is not { } loops) continue;
            pieces.Add((f, loops, face.Clone(), face.Verts.Select(v => m.Positions[v]).ToArray()));
            result.CutEdges.AddRange(SharedEdges(loops));
        }

        WritePieces(m, pieces, points, result.NewFaces);
        return result;
    }

    /// <summary>
    /// Replaces each face with its pieces (the first keeps the index, the rest are appended to <paramref name="added"/>),
    /// threads edge points into every face along their edges, and gives the pieces explicit UVs where the face had them.
    /// </summary>
    static void WritePieces(PolyMesh m, List<(int Face, List<int[]> Loops, Face Original, Vector3[] Corners)> pieces, EdgePoints points, List<int> added)
    {
        var written = new List<(List<int> Faces, Face Original, Vector3[] Corners)>();
        foreach (var (f, loops, original, corners) in pieces)
        {
            var indices = new List<int>();
            for (int i = 0; i < loops.Count; i++)
            {
                var piece = original.Derive(loops[i]);
                piece.Dominant = original.Dominant;
                if (i == 0) { m.Faces[f] = piece; indices.Add(f); }
                else { indices.Add(m.Faces.Count); added.Add(m.Faces.Count); m.Faces.Add(piece); }
            }
            written.Add((indices, original, corners));
        }
        points.InsertIntoFaces();
        PieceUVs(m, written);
        points.SplitSoftEdges();
    }

    /// <summary>
    /// The cut in face <paramref name="frame"/>'s plane: each path segment projected along eye rays (clipped where it
    /// would run past the horizon), plus the extensions of loose ends that stop inside the face. Empty when nothing
    /// comes near the face.
    /// </summary>
    static List<(P2 A, P2 B)> KnifeCuts(FaceFrame frame, P2[] loop, IReadOnlyList<Vector3> path, Vector3 eye, double eps)
    {
        var n = frame.Normal;
        double h = Vector3.Dot(frame.Origin - eye, n);
        if (Math.Abs(h) < 1e-6) return []; // the eye is in the face's plane: seen edge-on
        double sign = Math.Sign(h), floor = Math.Abs(h) * 1e-4; // a point may project at most 10,000 times its distance

        P2 Project(Vector3 p)
        {
            var r = p - eye;
            return frame.To2(eye + r * (float)(h / Vector3.Dot(r, n)));
        }

        var segs = new (P2 A, P2 B, bool FromStart, bool ToEnd)?[path.Count - 1];
        for (int i = 0; i < segs.Length; i++)
        {
            Vector3 p0 = path[i], p1 = path[i + 1];
            double g0 = Vector3.Dot(p0 - eye, n) * sign, g1 = Vector3.Dot(p1 - eye, n) * sign;
            if (g0 < floor && g1 < floor) continue;
            double s0 = g0 < floor ? (floor - g0) / (g1 - g0) : 0, s1 = g1 < floor ? (floor - g0) / (g1 - g0) : 1;
            var a = Project(Vector3.Lerp(p0, p1, (float)s0));
            var b = Project(Vector3.Lerp(p0, p1, (float)s1));
            if ((b - a).Length > eps) segs[i] = (a, b, s0 == 0, s1 == 1);
        }

        // Nothing near this face's bounds: no cut, and no end inside it to extend.
        double x0 = loop.Min(p => p.X) - eps, x1 = loop.Max(p => p.X) + eps, y0 = loop.Min(p => p.Y) - eps, y1 = loop.Max(p => p.Y) + eps;
        if (!segs.Any(s => s is { } v && Math.Max(v.A.X, v.B.X) >= x0 && Math.Min(v.A.X, v.B.X) <= x1
                && Math.Max(v.A.Y, v.B.Y) >= y0 && Math.Min(v.A.Y, v.B.Y) <= y1)) return [];

        var cuts = new List<(P2 A, P2 B)>();
        var owner = new List<int>();
        for (int i = 0; i < segs.Length; i++)
            if (segs[i] is { } s) { cuts.Add((s.A, s.B)); owner.Add(i); }

        // An end strictly inside the face that meets no other part of the line carries on to whatever it reaches first.
        var extensions = new List<(P2 A, P2 B)>();
        void Extend(int seg, P2 from, P2 away)
        {
            var dir = (from - away) * (1 / (from - away).Length);
            if (P2.Winding(loop, from) <= 0 || DistanceToLoop(loop, from) <= eps) return;
            for (int c = 0; c < cuts.Count; c++)
                if (owner[c] != seg && P2.SegmentDistance(from, cuts[c].A, cuts[c].B) <= eps) return;
            var others = cuts.Where((_, c) => owner[c] != seg).Concat(Enumerable.Range(0, loop.Length).Select(i => (loop[i], loop[(i + 1) % loop.Length])));
            if (RayHit(from, dir, others, eps) is { } hit) extensions.Add((from, hit));
        }
        if (segs[0] is { FromStart: true } first) Extend(0, first.A, first.B);
        if (segs[^1] is { ToEnd: true } last) Extend(segs.Length - 1, last.B, last.A);
        cuts.AddRange(extensions);
        return cuts;
    }

    static double DistanceToLoop(P2[] loop, P2 p)
    {
        double best = double.MaxValue;
        for (int i = 0; i < loop.Length; i++) best = Math.Min(best, P2.SegmentDistance(p, loop[i], loop[(i + 1) % loop.Length]));
        return best;
    }

    /// <summary>The nearest point where a ray from <paramref name="o"/> (unit <paramref name="dir"/>) meets one of the segments.</summary>
    static P2? RayHit(P2 o, P2 dir, IEnumerable<(P2 A, P2 B)> segs, double eps)
    {
        double best = double.MaxValue;
        foreach (var (a, b) in segs)
        {
            var e = b - a;
            double den = P2.Cross(dir, e), le = e.Length;
            if (Math.Abs(den) <= 1e-12 * le) continue;
            var w = a - o;
            double t = P2.Cross(w, e) / den, u = P2.Cross(w, dir) / den;
            if (t > eps && u >= -eps / le && u <= 1 + eps / le) best = Math.Min(best, t);
        }
        return best < double.MaxValue ? o + dir * best : null;
    }

    /// <summary>
    /// Vertex loops for the regions of face <paramref name="f"/>: corners keep their vertices, points on the face's
    /// edges go through <paramref name="points"/> (shared with neighbours), interior points become new vertices. The
    /// region holding the face's first corner comes first, so it keeps the face's index. Null if a region degenerates.
    /// </summary>
    static List<int[]>? PieceLoops(PolyMesh m, int f, FaceFrame frame, PlanarSplit.Result split, EdgePoints points)
    {
        var verts = m.Faces[f].Verts;
        int k = verts.Length;
        if (split.Regions.Any(r => r.Count < 3 || r.Distinct().Count() != r.Count)) return null;

        var ids = new Dictionary<int, int>();
        int Vertex(int n)
        {
            if (ids.TryGetValue(n, out int v)) return v;
            var node = split.Nodes[n];
            if (node.Corner >= 0) v = verts[node.Corner];
            else if (node.On.Count > 0)
            {
                v = node.On.Select(o => points.Find(verts[o.Edge], verts[(o.Edge + 1) % k], (float)o.T)).FirstOrDefault(x => x != null)
                    ?? points.At(verts[node.On[0].Edge], verts[(node.On[0].Edge + 1) % k], (float)node.On[0].T);
            }
            else
            {
                v = m.Positions.Count;
                m.Positions.Add(frame.To3(node.P));
            }
            foreach (var (e, t) in node.On) points.Register(v, verts[e], verts[(e + 1) % k], (float)t);
            return ids[n] = v;
        }

        var regions = split.Regions.OrderByDescending(r => r.Any(n => split.Nodes[n].Corner == 0))
            .ThenByDescending(r => P2.LoopArea(r.Select(n => split.Nodes[n].P).ToList())).ToList();
        var loops = regions.Select(r => r.Select(Vertex).ToArray()).ToList();
        return loops.All(l => l.Distinct().Count() == l.Length) ? loops : null;
    }

    /// <summary>Edges two of the loops share: the cut between them.</summary>
    static IEnumerable<Edge> SharedEdges(List<int[]> loops) =>
        loops.SelectMany(l => l.Select((v, i) => new Edge(v, l[(i + 1) % l.Length]))).GroupBy(e => e).Where(g => g.Count() > 1).Select(g => g.Key);

    /// <summary>Explicit UVs on split pieces: original corners keep theirs, new corners are interpolated on the original face.</summary>
    static void PieceUVs(PolyMesh m, List<(List<int> Faces, Face Original, Vector3[] Corners)> written)
    {
        foreach (var (indices, original, corners) in written)
        {
            if (original.UVs is not { } uvs) continue;
            var known = new Dictionary<int, Vector2>();
            for (int i = 0; i < original.Verts.Length; i++) known.TryAdd(original.Verts[i], uvs[i]);
            foreach (int pf in indices)
                m.Faces[pf].UVs = m.Faces[pf].Verts.Select(v => known.TryGetValue(v, out var uv) ? uv : InterpolateUV(corners, uvs, m.Positions[v])).ToArray();
        }
    }
}
