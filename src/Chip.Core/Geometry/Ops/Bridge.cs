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
            .Select(i => new Face([a[i], a[(i + 1) % n], J(i + 1), J(i)], src.Material, src.Map))
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
    /// Bridges two open edge chains or loops (boundary edges, i.e. used by one face) with quads. Both need the same edge
    /// count. Open chains pair end to end following the surface winding; closed loops pair by shape. Returns the new
    /// faces, or null with a reason.
    /// </summary>
    public static List<int>? BridgeEdges(PolyMesh m, IEnumerable<Edge> edges, out string? error)
    {
        error = null;
        var edgeFaces = Topology.EdgeFaces(m);
        var list = edges.Distinct().ToList();
        if (list.Count < 2 || list.Any(e => !edgeFaces.TryGetValue(e, out var fs) || fs.Count != 1))
        {
            error = "Bridge needs two chains of open (boundary) edges";
            return null;
        }

        // Direct each edge the way its face runs; the bridge runs the other way along it.
        var next = new Dictionary<int, int>();
        var faceOf = new Dictionary<int, int>();
        foreach (var e in list)
        {
            int f = edgeFaces[e][0];
            var v = m.Faces[f].Verts;
            int i = Array.IndexOf(v, e.A);
            bool forward = v[(i + 1) % v.Length] == e.B;
            int from = forward ? e.A : e.B, to = forward ? e.B : e.A;
            if (!next.TryAdd(from, to)) { error = "Bridge needs simple edge chains (no branches)"; return null; }
            faceOf[from] = f;
        }

        var chains = new List<(List<int> Verts, bool Closed)>();
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
            chains.Add((verts, closed));
        }
        if (chains.Count != 2) { error = $"Bridge needs exactly two edge chains ({chains.Count} selected)"; return null; }
        var (ca, closedA) = chains[0];
        var (cb, closedB) = chains[1];
        int edgesA = closedA ? ca.Count : ca.Count - 1, edgesB = closedB ? cb.Count : cb.Count - 1;
        if (closedA != closedB || edgesA != edgesB)
        {
            error = $"Bridge needs two chains of the same length ({edgesA} and {edgesB} edges)";
            return null;
        }

        var quads = new List<int[]>();
        if (!closedA)
        {
            int k = edgesA;
            for (int i = 0; i < k; i++) quads.Add([ca[i + 1], ca[i], cb[k - i], cb[k - i - 1]]);
        }
        else
        {
            int n = ca.Count;
            var centreA = ca.Aggregate(Vector3.Zero, (s, v) => s + m.Positions[v]) / n;
            var centreB = cb.Aggregate(Vector3.Zero, (s, v) => s + m.Positions[v]) / n;
            int best = 0;
            float bestCost = float.MaxValue;
            for (int off = 0; off < n; off++)
            {
                float cost = 0;
                for (int i = 0; i < n; i++)
                    cost += Vector3.DistanceSquared(m.Positions[ca[i]] - centreA, m.Positions[cb[((off - i) % n + n) % n]] - centreB);
                if (cost < bestCost) { bestCost = cost; best = off; }
            }
            int J(int i) => cb[((best - i) % n + n) % n];
            for (int i = 0; i < n; i++) quads.Add([ca[(i + 1) % n], ca[i], J(i), J(i + 1)]);
        }

        var src = m.Faces[faceOf[ca[0]]];
        var result = new List<int>();
        foreach (var q in quads)
        {
            result.Add(m.Faces.Count);
            m.Faces.Add(new Face(q, src.Material, src.Map));
        }
        AlignToGrid(m, result, src.Map);
        return result;
    }
}
