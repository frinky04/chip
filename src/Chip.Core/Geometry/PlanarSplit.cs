namespace Chip.Core.Geometry;

/// <summary>
/// Splits one polygon (a face flattened to 2D) by cut segments into simple regions. Every crossing between any two
/// segments (a cut with the border, a cut with another cut or itself, the border with itself) becomes a node, the
/// pieces inside the polygon form a planar graph, and the graph's regions are traced. A region is kept when the original
/// loop winds around it positively, so cuts may cross themselves, revisit points or close loops, and a face that
/// crosses itself comes apart into its lobes. Cut pieces left dangling are dropped. Faces can't hold holes, so a closed
/// cut touching nothing gets straight bridges out to whatever surrounds it, and a region pinched at one point (a loop
/// hanging off the rest by a single node) gets one more bridge, until every region is a simple polygon.
/// </summary>
internal static class PlanarSplit
{
    internal sealed class Node(P2 p)
    {
        public readonly P2 P = p;
        /// <summary>Index of the loop corner at this point, or -1.</summary>
        public int Corner = -1;
        /// <summary>Loop edges passing through this point (edge i runs from corner i to i + 1), with the parameter along each.</summary>
        public readonly List<(int Edge, double T)> On = new();
        public bool OnBorder => Corner >= 0 || On.Count > 0;
    }

    /// <summary>Every node, and the kept regions as counter-clockwise node cycles without redundant straight-through nodes.</summary>
    internal sealed record Result(List<Node> Nodes, List<List<int>> Regions);

    readonly record struct Seg(P2 A, P2 B, int Edge);

    /// <summary>Null when the cut couldn't be resolved into simple regions (degenerate input).</summary>
    public static Result? Split(IReadOnlyList<P2> loop, IEnumerable<(P2 A, P2 B)> cuts, double eps)
    {
        if (loop.Count < 3) return null;
        var segs = new List<Seg>();
        for (int i = 0; i < loop.Count; i++) segs.Add(new Seg(loop[i], loop[(i + 1) % loop.Count], i));
        foreach (var (a, b) in cuts)
            if ((b - a).Length > eps) segs.Add(new Seg(a, b, -1));

        for (int attempt = 0; attempt < 64; attempt++)
        {
            var g = new Graph(loop, segs, eps);
            List<List<int>>? regions = null;
            var bridges = g.IslandBridges() ?? g.Trace(out regions);
            if (bridges == null) return new Result(g.Nodes, regions!.Select(g.WithoutStraightNodes).ToList());
            if (bridges.Count == 0) return null;
            segs.AddRange(bridges);
        }
        return null;
    }

    sealed class Graph
    {
        public readonly List<Node> Nodes = new();
        readonly IReadOnlyList<P2> _loop;
        readonly double _eps;
        readonly List<HashSet<int>> _adj = new();

        public Graph(IReadOnlyList<P2> loop, List<Seg> segs, double eps)
        {
            _loop = loop;
            _eps = eps;
            for (int i = 0; i < loop.Count; i++)
            {
                int n = NodeAt(loop[i]);
                if (Nodes[n].Corner < 0) Nodes[n].Corner = i;
            }
            var ends = segs.Select(s => (A: NodeAt(s.A), B: NodeAt(s.B))).ToArray();
            for (int i = 0; i < segs.Count; i++)
                for (int j = i + 1; j < segs.Count; j++)
                    if (Crossing(segs[i], segs[j]) is { } x) NodeAt(x);

            // Split every segment at every node on it; pieces from border segments are border.
            var pieces = new Dictionary<(int, int), bool>();
            for (int i = 0; i < segs.Count; i++)
            {
                var s = segs[i];
                if (ends[i].A == ends[i].B) continue;
                var d = s.B - s.A;
                double len2 = P2.Dot(d, d), tol = eps / Math.Sqrt(len2);
                var on = new List<(double T, int N)>();
                for (int n = 0; n < Nodes.Count; n++)
                {
                    bool end = n == ends[i].A || n == ends[i].B;
                    double t = n == ends[i].A ? 0 : n == ends[i].B ? 1 : P2.Dot(Nodes[n].P - s.A, d) / len2;
                    if (!end && (t < -tol || t > 1 + tol || P2.SegmentDistance(Nodes[n].P, s.A, s.B) > eps)) continue;
                    t = Math.Clamp(t, 0, 1);
                    on.Add((t, n));
                    if (s.Edge >= 0 && !end && !Nodes[n].On.Any(o => o.Edge == s.Edge)) Nodes[n].On.Add((s.Edge, t));
                }
                on.Sort((x, y) => x.T.CompareTo(y.T));
                for (int k = 0; k + 1 < on.Count; k++)
                {
                    int a = on[k].N, b = on[k + 1].N;
                    if (a == b) continue;
                    var key = (Math.Min(a, b), Math.Max(a, b));
                    pieces[key] = (pieces.TryGetValue(key, out bool border) && border) || s.Edge >= 0;
                }
            }

            foreach (var _ in Nodes) _adj.Add(new HashSet<int>());
            foreach (var ((a, b), border) in pieces)
            {
                if (!border)
                {
                    // A cut piece counts only inside the polygon and off its border.
                    var mid = (Nodes[a].P + Nodes[b].P) * 0.5;
                    if (DistanceToLoop(mid) <= eps || P2.Winding(loop, mid) <= 0) continue;
                }
                _adj[a].Add(b);
                _adj[b].Add(a);
            }

            // Drop dangling pieces (and spikes in the border), so every edge left lies on a cycle or a bridge.
            var queue = new Queue<int>(Enumerable.Range(0, Nodes.Count).Where(n => _adj[n].Count == 1));
            while (queue.Count > 0)
            {
                int n = queue.Dequeue();
                if (_adj[n].Count != 1) continue;
                int other = _adj[n].First();
                _adj[n].Clear();
                _adj[other].Remove(n);
                if (_adj[other].Count == 1) queue.Enqueue(other);
            }
        }

        int NodeAt(P2 p)
        {
            for (int i = 0; i < Nodes.Count; i++)
                if ((Nodes[i].P - p).Length <= _eps) return i;
            Nodes.Add(new Node(p));
            return Nodes.Count - 1;
        }

        /// <summary>Where two non-parallel segments meet (within tolerance), or null. Overlaps come from the split pass.</summary>
        P2? Crossing(Seg s, Seg u)
        {
            var r = s.B - s.A; var q = u.B - u.A;
            double den = P2.Cross(r, q), lr = r.Length, lq = q.Length;
            if (Math.Abs(den) <= 1e-12 * lr * lq) return null;
            var w = u.A - s.A;
            double t = P2.Cross(w, q) / den, v = P2.Cross(w, r) / den;
            double tr = _eps / lr, tq = _eps / lq;
            if (t < -tr || t > 1 + tr || v < -tq || v > 1 + tq) return null;
            return s.A + r * Math.Clamp(t, 0, 1);
        }

        double DistanceToLoop(P2 p)
        {
            double best = double.MaxValue;
            for (int i = 0; i < _loop.Count; i++) best = Math.Min(best, P2.SegmentDistance(p, _loop[i], _loop[(i + 1) % _loop.Count]));
            return best;
        }

        /// <summary>
        /// Bridges for the first group of cut edges that touches neither the border nor anything attached to it: a
        /// straight run from its two extreme nodes outward, along the frame's first axis, to whatever they hit. Null
        /// when there is no such group; empty when one exists but can't be bridged.
        /// </summary>
        public List<Seg>? IslandBridges()
        {
            var seen = new bool[Nodes.Count];
            for (int start = 0; start < Nodes.Count; start++)
            {
                if (seen[start] || _adj[start].Count == 0) continue;
                var group = new List<int>();
                var stack = new Stack<int>([start]);
                seen[start] = true;
                while (stack.Count > 0)
                {
                    int n = stack.Pop();
                    group.Add(n);
                    foreach (int m in _adj[n])
                        if (!seen[m]) { seen[m] = true; stack.Push(m); }
                }
                if (group.Any(n => Nodes[n].OnBorder)) continue;

                var bridges = new List<Seg>();
                int lo = group.MinBy(n => Nodes[n].P.X), hi = group.MaxBy(n => Nodes[n].P.X);
                foreach (var (n, dir) in new[] { (lo, new P2(-1, 0)), (hi, new P2(1, 0)) })
                    if (RayHit(n, dir) is { } hit) bridges.Add(new Seg(Nodes[n].P, hit.Point, -1));
                return bridges;
            }
            return null;
        }

        /// <summary>The nearest graph edge a ray from node <paramref name="from"/> meets, not counting edges at that node.</summary>
        ((int, int) Edge, P2 Point)? RayHit(int from, P2 dir)
        {
            var o = Nodes[from].P;
            double best = double.MaxValue;
            (int, int) edge = default;
            for (int a = 0; a < Nodes.Count; a++)
                foreach (int b in _adj[a])
                {
                    if (b < a || a == from || b == from) continue;
                    var p = Nodes[a].P; var e = Nodes[b].P - p;
                    double den = P2.Cross(dir, e), le = e.Length;
                    if (Math.Abs(den) <= 1e-12 * le) continue;
                    var w = p - o;
                    double t = P2.Cross(w, e) / den, u = P2.Cross(w, dir) / den;
                    if (t > _eps && u >= -_eps / le && u <= 1 + _eps / le && t < best) { best = t; edge = (a, b); }
                }
            return best < double.MaxValue ? (edge, o + dir * best) : null;
        }

        /// <summary>
        /// Traces every region. Returns null with the kept regions when they are all simple; otherwise a bridge that
        /// splits the first pinched region (empty when none can be found).
        /// </summary>
        public List<Seg>? Trace(out List<List<int>>? regions)
        {
            regions = new List<List<int>>();
            var sorted = new int[Nodes.Count][];
            for (int n = 0; n < Nodes.Count; n++)
            {
                var p = Nodes[n].P;
                sorted[n] = _adj[n].OrderBy(m => Math.Atan2(Nodes[m].P.Y - p.Y, Nodes[m].P.X - p.X)).ToArray();
            }
            // The next half-edge round a region (kept on the left): at b, the first edge clockwise from b→a.
            int Next(int a, int b)
            {
                var around = sorted[b];
                int i = Array.IndexOf(around, a);
                return around[(i + around.Length - 1) % around.Length];
            }

            var visited = new HashSet<(int, int)>();
            int limit = 4 * _adj.Sum(s => s.Count) + 4;
            for (int a0 = 0; a0 < Nodes.Count; a0++)
                foreach (int b0 in sorted[a0])
                {
                    if (visited.Contains((a0, b0))) continue;
                    var cycle = new List<int>();
                    int a = a0, b = b0;
                    do
                    {
                        visited.Add((a, b));
                        cycle.Add(a);
                        (a, b) = (b, Next(a, b));
                    } while ((a, b) != (a0, b0) && cycle.Count < limit);

                    if (Area(cycle) <= _eps * _eps) continue; // the outside, or a sliver of nothing
                    if (cycle.Distinct().Count() != cycle.Count)
                    {
                        // Touching itself: lobes that meet at a point are separate faces, but a part wound the other
                        // way is a hole in this region and needs a bridge out.
                        var parts = Unpinch(cycle);
                        if (parts.FirstOrDefault(p => Area(p) < -_eps * _eps) is { } hole)
                        {
                            regions = null;
                            return PinchBridge(cycle, hole);
                        }
                        foreach (var part in parts)
                            if (Area(part) > _eps * _eps && Inside(part)) regions.Add(part);
                        continue;
                    }
                    if (Inside(cycle)) regions.Add(cycle);
                }
            return null;
        }

        double Area(List<int> cycle) => P2.LoopArea(cycle.Select(n => Nodes[n].P).ToList());

        /// <summary>The original loop winds round this region positively (it is part of the face, not outside or a reversed lobe).</summary>
        bool Inside(List<int> cycle) => InteriorPoint(cycle.Select(n => Nodes[n].P).ToList()) is { } p && P2.Winding(_loop, p) > 0;

        /// <summary>A cycle split at every node it visits twice, into loops that each visit their nodes once.</summary>
        static List<List<int>> Unpinch(List<int> cycle)
        {
            for (int i = 0; i < cycle.Count; i++)
            {
                int k = cycle.IndexOf(cycle[i], i + 1);
                if (k < 0) continue;
                var inner = cycle.GetRange(i, k - i);
                var outer = cycle.Skip(k).Concat(cycle.Take(i)).ToList();
                return Unpinch(inner).Concat(Unpinch(outer)).ToList();
            }
            return [cycle];
        }

        P2? InteriorPoint(List<P2> pts)
        {
            var tris = new List<int>();
            Triangulator.Triangulate(pts, _eps, tris);
            double best = 0;
            P2? point = null;
            for (int i = 0; i + 2 < tris.Count; i += 3)
            {
                var (a, b, c) = (pts[tris[i]], pts[tris[i + 1]], pts[tris[i + 2]]);
                double area = P2.Cross(b - a, c - a);
                if (area > best) { best = area; point = (a + b + c) * (1.0 / 3); }
            }
            return point;
        }

        /// <summary>
        /// A region whose outline touches itself round a hole (a loop hanging off the rest by one point, or by a single
        /// edge) can't be one face. Bridge from the hole's outline (its extreme nodes first, along both frame axes) to
        /// anything else on the region's outline.
        /// </summary>
        List<Seg> PinchBridge(List<int> cycle, List<int> inner)
        {
            var innerSet = inner.ToHashSet();
            // Nodes the hole shares with the rest of the outline: a bridge from there would add nothing.
            var attached = innerSet.Where(n => cycle.Count(m => m == n) > inner.Count(m => m == n)).ToHashSet();

            var dirs = new[] { new P2(-1, 0), new P2(1, 0), new P2(0, -1), new P2(0, 1) };
            // Each node and direction, the nodes furthest out in that direction first (they can't hit their own part).
            var candidates = inner.Where(n => !attached.Contains(n)).Distinct()
                .SelectMany(n => dirs.Select(d => (N: n, Dir: d)))
                .OrderByDescending(c => P2.Dot(Nodes[c.N].P, c.Dir) - inner.Max(m => P2.Dot(Nodes[m].P, c.Dir)));
            var polygon = cycle.Select(n => Nodes[n].P).ToList();
            foreach (var (n, dir) in candidates)
            {
                if (RayHit(n, dir) is not { } hit) continue;
                if (innerSet.Contains(hit.Edge.Item1) && innerSet.Contains(hit.Edge.Item2)) continue;
                var mid = (Nodes[n].P + hit.Point) * 0.5;
                if (!InsidePolygon(polygon, mid) || OnAnyEdge(mid)) continue; // must cross the region, not run along an edge
                return [new Seg(Nodes[n].P, hit.Point, -1)];
            }
            return [];
        }

        bool OnAnyEdge(P2 p)
        {
            for (int a = 0; a < Nodes.Count; a++)
                foreach (int b in _adj[a])
                    if (b > a && P2.SegmentDistance(p, Nodes[a].P, Nodes[b].P) <= _eps) return true;
            return false;
        }

        static bool InsidePolygon(List<P2> poly, P2 p)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                var a = poly[i]; var b = poly[j];
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            }
            return inside;
        }

        /// <summary>
        /// The region without nodes that only continue a straight run: a split point on one border edge with no cut
        /// leaving it, or a bend-free point inside. Corners and self-crossings stay.
        /// </summary>
        public List<int> WithoutStraightNodes(List<int> cycle)
        {
            var kept = new List<int>();
            for (int i = 0; i < cycle.Count; i++)
            {
                int n = cycle[i];
                var node = Nodes[n];
                if (node.Corner < 0 && node.On.Count <= 1 && _adj[n].Count == 2)
                {
                    var p = Nodes[cycle[(i + cycle.Count - 1) % cycle.Count]].P;
                    var q = Nodes[cycle[(i + 1) % cycle.Count]].P;
                    var d1 = node.P - p; var d2 = q - node.P;
                    if (Math.Abs(P2.Cross(d1, d2)) <= _eps * (q - p).Length && P2.Dot(d1, d2) > 0) continue;
                }
                kept.Add(n);
            }
            return kept;
        }
    }
}
