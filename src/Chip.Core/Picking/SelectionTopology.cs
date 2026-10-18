using Chip.Core.Geometry;

namespace Chip.Core.Picking;

/// <summary>
/// Adjacency walks behind the selection power tools: flood fills, edge loops and rings, grow/shrink and shortest paths.
/// Build one per mesh per operation (it snapshots the adjacency), then query as often as needed. Works on any n-gon mesh,
/// including open borders and non-manifold edges; loops and rings simply stop where quad topology ends.
/// </summary>
public sealed class SelectionTopology
{
    readonly PolyMesh _m;
    readonly Dictionary<Edge, List<int>> _edgeFaces;
    readonly List<int>[] _vertexFaces;
    readonly List<int>[] _neighbors;

    public SelectionTopology(PolyMesh mesh)
    {
        _m = mesh;
        _edgeFaces = Topology.EdgeFaces(mesh);
        _vertexFaces = Topology.VertexFaces(mesh);
        var sets = new HashSet<int>[mesh.Positions.Count];
        for (int i = 0; i < sets.Length; i++) sets[i] = new HashSet<int>();
        foreach (var e in _edgeFaces.Keys)
        {
            sets[e.A].Add(e.B);
            sets[e.B].Add(e.A);
        }
        _neighbors = sets.Select(s => s.Order().ToList()).ToArray();
    }

    public PolyMesh Mesh => _m;

    /// <summary>Vertices joined to <paramref name="v"/> by an edge.</summary>
    public IReadOnlyList<int> Neighbors(int v) => _neighbors[v];

    /// <summary>Faces using <paramref name="e"/> (empty if the edge doesn't exist).</summary>
    public IReadOnlyList<int> FacesOf(Edge e) => _edgeFaces.TryGetValue(e, out var f) ? f : [];

    public bool IsBoundary(Edge e) => FacesOf(e).Count == 1;

    /// <summary>True if the vertex sits on an open border (touches an edge used by one face).</summary>
    public bool IsBoundaryVertex(int v) => _neighbors[v].Any(n => IsBoundary(new Edge(v, n)));

    /// <summary>Faces sharing an edge with <paramref name="face"/>.</summary>
    public IEnumerable<int> FaceNeighbors(int face) =>
        _m.Faces[face].Edges().SelectMany(FacesOf).Where(f => f != face).Distinct();

    // ---- Flood fills ----

    /// <summary>Faces reachable from <paramref name="seed"/> across shared edges, optionally only through faces <paramref name="accept"/> allows.</summary>
    public List<int> ConnectedFaces(int seed, Func<int, bool>? accept = null)
    {
        var seen = new HashSet<int> { seed };
        var order = new List<int> { seed };
        for (int i = 0; i < order.Count; i++)
            foreach (var n in FaceNeighbors(order[i]))
                if ((accept == null || accept(n)) && seen.Add(n)) order.Add(n);
        return order;
    }

    /// <summary>
    /// Connected faces lying in the seed face's plane: each face's normal is within <paramref name="degrees"/> of the seed's.
    /// Comparing against the seed (not the neighbour) keeps gentle curves from creeping across.
    /// </summary>
    public List<int> CoplanarFaces(int seed, float degrees = 1f)
    {
        var n0 = _m.FaceNormal(seed);
        float cos = MathF.Cos(degrees * MathF.PI / 180f);
        return ConnectedFaces(seed, f => System.Numerics.Vector3.Dot(_m.FaceNormal(f), n0) >= cos);
    }

    /// <summary>Vertices reachable from the seeds along edges.</summary>
    public HashSet<int> ConnectedVertices(IEnumerable<int> seeds)
    {
        var seen = new HashSet<int>(seeds);
        var queue = new Queue<int>(seen);
        while (queue.Count > 0)
            foreach (var n in _neighbors[queue.Dequeue()])
                if (seen.Add(n)) queue.Enqueue(n);
        return seen;
    }

    // ---- Loops and rings ----

    /// <summary>
    /// The edge loop through <paramref name="start"/>, in walk order. Through interior edges it continues straight across
    /// regular quad vertices (four edges, four quads) and stops at poles, n-gons and borders. A border edge follows the open
    /// border instead. Closed loops come back to the start and stop there.
    /// </summary>
    public List<Edge> EdgeLoop(Edge start)
    {
        var result = new LinkedList<Edge>();
        result.AddLast(start);
        if (FacesOf(start).Count == 0) return [.. result];
        var seen = new HashSet<Edge> { start };
        foreach (bool forward in new[] { true, false })
        {
            var e = start;
            int v = forward ? start.B : start.A;
            bool closed = false;
            while (NextInLoop(e, v) is { } next)
            {
                if (!seen.Add(next)) { closed = next == start; break; }
                if (forward) result.AddLast(next); else result.AddFirst(next);
                v = next.A == v ? next.B : next.A;
                e = next;
            }
            if (closed) break;
        }
        return [.. result];
    }

    Edge? NextInLoop(Edge e, int v)
    {
        var faces = FacesOf(e);
        if (faces.Count == 1)
        {
            Edge? only = null;
            foreach (var n in _neighbors[v])
            {
                var c = new Edge(v, n);
                if (c == e || !IsBoundary(c)) continue;
                if (only != null) return null; // border pinches at this vertex: ambiguous
                only = c;
            }
            return only;
        }
        if (faces.Count != 2 || _neighbors[v].Count != 4 || _vertexFaces[v].Count != 4) return null;
        if (_vertexFaces[v].Any(f => _m.Faces[f].Verts.Length != 4)) return null;
        foreach (var n in _neighbors[v])
        {
            var c = new Edge(v, n);
            if (c != e && !FacesOf(c).Any(faces.Contains)) return c;
        }
        return null;
    }

    /// <summary>
    /// The edge ring through <paramref name="start"/>: opposite edges across consecutive quads, in walk order.
    /// Stops at n-gons and triangles, at border edges (which are included), and where the ring closes.
    /// </summary>
    public List<Edge> EdgeRing(Edge start)
    {
        var result = new LinkedList<Edge>();
        result.AddLast(start);
        var startFaces = FacesOf(start);
        if (startFaces.Count is 0 or > 2) return [.. result];
        var seen = new HashSet<Edge> { start };
        for (int side = 0; side < startFaces.Count; side++)
        {
            var e = start;
            int face = startFaces[side];
            while (Opposite(face, e) is { } opp && seen.Add(opp))
            {
                if (side == 0) result.AddLast(opp); else result.AddFirst(opp);
                var next = FacesOf(opp);
                if (next.Count != 2) break;
                face = next[0] == face ? next[1] : next[0];
                e = opp;
            }
        }
        return [.. result];
    }

    Edge? Opposite(int face, Edge e)
    {
        var v = _m.Faces[face].Verts;
        if (v.Length != 4) return null;
        for (int i = 0; i < 4; i++)
            if (new Edge(v[i], v[(i + 1) % 4]) == e) return new Edge(v[(i + 2) % 4], v[(i + 3) % 4]);
        return null;
    }

    // ---- Shortest paths ----

    /// <summary>
    /// Shortest vertex path (by edge length) from the nearest of <paramref name="from"/> to the nearest of <paramref name="to"/>,
    /// inclusive. Null when they aren't connected.
    /// </summary>
    public List<int>? VertexPath(IEnumerable<int> from, IEnumerable<int> to)
    {
        var targets = to.ToHashSet();
        var dist = new float[_m.Positions.Count];
        var prev = new int[dist.Length];
        Array.Fill(dist, float.PositiveInfinity);
        Array.Fill(prev, -1);
        var queue = new PriorityQueue<int, float>();
        foreach (var s in from) { dist[s] = 0; queue.Enqueue(s, 0); }
        while (queue.TryDequeue(out int v, out float d))
        {
            if (d > dist[v]) continue;
            if (targets.Contains(v))
            {
                var path = new List<int>();
                for (int c = v; c >= 0; c = prev[c]) path.Add(c);
                path.Reverse();
                return path;
            }
            foreach (var n in _neighbors[v])
            {
                float nd = d + System.Numerics.Vector3.Distance(_m.Positions[v], _m.Positions[n]);
                if (nd >= dist[n]) continue;
                dist[n] = nd;
                prev[n] = v;
                queue.Enqueue(n, nd);
            }
        }
        return null;
    }

    /// <summary>Edges from <paramref name="from"/> to <paramref name="to"/> along the shortest vertex path between their ends, both included.</summary>
    public List<Edge>? EdgePath(Edge from, Edge to)
    {
        if (from == to) return [from];
        if (VertexPath([from.A, from.B], [to.A, to.B]) is not { } verts) return null;
        var result = new List<Edge> { from };
        for (int i = 1; i < verts.Count; i++) result.Add(new Edge(verts[i - 1], verts[i]));
        result.Add(to);
        return result.Distinct().ToList();
    }

    /// <summary>Fewest-steps face path across shared edges, both ends included. Null when they aren't connected.</summary>
    public List<int>? FacePath(int from, int to)
    {
        var prev = new Dictionary<int, int> { [from] = -1 };
        var queue = new Queue<int>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            int f = queue.Dequeue();
            if (f == to)
            {
                var path = new List<int>();
                for (int c = f; c >= 0; c = prev[c]) path.Add(c);
                path.Reverse();
                return path;
            }
            foreach (var n in FaceNeighbors(f))
                if (prev.TryAdd(n, f)) queue.Enqueue(n);
        }
        return null;
    }

    // ---- Grow / shrink ----

    /// <summary>Adds every face sharing an edge with the selection.</summary>
    public HashSet<int> GrowFaces(IReadOnlySet<int> faces)
    {
        var result = new HashSet<int>(faces);
        foreach (var f in faces) result.UnionWith(FaceNeighbors(f));
        return result;
    }

    /// <summary>Drops faces on the selection's rim: those sharing an edge with an unselected face or lying on an open border.</summary>
    public HashSet<int> ShrinkFaces(IReadOnlySet<int> faces) =>
        faces.Where(f => _m.Faces[f].Edges().All(e => FacesOf(e) is { Count: >= 2 } fs && fs.All(faces.Contains))).ToHashSet();

    /// <summary>Adds every vertex joined to the selection by an edge.</summary>
    public HashSet<int> GrowVertices(IReadOnlySet<int> verts)
    {
        var result = new HashSet<int>(verts);
        foreach (var v in verts) result.UnionWith(_neighbors[v]);
        return result;
    }

    /// <summary>Drops vertices on the selection's rim: those with an unselected neighbour or on an open border.</summary>
    public HashSet<int> ShrinkVertices(IReadOnlySet<int> verts) =>
        verts.Where(v => !IsBoundaryVertex(v) && _neighbors[v].All(verts.Contains)).ToHashSet();

    /// <summary>Adds every edge sharing a vertex with the selection.</summary>
    public HashSet<Edge> GrowEdges(IReadOnlySet<Edge> edges)
    {
        var result = new HashSet<Edge>(edges);
        foreach (var e in edges)
            foreach (var v in new[] { e.A, e.B })
                foreach (var n in _neighbors[v]) result.Add(new Edge(v, n));
        return result;
    }

    /// <summary>Keeps edges whose both ends are inside the selection: every edge at the vertex is selected and it isn't on a border.</summary>
    public HashSet<Edge> ShrinkEdges(IReadOnlySet<Edge> edges)
    {
        bool Inside(int v) => !IsBoundaryVertex(v) && _neighbors[v].All(n => edges.Contains(new Edge(v, n)));
        return edges.Where(e => Inside(e.A) && Inside(e.B)).ToHashSet();
    }
}
