using System.Numerics;

namespace Chip.Core.Geometry;

/// <summary>
/// Ear-clipping triangulation of a face loop, after Mapbox earcut. Output preserves the face winding. A simple loop
/// always gets n - 2 triangles inside it that use every boundary edge: collinear corners and repeated points get a
/// zero-area triangle instead of being dropped, so neighbouring faces' edges still line up. When plain ear clipping
/// stalls, the loop is cured of local self-intersections and then split along a valid diagonal; it never fans across
/// the outside of a concave face.
/// </summary>
public static class Triangulator
{
    // Working rings are reused per thread (dressing triangulates in parallel); this runs for every face on every rebuild.
    [ThreadStatic] static Earcut? _earcut;

    /// <summary>Appends triangles as indices into the face's loop (0..n-1).</summary>
    public static void Triangulate(PolyMesh mesh, int face, List<int> loopIndicesOut)
    {
        var verts = mesh.Faces[face].Verts;
        int n = verts.Length;
        if (n < 3) return;
        if (n == 3) { loopIndicesOut.AddRange([0, 1, 2]); return; }

        var frame = new FaceFrame(mesh, face);
        var e = _earcut ??= new Earcut();
        e.Load(n);
        for (int i = 0; i < n; i++) e.Points[i] = frame.To2(mesh.Positions[verts[i]]);
        e.Run(FaceFrame.Epsilon(e.Points.AsSpan(0, n), frame.Origin), loopIndicesOut);
    }

    /// <summary>
    /// Triangulates a counter-clockwise 2D loop, appending index triples into it. Points within <paramref name="eps"/>
    /// of each other count as the same, and a corner within it of the line through its neighbours as straight.
    /// </summary>
    internal static void Triangulate(IReadOnlyList<P2> loop, double eps, List<int> output)
    {
        int n = loop.Count;
        if (n < 3) return;
        if (n == 3) { output.AddRange([0, 1, 2]); return; }
        var e = _earcut ??= new Earcut();
        e.Load(n);
        for (int i = 0; i < n; i++) e.Points[i] = loop[i];
        e.Run(eps, output);
    }

    /// <summary>
    /// Earcut on a doubly linked ring (no holes). Its sign convention is kept: <see cref="Area"/> is negative for a
    /// convex corner of a counter-clockwise ring.
    /// </summary>
    sealed class Earcut
    {
        public P2[] Points = new P2[64];
        int[] _i = new int[64], _prev = new int[64], _next = new int[64];
        int _count;
        List<int> _out = null!;
        double _eps;

        /// <summary>Starts a ring of <paramref name="n"/> points; the caller fills <see cref="Points"/>.</summary>
        public void Load(int n)
        {
            // Each split adds two nodes, and there are fewer splits than points.
            int need = 3 * n + 4;
            if (Points.Length < need)
            {
                Points = new P2[need];
                _i = new int[need]; _prev = new int[need]; _next = new int[need];
            }
            for (int k = 0; k < n; k++)
            {
                _i[k] = k;
                _prev[k] = (k + n - 1) % n;
                _next[k] = (k + 1) % n;
            }
            _count = n;
        }

        public void Run(double eps, List<int> output)
        {
            _eps = eps;
            _out = output;
            Run(0, 0);
            _out = null!;
        }

        int NewNode(int from)
        {
            Points[_count] = Points[from];
            _i[_count] = _i[from];
            return _count++;
        }

        void Remove(int p)
        {
            _next[_prev[p]] = _next[p];
            _prev[_next[p]] = _prev[p];
        }

        void Emit(int a, int b, int c)
        {
            _out.Add(_i[a]); _out.Add(_i[b]); _out.Add(_i[c]);
        }

        double Area(int p, int q, int r)
        {
            P2 a = Points[p], b = Points[q], c = Points[r];
            return (b.Y - a.Y) * (c.X - b.X) - (b.X - a.X) * (c.Y - b.Y);
        }

        /// <summary>q lies on the line through p and r, within tolerance (the corner at q is straight or a spike).</summary>
        bool Flat(int p, int q, int r) => Math.Abs(Area(p, q, r)) <= _eps * Math.Max((Points[r] - Points[p]).Length, _eps);

        bool Convex(int p, int q, int r) => Area(p, q, r) < 0 && !Flat(p, q, r);

        bool Same(int p, int q) => (Points[p] - Points[q]).Length <= _eps;

        void Run(int ear, int pass)
        {
            int stop = ear;
            while (_prev[ear] != _next[ear])
            {
                int prev = _prev[ear], next = _next[ear];
                if (IsEar(ear))
                {
                    Emit(prev, ear, next);
                    Remove(ear);
                    ear = _next[next];
                    stop = ear;
                    continue;
                }
                ear = next;
                if (ear != stop) continue;

                // A full lap without an ear.
                if (pass == 0) Run(Filter(ear), 1);
                else if (pass == 1) Run(CureLocalIntersections(Filter(ear)), 2);
                else SplitAndRun(ear);
                return;
            }
        }

        bool IsEar(int ear)
        {
            int a = _prev[ear], b = ear, c = _next[ear];
            if (!Convex(a, b, c)) return false;
            P2 pa = Points[a], pb = Points[b], pc = Points[c];
            double x0 = Math.Min(pa.X, Math.Min(pb.X, pc.X)), x1 = Math.Max(pa.X, Math.Max(pb.X, pc.X));
            double y0 = Math.Min(pa.Y, Math.Min(pb.Y, pc.Y)), y1 = Math.Max(pa.Y, Math.Max(pb.Y, pc.Y));
            for (int p = _next[c]; p != a; p = _next[p])
            {
                var q = Points[p];
                if (q.X < x0 || q.X > x1 || q.Y < y0 || q.Y > y1) continue;
                if (Same(p, a)) continue; // a repeat of the ear's first corner doesn't block it (earcut)
                if (InTriangle(pa, pb, pc, q) && !Convex(_prev[p], p, _next[p])) return false;
            }
            return true;
        }

        static bool InTriangle(P2 a, P2 b, P2 c, P2 p) =>
            (c.X - p.X) * (a.Y - p.Y) >= (a.X - p.X) * (c.Y - p.Y) &&
            (a.X - p.X) * (b.Y - p.Y) >= (b.X - p.X) * (a.Y - p.Y) &&
            (b.X - p.X) * (c.Y - p.Y) >= (c.X - p.X) * (b.Y - p.Y);

        /// <summary>Takes out repeated and straight-through points, each as a zero-area triangle so its edges stay covered.</summary>
        int Filter(int start)
        {
            int p = start, end = start;
            bool again;
            do
            {
                again = false;
                if (_next[p] != _prev[p] && (Same(p, _next[p]) || Flat(_prev[p], p, _next[p])))
                {
                    Emit(_prev[p], p, _next[p]);
                    Remove(p);
                    p = end = _prev[p];
                    if (p == _next[p]) break;
                    again = true;
                }
                else p = _next[p];
            } while (again || p != end);
            return end;
        }

        int CureLocalIntersections(int start)
        {
            int p = start;
            do
            {
                int a = _prev[p], b = _next[_next[p]];
                if (!Same(a, b) && Intersects(a, p, _next[p], b) && LocallyInside(a, b) && LocallyInside(b, a))
                {
                    Emit(a, p, b);
                    Remove(p);
                    Remove(_next[p]);
                    p = start = b;
                }
                p = _next[p];
            } while (p != start);
            return Filter(p);
        }

        void SplitAndRun(int start)
        {
            int a = start;
            do
            {
                for (int b = _next[_next[a]]; b != _prev[a]; b = _next[b])
                {
                    if (_i[a] == _i[b] || !IsValidDiagonal(a, b)) continue;
                    int c = Split(a, b);
                    a = Filter(a);
                    c = Filter(c);
                    Run(a, 0);
                    Run(c, 0);
                    return;
                }
                a = _next[a];
            } while (a != start);

            // Nothing valid left (the loop crosses itself): close what remains with a fan rather than leave a gap.
            for (int p = _next[start]; _next[p] != start; p = _next[p]) Emit(start, p, _next[p]);
        }

        bool IsValidDiagonal(int a, int b) =>
            _i[_next[a]] != _i[b] && _i[_prev[a]] != _i[b] && !IntersectsRing(a, b) &&
            (LocallyInside(a, b) && LocallyInside(b, a) && MiddleInside(a, b) &&
                (Area(_prev[a], a, _prev[b]) != 0 || Area(a, _prev[b], b) != 0) ||
             Same(a, b) && Area(_prev[a], a, _next[a]) > 0 && Area(_prev[b], b, _next[b]) > 0);

        bool Intersects(int p1, int q1, int p2, int q2)
        {
            int o1 = Math.Sign(Area(p1, q1, p2)), o2 = Math.Sign(Area(p1, q1, q2));
            int o3 = Math.Sign(Area(p2, q2, p1)), o4 = Math.Sign(Area(p2, q2, q1));
            if (o1 != o2 && o3 != o4) return true;
            return (o1 == 0 && OnSegment(p1, p2, q1)) || (o2 == 0 && OnSegment(p1, q2, q1))
                || (o3 == 0 && OnSegment(p2, p1, q2)) || (o4 == 0 && OnSegment(p2, q1, q2));
        }

        bool OnSegment(int p, int q, int r)
        {
            P2 a = Points[p], b = Points[q], c = Points[r];
            return b.X <= Math.Max(a.X, c.X) && b.X >= Math.Min(a.X, c.X) && b.Y <= Math.Max(a.Y, c.Y) && b.Y >= Math.Min(a.Y, c.Y);
        }

        bool IntersectsRing(int a, int b)
        {
            int p = a;
            do
            {
                int q = _next[p];
                if (_i[p] != _i[a] && _i[q] != _i[a] && _i[p] != _i[b] && _i[q] != _i[b] && Intersects(p, q, a, b)) return true;
                p = q;
            } while (p != a);
            return false;
        }

        bool LocallyInside(int a, int b) =>
            Area(_prev[a], a, _next[a]) < 0
                ? Area(a, b, _next[a]) >= 0 && Area(a, _prev[a], b) >= 0
                : Area(a, b, _prev[a]) < 0 || Area(a, _next[a], b) < 0;

        bool MiddleInside(int a, int b)
        {
            var m = (Points[a] + Points[b]) * 0.5;
            bool inside = false;
            int p = a;
            do
            {
                P2 u = Points[p], v = Points[_next[p]];
                if ((u.Y > m.Y) != (v.Y > m.Y) && v.Y != u.Y && m.X < (v.X - u.X) * (m.Y - u.Y) / (v.Y - u.Y) + u.X) inside = !inside;
                p = _next[p];
            } while (p != a);
            return inside;
        }

        /// <summary>Links a to b, splitting the ring in two; returns a node of the second ring.</summary>
        int Split(int a, int b)
        {
            int a2 = NewNode(a), b2 = NewNode(b), an = _next[a], bp = _prev[b];
            _next[a] = b; _prev[b] = a;
            _next[a2] = an; _prev[an] = a2;
            _next[b2] = a2; _prev[a2] = b2;
            _next[bp] = b2; _prev[b2] = bp;
            return b2;
        }
    }
}
