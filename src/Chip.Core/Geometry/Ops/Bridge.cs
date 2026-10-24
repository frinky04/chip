using System.Numerics;

namespace Chip.Core.Geometry;

public static partial class MeshOps
{
    /// <summary>
    /// Bridges two faces of one mesh: both are removed and their outlines are joined with quads. The faces need the
    /// same corner count and must not touch. Corners pair up by shape (relative to each face's centre), so two facing
    /// openings join straight across. The two removed slots are reused, so other face indices don't move. Returns the
    /// new faces, or null with a reason.
    /// </summary>
    public static List<int>? BridgeFaces(PolyMesh m, int fa, int fb, out string? error)
    {
        error = null;
        var a = m.Faces[fa].Verts;
        var b = m.Faces[fb].Verts;
        if (fa == fb || a.Intersect(b).Any()) { error = "Bridge needs two separate faces that don't share a corner"; return null; }
        if (a.Length != b.Length) { error = $"Bridge needs faces with the same number of corners ({a.Length} and {b.Length})"; return null; }
        int n = a.Length;
        var ca = m.FaceCenter(fa);
        var cb = m.FaceCenter(fb);

        // The loops run opposite ways once joined (each keeps its own winding), so pair a[i] with b[off - i].
        int best = 0;
        float bestCost = float.MaxValue;
        for (int off = 0; off < n; off++)
        {
            float cost = 0;
            for (int i = 0; i < n; i++)
                cost += Vector3.DistanceSquared(m.Positions[a[i]] - ca, m.Positions[b[((off - i) % n + n) % n]] - cb);
            if (cost < bestCost) { bestCost = cost; best = off; }
        }
        int J(int i) => b[((best - i) % n + n) % n];

        var src = m.Faces[fa];
        var quads = Enumerable.Range(0, n)
            .Select(i => src.Derive([a[i], a[(i + 1) % n], J(i + 1), J(i)]))
            .ToList();
        var result = new List<int>();
        m.Faces[fa] = quads[0];
        result.Add(fa);
        m.Faces[fb] = quads[1];
        result.Add(fb);
        foreach (var q in quads.Skip(2))
        {
            result.Add(m.Faces.Count);
            m.Faces.Add(q);
        }
        AlignToGrid(m, result, src.Map);
        return result;
    }

    /// <summary>
    /// Bridges open edge chains or loops (boundary edges, i.e. used by one face). The selection can hold any number of
    /// chains: they pair up two by two, each with the partner it bridges to most directly without a flat or twisted
    /// face. Chain ends that sit on top of each other (pieces split without sharing a vertex) count as one corner and
    /// are welded. A pair with the same edge count joins with one quad per edge; otherwise corners match by how far
    /// along each chain they sit, with a triangle wherever only one side has a corner. Open chains pair end to end
    /// following the surface winding; closed loops pair by shape. Returns the new faces, or null with a reason.
    /// </summary>
    public static EdgeBridgeResult? BridgeEdges(PolyMesh m, IEnumerable<Edge> edges, out string? error)
    {
        error = null;
        var edgeFaces = Topology.EdgeFaces(m);
        var list = edges.Distinct().ToList();
        if (list.Count < 2 || list.Any(e => !edgeFaces.TryGetValue(e, out var fs) || fs.Count != 1))
        {
            error = "Bridge needs two or more chains of open (boundary) edges";
            return null;
        }
        var weld = CoincidentEnds(m, list);

        // Direct each edge the way its face runs; the bridge runs the other way along it.
        var next = new Dictionary<int, int>();
        var faceOf = new Dictionary<int, int>();
        var directed = new HashSet<Edge>();
        foreach (var e in list)
        {
            int f = edgeFaces[e][0];
            var v = m.Faces[f].Verts;
            int i = Array.IndexOf(v, e.A);
            bool forward = v[(i + 1) % v.Length] == e.B;
            int from = weld[forward ? e.A : e.B], to = weld[forward ? e.B : e.A];
            if (from == to) { error = "Bridge can't use edges shorter than 1 mm"; return null; }
            if (!directed.Add(new Edge(from, to))) { error = "Two selected edges lie on top of each other: weld that seam first (Ctrl+Shift+Alt+M)"; return null; }
            if (!next.TryAdd(from, to)) { error = "Bridge needs simple edge chains (no branches)"; return null; }
            faceOf[from] = f;
        }

        var chains = new List<BridgeChain>();
        var used = new HashSet<int>();
        var targets = next.Values.ToHashSet();
        // Open chains start where nothing leads in; whatever remains is closed loops.
        foreach (var start in next.Keys.Where(k => !targets.Contains(k)).Concat(next.Keys).ToList())
        {
            if (used.Contains(start)) continue;
            var verts = new List<int> { start };
            used.Add(start);
            int cur = start;
            bool closed = false;
            while (next.TryGetValue(cur, out int to))
            {
                if (to == start) { closed = true; break; }
                if (!used.Add(to)) { error = "Bridge needs simple edge chains (no branches)"; return null; }
                verts.Add(to);
                cur = to;
            }
            chains.Add(new BridgeChain(verts, closed));
        }
        if (chains.Count < 2) { error = "Bridge needs at least two edge chains (these edges form one connected chain)"; return null; }

        // Every workable pairing, shortest first; each chain takes the first partner still free.
        var plans = new List<(int A, int B, BridgePlan Plan)>();
        for (int a = 0; a < chains.Count; a++)
            for (int b = a + 1; b < chains.Count; b++)
                if (PlanBridge(m, chains[a], chains[b]) is { } plan) plans.Add((a, b, plan));
        var paired = new HashSet<int>();
        var chosen = new List<(int A, BridgePlan Plan)>();
        foreach (var (a, b, plan) in plans.OrderBy(p => p.Plan.Cost))
            if (!paired.Contains(a) && !paired.Contains(b))
            {
                paired.Add(a);
                paired.Add(b);
                chosen.Add((a, plan));
            }
        if (paired.Count != chains.Count)
        {
            int left = chains.Count - paired.Count;
            error = chains.Count > 2
                ? $"Bridge could pair {paired.Count} of {chains.Count} chains: {(left == 1 ? "1 chain has" : $"{left} chains have")} nothing to bridge to"
                : chains[0].Closed != chains[1].Closed
                    ? "Bridge can't join a closed loop to an open chain"
                    : "These chains would bridge with a twisted or flat face (is one side flipped?)";
            return null;
        }

        var result = new List<int>();
        foreach (var (a, plan) in chosen)
        {
            var src = m.Faces[faceOf[chains[a].Verts[0]]];
            int first = m.Faces.Count;
            foreach (var f in plan.Faces) m.Faces.Add(src.Derive(f));
            var made = Enumerable.Range(first, plan.Faces.Count).ToList();
            AlignToGrid(m, made, src.Map);
            result.AddRange(made);
        }
        if (weld.Where((w, v) => w != v).Any())
        {
            var (vmap, fmap) = MergeVertices(m, weld);
            return new EdgeBridgeResult(result.Select(f => fmap[f]).Where(f => f >= 0).ToList(), chosen.Count, vmap, fmap);
        }
        return new EdgeBridgeResult(result, chosen.Count);
    }

    /// <summary>Maps each end of <paramref name="edges"/> onto the first end within <see cref="WeldDistance"/> of it (everything else maps to itself).</summary>
    static int[] CoincidentEnds(PolyMesh m, List<Edge> edges)
    {
        var weld = Enumerable.Range(0, m.Positions.Count).ToArray();
        var reps = new List<int>();
        foreach (var v in edges.SelectMany(e => new[] { e.A, e.B }).Distinct())
        {
            int rep = reps.FindIndex(r => Vector3.DistanceSquared(m.Positions[r], m.Positions[v]) <= WeldDistance * WeldDistance);
            if (rep >= 0) weld[v] = reps[rep];
            else reps.Add(v);
        }
        return weld;
    }

    /// <summary>
    /// The faces joining two chains and their cost (mean rung length), or null when they can't pair: one is closed and
    /// the other open, they touch, or a face would come out flat or twisted (the chains run the same way, so one side
    /// is flipped).
    /// </summary>
    static BridgePlan? PlanBridge(PolyMesh m, BridgeChain a, BridgeChain b)
    {
        if (a.Closed != b.Closed || a.Verts.Intersect(b.Verts).Any()) return null;
        var sa = new List<int>(a.Verts);
        List<int> sb;
        if (!a.Closed) sb = Enumerable.Reverse(b.Verts).ToList();
        else
        {
            // Loops run opposite ways once joined, so walk b backwards from the corner that best matches a's first.
            int n = b.Verts.Count;
            var centreA = Centre(m, a.Verts);
            var centreB = Centre(m, b.Verts);
            Vector3 RelA(int i) => m.Positions[sa[i]] - centreA;
            Vector3 RelB(int i) => m.Positions[b.Verts[((i % n) + n) % n]] - centreB;
            int best = 0;
            float bestCost = float.MaxValue;
            for (int off = 0; off < n; off++)
            {
                float cost = 0;
                if (n == sa.Count)
                    for (int i = 0; i < n; i++) cost += Vector3.DistanceSquared(RelA(i), RelB(off - i));
                else cost = Vector3.DistanceSquared(RelA(0), RelB(off));
                if (cost < bestCost) { bestCost = cost; best = off; }
            }
            sb = Enumerable.Range(0, n).Select(i => b.Verts[((best - i) % n + n) % n]).ToList();
            sa.Add(sa[0]);
            sb.Add(sb[0]);
        }

        int ka = sa.Count - 1, kb = sb.Count - 1;
        var ta = AlongChain(m, sa);
        var tb = AlongChain(m, sb);
        var faces = new List<int[]>();
        float rungs = Vector3.Distance(m.Positions[sa[0]], m.Positions[sb[0]]);
        int ia = 0, ib = 0;
        while (ia < ka || ib < kb)
        {
            bool stepA = true, stepB = true;
            if (ia == ka) stepA = false;
            else if (ib == kb) stepB = false;
            else if (ka != kb)
            {
                // Step whichever way leaves the next rung most level across the two chains.
                float both = MathF.Abs(ta[ia + 1] - tb[ib + 1]), onlyA = MathF.Abs(ta[ia + 1] - tb[ib]), onlyB = MathF.Abs(ta[ia] - tb[ib + 1]);
                if (both > MathF.Min(onlyA, onlyB)) (stepA, stepB) = (onlyA <= onlyB, onlyA > onlyB);
            }
            int[] f = (stepA, stepB) switch
            {
                (true, true) => [sa[ia + 1], sa[ia], sb[ib], sb[ib + 1]],
                (true, false) => [sa[ia + 1], sa[ia], sb[ib]],
                _ => [sa[ia], sb[ib], sb[ib + 1]],
            };
            if (!Sound(m, f)) return null;
            faces.Add(f);
            if (stepA) ia++;
            if (stepB) ib++;
            rungs += Vector3.Distance(m.Positions[sa[ia]], m.Positions[sb[ib]]);
        }
        return new BridgePlan(faces, rungs / (faces.Count + 1));
    }

    static Vector3 Centre(PolyMesh m, List<int> verts) => verts.Aggregate(Vector3.Zero, (s, v) => s + m.Positions[v]) / verts.Count;

    /// <summary>How far along the chain each corner sits, from 0 at the first to 1 at the last.</summary>
    static float[] AlongChain(PolyMesh m, List<int> verts)
    {
        var t = new float[verts.Count];
        for (int i = 1; i < verts.Count; i++) t[i] = t[i - 1] + Vector3.Distance(m.Positions[verts[i - 1]], m.Positions[verts[i]]);
        float total = t[^1];
        for (int i = 1; i < t.Length; i++) t[i] = total > 0 ? t[i] / total : i / (float)(t.Length - 1);
        return t;
    }

    /// <summary>A bridge face with real area that doesn't cross itself: a quad must split into two halves facing the same way along at least one diagonal.</summary>
    static bool Sound(PolyMesh m, int[] f)
    {
        const float minArea = WeldDistance * WeldDistance;
        var p = f.Select(v => m.Positions[v]).ToArray();
        Vector3 Tri(int a, int b, int c) => Vector3.Cross(p[b] - p[a], p[c] - p[a]);
        if (p.Length == 3) return Tri(0, 1, 2).Length() > minArea;
        if ((Tri(0, 1, 2) + Tri(0, 2, 3)).Length() <= minArea) return false;
        return Vector3.Dot(Tri(0, 1, 2), Tri(0, 2, 3)) > 0 || Vector3.Dot(Tri(1, 2, 3), Tri(1, 3, 0)) > 0;
    }

    sealed record BridgeChain(List<int> Verts, bool Closed);
    sealed record BridgePlan(List<int[]> Faces, float Cost);
}

/// <summary>
/// What <see cref="MeshOps.BridgeEdges"/> made: the new faces and how many chain pairs they bridge. When touching chain
/// ends were welded, vertices and faces were renumbered and the maps say how (old → new, -1 = removed); otherwise null.
/// </summary>
public sealed record EdgeBridgeResult(List<int> Faces, int Bridges, int[]? VertexMap = null, int[]? FaceMap = null);
