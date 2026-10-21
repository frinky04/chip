using System.Numerics;

namespace Chip.Core.Geometry;

/// <summary>
/// Vertices an op places on existing mesh edges. They are looked up by position along the edge, so two faces cutting
/// the same edge at the same point share one vertex, and a vertex may sit on several edges (where a face crossed
/// itself). <see cref="InsertIntoFaces"/> then threads them into every face loop that runs along those edges, so the
/// mesh gets no T-junctions, and <see cref="SplitSoftEdges"/> keeps soft edges soft across them.
/// </summary>
internal sealed class EdgePoints(PolyMesh m)
{
    readonly Dictionary<Edge, List<(float T, int V)>> _on = new(); // t measured from Edge.A
    readonly Dictionary<int, List<(Edge E, float T)>> _of = new();

    public int Count => _of.Count;

    static float FromA(Edge e, int a, float t) => a == e.A ? t : 1 - t;

    /// <summary>Parameter tolerance on an edge: 0.01 mm, or 1e-5 of the edge on long ones.</summary>
    float Tolerance(Edge e)
    {
        float len = Vector3.Distance(m.Positions[e.A], m.Positions[e.B]);
        return len > 1e-12f ? MathF.Max(1e-5f, 1e-5f / len) : 1;
    }

    /// <summary>The vertex already at <paramref name="t"/> along a→b (an end, or one placed there before), or null.</summary>
    public int? Find(int a, int b, float t)
    {
        var e = new Edge(a, b);
        t = FromA(e, a, t);
        float tol = Tolerance(e);
        if (t <= tol) return e.A;
        if (t >= 1 - tol) return e.B;
        if (_on.TryGetValue(e, out var list))
            foreach (var (lt, lv) in list)
                if (MathF.Abs(lt - t) <= tol) return lv;
        return null;
    }

    /// <summary>The vertex at <paramref name="t"/> along a→b, added there if there isn't one yet.</summary>
    public int At(int a, int b, float t)
    {
        if (Find(a, b, t) is { } found) return found;
        int v = m.Positions.Count;
        m.Positions.Add(Vector3.Lerp(m.Positions[a], m.Positions[b], t));
        Register(v, a, b, t);
        return v;
    }

    /// <summary>Records that vertex <paramref name="v"/> lies at <paramref name="t"/> along a→b.</summary>
    public void Register(int v, int a, int b, float t)
    {
        var e = new Edge(a, b);
        if (v == e.A || v == e.B) return;
        if (!_on.TryGetValue(e, out var list)) _on[e] = list = new List<(float, int)>();
        if (list.Any(p => p.V == v)) return;
        t = FromA(e, a, t);
        list.Add((t, v));
        if (!_of.TryGetValue(v, out var of)) _of[v] = of = new List<(Edge, float)>();
        of.Add((e, t));
    }

    float? T(int x, Edge e)
    {
        if (x == e.A) return 0;
        if (x == e.B) return 1;
        if (_of.TryGetValue(x, out var of))
            foreach (var (oe, t) in of)
                if (oe == e) return t;
        return null;
    }

    /// <summary>
    /// The registered points strictly between a and b, ordered from a, when a→b runs along a registered edge (whole, or
    /// part of it between points already placed). <paramref name="ta"/> and <paramref name="tb"/> are a and b on it.
    /// </summary>
    List<(float T, int V)> Between(int a, int b, int[] loop, out float ta, out float tb)
    {
        ta = tb = 0;
        IEnumerable<Edge> candidates = [new Edge(a, b)];
        if (_of.TryGetValue(a, out var ofA)) candidates = candidates.Concat(ofA.Select(o => o.E));
        if (_of.TryGetValue(b, out var ofB)) candidates = candidates.Concat(ofB.Select(o => o.E));
        foreach (var e in candidates)
        {
            if (!_on.TryGetValue(e, out var list) || T(a, e) is not { } x || T(b, e) is not { } y || x == y) continue;
            ta = x; tb = y;
            float lo = MathF.Min(x, y), hi = MathF.Max(x, y);
            var extra = list.Where(p => p.T > lo + 1e-6f && p.T < hi - 1e-6f && !loop.Contains(p.V));
            return (x < y ? extra.OrderBy(p => p.T) : extra.OrderByDescending(p => p.T)).ToList();
        }
        return [];
    }

    /// <summary>Puts each registered vertex into every face loop running along its edge (explicit UVs interpolated).</summary>
    public void InsertIntoFaces()
    {
        if (_on.Count == 0) return;
        foreach (var face in m.Faces)
        {
            var v = face.Verts;
            List<int>? loop = null;
            List<Vector2>? uvLoop = null;
            for (int i = 0; i < v.Length; i++)
            {
                int j = (i + 1) % v.Length;
                var extra = Between(v[i], v[j], v, out float ta, out float tb);
                if (extra.Count > 0 && loop == null)
                {
                    loop = v.Take(i).ToList();
                    uvLoop = face.UVs?.Take(i).ToList();
                }
                if (loop == null) continue;
                loop.Add(v[i]);
                uvLoop?.Add(face.UVs![i]);
                foreach (var (t, x) in extra)
                {
                    loop.Add(x);
                    uvLoop?.Add(Vector2.Lerp(face.UVs![i], face.UVs[j], (t - ta) / (tb - ta)));
                }
            }
            if (loop == null) continue;
            face.Verts = loop.ToArray();
            if (uvLoop != null) face.UVs = uvLoop.ToArray();
        }
    }

    /// <summary>A soft edge that gained vertices stays soft along all its pieces.</summary>
    public void SplitSoftEdges()
    {
        foreach (var (e, list) in _on)
        {
            if (!m.SoftEdges.Remove(e)) continue;
            var chain = list.OrderBy(s => s.T).Select(s => s.V).Prepend(e.A).Append(e.B).ToList();
            for (int i = 0; i + 1 < chain.Count; i++) m.SoftEdges.Add(new Edge(chain[i], chain[i + 1]));
        }
    }
}
