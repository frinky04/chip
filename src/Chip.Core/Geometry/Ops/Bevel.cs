using System.Numerics;

namespace Chip.Core.Geometry;

public static partial class MeshOps
{
    /// <summary>
    /// Bevels edges: each edge becomes a band of <paramref name="segments"/> faces, <paramref name="width"/> metres in from
    /// the edge on both sides. One segment is a flat chamfer; more give a rounded profile (soft-shaded). Where bevelled
    /// edges meet, the corner is patched (one face when flat, a fan when curved), so a closed mesh stays closed.
    /// Only edges with two faces whose end vertices are fully surrounded by faces are bevelled; others are skipped.
    /// Existing faces keep index, material and mapping (explicit UVs are interpolated); new faces copy the material of an
    /// adjacent face and are world-aligned. Vertices are compacted. Returns the new face indices.
    /// </summary>
    public static List<int> BevelEdges(PolyMesh m, IEnumerable<Edge> edges, float width, int segments = 1) =>
        Bevel(m, edges.ToHashSet(), new HashSet<int>(), width, segments);

    /// <summary>Chamfers vertices: each corner is cut off <paramref name="width"/> metres along every edge and capped.</summary>
    public static List<int> BevelVertices(PolyMesh m, IEnumerable<int> verts, float width) =>
        Bevel(m, new HashSet<Edge>(), verts.ToHashSet(), width, 1);

    static List<int> Bevel(PolyMesh m, HashSet<Edge> sel, HashSet<int> cornerVerts, float width, int segments)
    {
        var added = new List<int>();
        if (width <= 1e-6f) return added;
        segments = Math.Clamp(segments, 1, 32);

        var edgeFaces = Topology.EdgeFaces(m);
        var vertexEdges = new Dictionary<int, List<Edge>>();
        foreach (var e in edgeFaces.Keys)
        {
            (vertexEdges.TryGetValue(e.A, out var la) ? la : vertexEdges[e.A] = new List<Edge>()).Add(e);
            (vertexEdges.TryGetValue(e.B, out var lb) ? lb : vertexEdges[e.B] = new List<Edge>()).Add(e);
        }
        bool Interior(int v) => vertexEdges.TryGetValue(v, out var list) && list.All(e => edgeFaces[e].Count == 2);
        sel = sel.Where(e => edgeFaces.TryGetValue(e, out var fs) && fs.Count == 2 && Interior(e.A) && Interior(e.B)).ToHashSet();
        var edgeVerts = sel.SelectMany(e => new[] { e.A, e.B }).ToHashSet();
        cornerVerts = cornerVerts.Where(v => Interior(v) && !edgeVerts.Contains(v)).ToHashSet();
        var bevelled = edgeVerts.Union(cornerVerts).ToHashSet();
        if (bevelled.Count == 0) return added;

        var P = new List<Vector3>(m.Positions);
        int originalCount = P.Count;
        var owner = new Dictionary<int, int>(); // new vertex -> the bevelled vertex it came from
        int NewVertex(Vector3 p, int from)
        {
            m.Positions.Add(p);
            owner[m.Positions.Count - 1] = from;
            return m.Positions.Count - 1;
        }
        float SlideLimit(int v, Edge e)
        {
            int other = e.A == v ? e.B : e.A;
            return Vector3.Distance(P[v], P[other]) * (bevelled.Contains(other) ? 0.49f : 0.9f);
        }

        // 1. Where each corner of a bevelled vertex moves: onto an unbevelled edge (shared by the faces on that edge),
        //    or into the face where two bevelled edges meet.
        var slides = new Dictionary<(int V, Edge E), (Vector3 Sum, int Count)>();
        void Slide(int v, Edge e, Vector3 dir, float dist)
        {
            var p = P[v] + dir * MathF.Min(dist, SlideLimit(v, e));
            var s = slides.GetValueOrDefault((v, e));
            slides[(v, e)] = (s.Sum + p, s.Count + 1);
        }
        var insets = new Dictionary<(int F, int J), Vector3>();
        for (int f = 0; f < m.Faces.Count; f++)
        {
            var V = m.Faces[f].Verts;
            int k = V.Length;
            var N = m.FaceNormal(f);
            for (int j = 0; j < k; j++)
            {
                int v = V[j];
                if (!bevelled.Contains(v)) continue;
                int prev = V[(j + k - 1) % k], next = V[(j + 1) % k];
                Edge ein = new(prev, v), eout = new(v, next);
                var din = SafeNormalize(P[prev] - P[v]);
                var dout = SafeNormalize(P[next] - P[v]);
                if (cornerVerts.Contains(v))
                {
                    Slide(v, ein, din, width);
                    Slide(v, eout, dout, width);
                    continue;
                }
                bool si = sel.Contains(ein), so = sel.Contains(eout);
                if (!si && !so) continue;
                var inIn = SafeNormalize(-Vector3.Cross(N, din)); // into the face from e_in
                var inOut = SafeNormalize(Vector3.Cross(N, dout)); // into the face from e_out
                float a = Vector3.Dot(din, inOut), b = Vector3.Dot(dout, inIn);
                if (si && so)
                    insets[(f, j)] = MathF.Abs(a) < 1e-4f || MathF.Abs(b) < 1e-4f
                        ? P[v] + SafeNormalize(inIn + inOut) * width
                        : P[v] + din * (width / a) + dout * (width / b);
                else if (si) Slide(v, eout, dout, b > 1e-4f ? width / b : float.MaxValue);
                else Slide(v, ein, din, a > 1e-4f ? width / a : float.MaxValue);
            }
        }
        var slideVertex = slides.ToDictionary(kv => kv.Key, kv => NewVertex(kv.Value.Sum / kv.Value.Count, kv.Key.V));
        var insetVertex = insets.ToDictionary(kv => kv.Key, kv => NewVertex(kv.Value, m.Faces[kv.Key.F].Verts[kv.Key.J]));

        // 2. Rebuild every face with its bevelled corners replaced.
        var seqs = new Dictionary<(int F, int J), int[]>();
        var originalLoops = m.Faces.Select(f => f.Verts).ToArray(); // rebuilt faces get new arrays
        int faceCount = m.Faces.Count;
        for (int f = 0; f < faceCount; f++)
        {
            var face = m.Faces[f];
            var V = face.Verts;
            int k = V.Length;
            if (!V.Any(bevelled.Contains)) continue;
            var loop = new List<int>();
            for (int j = 0; j < k; j++)
            {
                int v = V[j];
                if (!bevelled.Contains(v)) { loop.Add(v); continue; }
                int prev = V[(j + k - 1) % k], next = V[(j + 1) % k];
                Edge ein = new(prev, v), eout = new(v, next);
                bool si = sel.Contains(ein), so = sel.Contains(eout);
                int[] seq;
                if (si && so) seq = [insetVertex[(f, j)]];
                else if (si) seq = [slideVertex[(v, eout)]];
                else if (so) seq = [slideVertex[(v, ein)]];
                else
                {
                    int x = slideVertex.TryGetValue((v, ein), out var sx) ? sx : v;
                    int y = slideVertex.TryGetValue((v, eout), out var sy) ? sy : v;
                    seq = x == y ? [x] : [x, y];
                }
                seqs[(f, j)] = seq;
                loop.AddRange(seq);
            }
            if (face.UVs is { } uvs)
            {
                var corners = V.Select(i => P[i]).ToArray();
                var known = new Dictionary<int, Vector2>();
                for (int j = 0; j < k; j++) known.TryAdd(V[j], uvs[j]);
                face.UVs = loop.Select(v => known.TryGetValue(v, out var uv) ? uv : InterpolateUV(corners, uvs, m.Positions[v])).ToArray();
            }
            face.Verts = loop.ToArray();
        }

        // 3. A band of quads along each bevelled edge, between the corners its two faces moved to.
        var arcCenters = new Dictionary<int, List<Vector3>>();
        int[] Profile(int v, int other, int from, int to)
        {
            var p0 = m.Positions[from]; var p1 = m.Positions[to];
            var axis = SafeNormalize(P[other] - P[v]);
            var mid = (p0 + p1) * 0.5f;
            var corner = P[v] + axis * Vector3.Dot(mid - P[v], axis);
            var center = p0 + p1 - corner;
            (arcCenters.TryGetValue(v, out var list) ? list : arcCenters[v] = new List<Vector3>()).Add(center);
            var ids = new int[segments + 1];
            ids[0] = from;
            ids[segments] = to;
            for (int s = 1; s < segments; s++)
            {
                float th = s * MathF.PI * 0.5f / segments;
                ids[s] = NewVertex(center + (p0 - center) * MathF.Cos(th) + (p1 - center) * MathF.Sin(th), v);
            }
            return ids;
        }
        foreach (var e in sel)
        {
            var fs = edgeFaces[e];
            int f1 = -1, f2 = -1, a1 = 0, b1 = 0, a2 = 0, b2 = 0;
            foreach (int f in fs)
            {
                var V = originalLoops[f];
                int ia = Array.IndexOf(V, e.A), ib = Array.IndexOf(V, e.B);
                if ((ia + 1) % V.Length == ib) { f1 = f; a1 = ia; b1 = ib; }
                else { f2 = f; a2 = ia; b2 = ib; }
            }
            if (f1 < 0 || f2 < 0) continue; // inconsistent winding
            int p1a = seqs[(f1, a1)][^1], p1b = seqs[(f1, b1)][0];
            int p2b = seqs[(f2, b2)][^1], p2a = seqs[(f2, a2)][0];
            var A = Profile(e.A, e.B, p1a, p2a);
            var B = Profile(e.B, e.A, p1b, p2b);
            var src = m.Faces[f1];
            for (int s = 0; s < segments; s++)
            {
                added.Add(m.Faces.Count);
                m.Faces.Add(new Face([B[s], A[s], A[s + 1], B[s + 1]], src.Material, src.Map));
                if (s > 0) m.SoftEdges.Add(new Edge(A[s], B[s]));
            }
        }

        // 4. Close what is left open around each bevelled vertex.
        foreach (var f in FillBevelHoles(m, originalCount, owner, P, arcCenters, segments > 1)) added.Add(f);
        AlignToGrid(m, added.Where(f => f < m.Faces.Count));
        RemoveUnusedVertices(m);
        return added.Where(f => f < m.Faces.Count).ToList();
    }

    /// <summary>
    /// Caps open loops that touch new vertices (index &gt;= <paramref name="originalCount"/>), so pre-existing open
    /// borders are left alone. A flat loop becomes one face, merged into a coplanar neighbour when they share one edge
    /// (the rounded end of a bevel on an end face); a curved loop becomes a fan around a point on the rounded corner.
    /// </summary>
    static List<int> FillBevelHoles(PolyMesh m, int originalCount, Dictionary<int, int> owner, List<Vector3> original,
        Dictionary<int, List<Vector3>> arcCenters, bool smooth)
    {
        var added = new List<int>();
        var directed = new Dictionary<(int, int), int>();
        for (int f = 0; f < m.Faces.Count; f++)
        {
            var v = m.Faces[f].Verts;
            for (int i = 0; i < v.Length; i++) directed.TryAdd((v[i], v[(i + 1) % v.Length]), f);
        }
        var next = new Dictionary<int, int>();
        var source = new Dictionary<int, int>();
        foreach (var ((a, b), f) in directed)
        {
            if (directed.ContainsKey((b, a)) || (a < originalCount && b < originalCount)) continue;
            next[b] = a;
            source[b] = f;
        }

        foreach (var loop in ChainLoops(next))
        {
            var srcFace = m.Faces[source[loop[0]]];
            var pts = loop.Select(v => m.Positions[v]).ToList();
            var normal = LoopNormal(pts);
            var centroid = pts.Aggregate(Vector3.Zero, (s, p) => s + p) / pts.Count;
            float extent = pts.Max(p => Vector3.Distance(p, centroid));
            float deviation = pts.Max(p => MathF.Abs(Vector3.Dot(p - centroid, normal)));
            if (deviation <= 1e-4f * MathF.Max(1, extent))
            {
                if (TryMergeCoplanar(m, loop, normal, directed)) continue;
                added.Add(m.Faces.Count);
                m.Faces.Add(new Face(loop.ToArray(), srcFace.Material, srcFace.Map));
                continue;
            }

            var center = centroid;
            int from = loop.Where(owner.ContainsKey).Select(v => owner[v]).DefaultIfEmpty(-1).First();
            if (from >= 0 && arcCenters.TryGetValue(from, out var centers))
            {
                var c = centers.Aggregate(Vector3.Zero, (s, p) => s + p) / centers.Count;
                float r = pts.Average(p => Vector3.Distance(p, c));
                var dir = SafeNormalize(original[from] - c);
                if (dir != Vector3.Zero) center = c + dir * r;
            }
            int ci = m.Positions.Count;
            m.Positions.Add(center);
            for (int i = 0; i < loop.Count; i++)
            {
                added.Add(m.Faces.Count);
                m.Faces.Add(new Face([loop[i], loop[(i + 1) % loop.Count], ci], srcFace.Material, srcFace.Map));
                if (smooth) m.SoftEdges.Add(new Edge(loop[i], ci));
            }
        }
        return added;
    }

    /// <summary>Folds a flat cap into the neighbour across exactly one of its edges when that neighbour is coplanar.</summary>
    static bool TryMergeCoplanar(PolyMesh m, List<int> cap, Vector3 capNormal, Dictionary<(int, int), int> directed)
    {
        int n = cap.Count;
        var shared = new Dictionary<int, List<int>>();
        for (int i = 0; i < n; i++)
            if (directed.TryGetValue((cap[(i + 1) % n], cap[i]), out var nf))
                (shared.TryGetValue(nf, out var l) ? l : shared[nf] = new List<int>()).Add(i);
        foreach (var (nf, edges) in shared)
        {
            if (edges.Count != 1) continue;
            var face = m.Faces[nf];
            var nn = m.FaceNormal(nf);
            var p0 = m.Positions[face.Verts[0]];
            if (Vector3.Dot(nn, capNormal) < 0.9999f || cap.Any(v => MathF.Abs(Vector3.Dot(m.Positions[v] - p0, nn)) > 1e-4f)) continue;
            int i = edges[0];
            int x = cap[i], y = cap[(i + 1) % n];
            var nv = face.Verts.ToList();
            var merged = Cycle(nv, nv.IndexOf(x), nv.IndexOf(y)).Concat(Cycle(cap, (i + 1) % n, i).Skip(1).SkipLast(1)).ToArray();
            if (face.UVs is { } uvs)
            {
                var corners = face.Verts.Select(v => m.Positions[v]).ToArray();
                var known = new Dictionary<int, Vector2>();
                for (int j = 0; j < face.Verts.Length; j++) known.TryAdd(face.Verts[j], uvs[j]);
                face.UVs = merged.Select(v => known.TryGetValue(v, out var uv) ? uv : InterpolateUV(corners, uvs, m.Positions[v])).ToArray();
            }
            face.Verts = merged;
            return true;
        }
        return false;
    }

    static Vector3 LoopNormal(IReadOnlyList<Vector3> pts)
    {
        var n = Vector3.Zero;
        for (int i = 0; i < pts.Count; i++)
        {
            var a = pts[i]; var b = pts[(i + 1) % pts.Count];
            n.X += (a.Y - b.Y) * (a.Z + b.Z);
            n.Y += (a.Z - b.Z) * (a.X + b.X);
            n.Z += (a.X - b.X) * (a.Y + b.Y);
        }
        return SafeNormalize(n);
    }

}
