using System.Numerics;

namespace Chip.Core.Geometry;

/// <summary>What a knife cut produced: faces appended after the originals, and the new edges along the cut.</summary>
public sealed record KnifeResult(List<int> NewFaces, List<Edge> CutEdges);

public static partial class MeshOps
{
    /// <summary>
    /// Knife cut as seen from <paramref name="eye"/>: the polyline <paramref name="path"/> is projected onto each face
    /// along rays from the eye, and every face it crosses from border to border is split along it (a projected point
    /// inside a face becomes a vertex of the cut). Where the line starts or ends inside a face, that end carries on in
    /// its direction to the face's border (Scythe / Source 2 behaviour; a face can't hold a dangling edge). The extension
    /// stays within that face. Crossed edges get one shared vertex, inserted into every face using that edge, so a
    /// closed mesh stays closed. Split faces keep index, material and mapping; extra pieces are appended.
    /// </summary>
    /// <param name="faces">Faces to cut; null means all faces.</param>
    /// <param name="frontFacingOnly">Skip faces pointing away from the eye (the knife cuts what you see).</param>
    public static KnifeResult Knife(PolyMesh m, IReadOnlyList<Vector3> path, Vector3 eye, IEnumerable<int>? faces = null, bool frontFacingOnly = true)
    {
        var result = new KnifeResult(new List<int>(), new List<Edge>());
        if (path.Count < 2) return result;

        // One cutting plane per segment, through the eye and both segment ends.
        var planes = new (Vector3 A, Vector3 Ra, Vector3 Rb, Vector3 N, float Det)?[path.Count - 1];
        for (int i = 0; i < planes.Length; i++)
        {
            var ra = path[i] - eye; var rb = path[i + 1] - eye;
            var c = Vector3.Cross(ra, rb);
            float len = c.Length();
            if (len < 1e-9f) continue;
            planes[i] = (path[i], ra, rb, c / len, len);
        }

        var splits = new Dictionary<Edge, List<(float T, int V)>>(); // vertices added on original edges, t from Edge.A
        var splitOf = new Dictionary<int, (Edge E, float T)>();
        int SplitVertex(int a, int b, float tFromA)
        {
            var e = new Edge(a, b);
            float t = a == e.A ? tFromA : 1 - tFromA;
            if (!splits.TryGetValue(e, out var list)) splits[e] = list = new List<(float, int)>();
            foreach (var (lt, lv) in list)
                if (MathF.Abs(lt - t) < 1e-4f) return lv;
            int v = m.Positions.Count;
            m.Positions.Add(Vector3.Lerp(m.Positions[e.A], m.Positions[e.B], t));
            list.Add((t, v));
            splitOf[v] = (e, t);
            return v;
        }

        var targets = (faces ?? Enumerable.Range(0, m.Faces.Count)).Distinct().Order().ToList();
        var pieces = new List<(int Face, List<List<int>> Loops, Face Original, Vector3[] Corners)>();
        foreach (int f in targets)
        {
            var face = m.Faces[f];
            var verts = face.Verts;
            int k = verts.Length;
            var normal = m.FaceNormal(f);
            var origin = m.Positions[verts[0]];
            if (frontFacingOnly && Vector3.Dot(normal, m.FaceCenter(f) - eye) >= 0) continue;

            // Face plane frame for inside tests and projection of interior path points.
            var bu = Vector3.Normalize(Vector3.Cross(normal, MathF.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
            var bv = Vector3.Cross(normal, bu);
            Vector2 To2(Vector3 p) => new(Vector3.Dot(p - origin, bu), Vector3.Dot(p - origin, bv));
            var poly = verts.Select(v => To2(m.Positions[v])).ToList();
            Vector3? Project(Vector3 p)
            {
                float den = Vector3.Dot(p - eye, normal);
                if (MathF.Abs(den) < 1e-9f) return null;
                float t = Vector3.Dot(origin - eye, normal) / den;
                return t > 0 ? eye + (p - eye) * t : null;
            }
            bool Inside(Vector3 p) => Project(p) is { } q && PointInPolygon(To2(q), poly) && DistanceToBorder(To2(q), poly) > 1e-4f;

            // A line that stops inside this face carries on in its direction to the face's border (Scythe / Source 2):
            // only within this face, and only at an end that is actually inside it.
            bool extendStart = Inside(path[0]), extendEnd = Inside(path[^1]);

            // Crossings of the projected path with this face's border, ordered along the path. G is the path parameter:
            // segment index + fraction, below 0 or beyond the last segment for the extensions.
            var crossings = new List<(double G, int Corner, bool AtVertex, float T, Vector3 P)>();
            for (int i = 0; i < planes.Length; i++)
            {
                if (planes[i] is not { } pl) continue;
                var dist = verts.Select(v => Vector3.Dot(m.Positions[v] - pl.A, pl.N)).ToArray();
                bool back = extendStart && i == 0, forward = extendEnd && i == planes.Length - 1;
                bool Within(Vector3 x, out double s)
                {
                    var r = x - eye;
                    float alpha = Vector3.Dot(Vector3.Cross(r, pl.Rb), pl.N) / pl.Det;
                    float beta = Vector3.Dot(Vector3.Cross(pl.Ra, r), pl.N) / pl.Det;
                    s = alpha + beta > 1e-9f ? beta / (alpha + beta) : 0;
                    if (alpha + beta <= 1e-9f) return false;
                    if (alpha >= -1e-5f && beta >= -1e-5f) return true;
                    return (back && beta < 0 && alpha > 0) || (forward && alpha < 0 && beta > 0); // past A / past B
                }
                for (int j = 0; j < k; j++)
                {
                    var pj = m.Positions[verts[j]];
                    if (MathF.Abs(dist[j]) < PlaneEpsilon)
                    {
                        if (Within(pj, out var s)) crossings.Add((i + s, j, true, 0, pj));
                        continue;
                    }
                    int j1 = (j + 1) % k;
                    if (MathF.Abs(dist[j1]) < PlaneEpsilon || (dist[j] > 0) == (dist[j1] > 0)) continue;
                    float t = dist[j] / (dist[j] - dist[j1]);
                    var x = Vector3.Lerp(pj, m.Positions[verts[j1]], t);
                    if (Within(x, out var sx)) crossings.Add((i + sx, j, false, t, x));
                }
            }
            // Each extension stops at the first border it meets.
            const double Eps = 1e-6;
            double lastG = planes.Length;
            if (crossings.Any(x => x.G < -Eps))
            {
                double nearest = crossings.Where(x => x.G < -Eps).Max(x => x.G);
                crossings.RemoveAll(x => x.G < nearest - Eps);
            }
            if (crossings.Any(x => x.G > lastG + Eps))
            {
                double nearest = crossings.Where(x => x.G > lastG + Eps).Min(x => x.G);
                crossings.RemoveAll(x => x.G > nearest + Eps);
            }
            if (crossings.Count < 2) continue;
            crossings.Sort((a, b) => a.G.CompareTo(b.G));
            for (int c = crossings.Count - 1; c > 0; c--)
                if (Vector3.DistanceSquared(crossings[c].P, crossings[c - 1].P) < 1e-10f) crossings.RemoveAt(c);

            var chords = new List<(int Entry, List<Vector3> Inner, int Exit)>();
            for (int c = 0; c + 1 < crossings.Count; c++)
            {
                var (g0, g1) = (crossings[c].G, crossings[c + 1].G);
                var inner = new List<Vector3>();
                bool ok = true;
                for (int p = Math.Max((int)Math.Floor(g0) + 1, 0); p < g1 - Eps && p < path.Count && ok; p++)
                {
                    if (p <= g0 + Eps) continue;
                    // An extended end point sits on the straight run to the border: no vertex there.
                    if ((p == 0 && g0 < -Eps) || (p == path.Count - 1 && g1 > lastG + Eps)) continue;
                    if (Project(path[p]) is { } q) inner.Add(q); else ok = false;
                }
                if (!ok) continue;
                var first = inner.Count > 0 ? inner[0] : crossings[c + 1].P;
                var mid = To2((crossings[c].P + first) * 0.5f);
                if (!PointInPolygon(mid, poly) || DistanceToBorder(mid, poly) < 1e-5f) continue;
                chords.Add((c, inner, c + 1));
            }
            if (chords.Count == 0) continue;

            // Materialize the used crossings and build the face loop with them in place.
            var vertexOf = new Dictionary<int, int>();
            int CrossingVertex(int c)
            {
                if (vertexOf.TryGetValue(c, out var v)) return v;
                var x = crossings[c];
                v = x.AtVertex ? verts[x.Corner] : SplitVertex(verts[x.Corner], verts[(x.Corner + 1) % k], x.T);
                return vertexOf[c] = v;
            }
            var resolved = chords.Select(ch => (Entry: CrossingVertex(ch.Entry), ch.Inner, Exit: CrossingVertex(ch.Exit))).ToList();
            var loop = new List<int>();
            for (int j = 0; j < k; j++)
            {
                loop.Add(verts[j]);
                foreach (var c in vertexOf.Keys.Where(c => !crossings[c].AtVertex && crossings[c].Corner == j).OrderBy(c => crossings[c].T))
                    if (!loop.Contains(vertexOf[c])) loop.Add(vertexOf[c]);
            }

            var loops = new List<List<int>> { loop };
            foreach (var (entry, inner, exit) in resolved)
            {
                if (entry == exit) continue;
                int host = loops.FindIndex(l => l.Contains(entry) && l.Contains(exit));
                if (host < 0) continue;
                var l = loops[host];
                int ie = l.IndexOf(entry), ix = l.IndexOf(exit);
                if (inner.Count == 0 && ((ie + 1) % l.Count == ix || (ix + 1) % l.Count == ie)) continue; // already an edge
                var innerIds = inner.Select(p => { m.Positions.Add(p); return m.Positions.Count - 1; }).ToList();
                var a = Cycle(l, ie, ix).Concat(Enumerable.Reverse(innerIds)).ToList();
                var b = Cycle(l, ix, ie).Concat(innerIds).ToList();
                if (a.Count < 3 || b.Count < 3) continue;
                loops[host] = a;
                loops.Add(b);
                var chain = new List<int> { entry };
                chain.AddRange(innerIds);
                chain.Add(exit);
                for (int i = 0; i + 1 < chain.Count; i++) result.CutEdges.Add(new Edge(chain[i], chain[i + 1]));
            }
            if (loops.Count > 1) pieces.Add((f, loops, face.Clone(), verts.Select(v => m.Positions[v]).ToArray()));
        }

        var written = new List<(List<int> Faces, Face Original, Vector3[] Corners)>();
        foreach (var (f, loops, original, corners) in pieces)
        {
            var indices = new List<int>();
            for (int i = 0; i < loops.Count; i++)
            {
                var piece = original.Derive(loops[i].ToArray());
                piece.Dominant = original.Dominant;
                if (i == 0) { m.Faces[f] = piece; indices.Add(f); }
                else { indices.Add(m.Faces.Count); result.NewFaces.Add(m.Faces.Count); m.Faces.Add(piece); }
            }
            written.Add((indices, original, corners));
        }

        InsertEdgeSplits(m, splits, splitOf);

        // Explicit UVs: original corners keep theirs, new corners are interpolated on the original face.
        foreach (var (indices, original, corners) in written)
        {
            if (original.UVs is not { } uvs) continue;
            var known = new Dictionary<int, Vector2>();
            for (int i = 0; i < original.Verts.Length; i++) known.TryAdd(original.Verts[i], uvs[i]);
            foreach (int pf in indices)
                m.Faces[pf].UVs = m.Faces[pf].Verts.Select(v => known.TryGetValue(v, out var uv) ? uv : InterpolateUV(corners, uvs, m.Positions[v])).ToArray();
        }

        // Soft edges continue across their new vertices.
        foreach (var (e, list) in splits)
        {
            if (!m.SoftEdges.Remove(e)) continue;
            var chain = list.OrderBy(s => s.T).Select(s => s.V).Prepend(e.A).Append(e.B).ToList();
            for (int i = 0; i + 1 < chain.Count; i++) m.SoftEdges.Add(new Edge(chain[i], chain[i + 1]));
        }
        return result;
    }

    /// <summary>Inserts vertices added on original edges into every face loop that runs along that edge (no T-junctions).</summary>
    static void InsertEdgeSplits(PolyMesh m, Dictionary<Edge, List<(float T, int V)>> splits, Dictionary<int, (Edge E, float T)> splitOf)
    {
        if (splits.Count == 0) return;
        foreach (var face in m.Faces)
        {
            var v = face.Verts;
            List<int>? loop = null;
            List<Vector2>? uvLoop = null;
            for (int i = 0; i < v.Length; i++)
            {
                int a = v[i], b = v[(i + 1) % v.Length];
                var e = splitOf.TryGetValue(a, out var sa) ? sa.E : splitOf.TryGetValue(b, out var sb) ? sb.E : new Edge(a, b);
                float? T(int x) => x == e.A ? 0 : x == e.B ? 1 : splitOf.TryGetValue(x, out var s) && s.E == e ? s.T : null;
                var extra = new List<(float, int)>();
                if (splits.TryGetValue(e, out var list) && T(a) is { } ta && T(b) is { } tb)
                {
                    float lo = MathF.Min(ta, tb), hi = MathF.Max(ta, tb);
                    extra = list.Where(s => s.T > lo + 1e-6f && s.T < hi - 1e-6f && !v.Contains(s.V)).ToList();
                    extra = ta < tb ? extra.OrderBy(s => s.Item1).ToList() : extra.OrderByDescending(s => s.Item1).ToList();
                }
                if (extra.Count > 0 && loop == null)
                {
                    loop = v.Take(i).ToList();
                    uvLoop = face.UVs?.Take(i).ToList();
                }
                if (loop == null) continue;
                loop.Add(a);
                uvLoop?.Add(face.UVs![i]);
                foreach (var (t, x) in extra)
                {
                    loop.Add(x);
                    if (uvLoop != null)
                    {
                        float ta2 = T(a)!.Value, tb2 = T(b)!.Value;
                        uvLoop.Add(Vector2.Lerp(face.UVs![i], face.UVs[(i + 1) % v.Length], (t - ta2) / (tb2 - ta2)));
                    }
                }
            }
            if (loop == null) continue;
            face.Verts = loop.ToArray();
            if (uvLoop != null) face.UVs = uvLoop.ToArray();
        }
    }

    /// <summary>Loop entries from index <paramref name="from"/> forward to <paramref name="to"/>, inclusive, wrapping.</summary>
    static IEnumerable<int> Cycle(List<int> loop, int from, int to)
    {
        for (int i = from; ; i = (i + 1) % loop.Count)
        {
            yield return loop[i];
            if (i == to) yield break;
        }
    }

    static float DistanceToBorder(Vector2 p, IReadOnlyList<Vector2> poly)
    {
        float best = float.MaxValue;
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i]; var b = poly[(i + 1) % poly.Count];
            var ab = b - a;
            float t = ab.LengthSquared() < 1e-12f ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
            best = MathF.Min(best, Vector2.Distance(p, a + ab * t));
        }
        return best;
    }
}
