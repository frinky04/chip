using System.Numerics;

namespace Chip.Core.Geometry;

public enum MeshIssueKind
{
    /// <summary>Fewer than three distinct corners, or no area.</summary>
    DegenerateFace,
    /// <summary>The face's loop visits a vertex twice (it is pinched there).</summary>
    RepeatedVertex,
    /// <summary>The face's outline crosses or touches itself, so it can't be filled properly.</summary>
    SelfIntersectingFace,
    /// <summary>A vertex sits on an open edge of a face that doesn't use it: the faces either side don't share their edges.</summary>
    TJunction,
    /// <summary>More than two faces use the edge.</summary>
    NonManifoldEdge,
    /// <summary>Both faces on the edge run along it the same way: one of them is flipped.</summary>
    FlippedNeighbour,
}

/// <summary>One problem found by <see cref="MeshOps.Check"/>. Face, vertex and edge are set where they apply (-1 / default otherwise).</summary>
public readonly record struct MeshIssue(MeshIssueKind Kind, int Face = -1, int Vertex = -1, Edge Edge = default);

public static partial class MeshOps
{
    /// <summary>
    /// Lists what is broken in a mesh: faces that are degenerate, pinched or cross themselves, T-junctions (vertices
    /// lying on an open edge within <paramref name="epsilon"/>), and edges with more than two faces or with neighbours
    /// wound against each other. Open edges on their own are fine (a floor plane has them). An empty list is a clean mesh.
    /// </summary>
    public static List<MeshIssue> Check(PolyMesh m, float epsilon = WeldDistance)
    {
        var issues = new List<MeshIssue>();
        for (int f = 0; f < m.Faces.Count; f++)
            if (FaceIssue(m, f) is { } kind) issues.Add(new MeshIssue(kind, f));

        var uses = new Dictionary<Edge, (int Count, int Forward)>();
        foreach (var face in m.Faces)
            for (int i = 0; i < face.Verts.Length; i++)
            {
                int a = face.Verts[i], b = face.Verts[(i + 1) % face.Verts.Length];
                if (a == b) continue;
                var e = new Edge(a, b);
                var u = uses.GetValueOrDefault(e);
                uses[e] = (u.Count + 1, u.Forward + (a < b ? 1 : 0));
            }
        foreach (var (e, (count, forward)) in uses)
        {
            if (count > 2) issues.Add(new MeshIssue(MeshIssueKind.NonManifoldEdge, Edge: e));
            else if (count == 2 && forward != 1) issues.Add(new MeshIssue(MeshIssueKind.FlippedNeighbour, Edge: e));
        }

        foreach (var t in TJunctions(m, epsilon))
            issues.Add(new MeshIssue(MeshIssueKind.TJunction, t.Face, t.Vertex, new Edge(t.A, t.B)));
        return issues;
    }

    /// <summary>What is wrong with one face's loop on its own, or null when it is a simple polygon.</summary>
    public static MeshIssueKind? FaceIssue(PolyMesh m, int f)
    {
        var v = m.Faces[f].Verts;
        int distinct = v.Distinct().Count();
        if (distinct < 3) return MeshIssueKind.DegenerateFace;
        if (distinct != v.Length) return MeshIssueKind.RepeatedVertex;

        var frame = new FaceFrame(m, f);
        var loop = frame.Loop(m, f);
        double eps = 10 * FaceFrame.Epsilon(loop, frame.Origin);
        int k = loop.Length;
        if (Width(loop) <= eps) return MeshIssueKind.DegenerateFace;

        for (int i = 0; i < k; i++)
        {
            P2 a = loop[i], b = loop[(i + 1) % k];
            // The next edge folding straight back along this one is a spike.
            var c = loop[(i + 2) % k];
            if (Math.Abs(P2.Cross(b - a, c - b)) <= eps * (c - a).Length && P2.Dot(b - a, c - b) < 0) return MeshIssueKind.SelfIntersectingFace;
            for (int j = i + 2; j < k; j++)
            {
                if ((j + 1) % k == i) continue; // adjacent through the loop's start
                if (SegmentsTouch(a, b, loop[j], loop[(j + 1) % k], eps)) return MeshIssueKind.SelfIntersectingFace;
            }
        }
        return null;
    }

    /// <summary>How far the loop reaches off the line through its two furthest-apart corners (0 when all are in a line).</summary>
    static double Width(P2[] loop)
    {
        int ia = 0, ib = 0;
        double far = -1;
        for (int i = 0; i < loop.Length; i++)
            for (int j = i + 1; j < loop.Length; j++)
            {
                double d = (loop[j] - loop[i]).Length;
                if (d > far) { far = d; ia = i; ib = j; }
            }
        if (far <= 0) return 0;
        var axis = (loop[ib] - loop[ia]) * (1 / far);
        return loop.Max(p => Math.Abs(P2.Cross(axis, p - loop[ia])));
    }

    static bool SegmentsTouch(P2 a, P2 b, P2 c, P2 d, double eps)
    {
        double d1 = P2.Cross(b - a, c - a), d2 = P2.Cross(b - a, d - a);
        double d3 = P2.Cross(d - c, a - c), d4 = P2.Cross(d - c, b - c);
        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return true;
        return P2.SegmentDistance(c, a, b) <= eps || P2.SegmentDistance(d, a, b) <= eps
            || P2.SegmentDistance(a, c, d) <= eps || P2.SegmentDistance(b, c, d) <= eps;
    }

    /// <summary>
    /// Vertices lying inside an open edge a→b of a face (t along it from a) that continue along that edge with an open
    /// edge of their own: the two sides of a seam that were split differently.
    /// </summary>
    internal static List<(int Face, int A, int B, int Vertex, float T)> TJunctions(PolyMesh m, float epsilon)
    {
        var count = new Dictionary<Edge, int>();
        foreach (var face in m.Faces)
            foreach (var e in face.Edges())
                count[e] = count.GetValueOrDefault(e) + 1;
        var open = new List<(int Face, int A, int B)>();
        var openNeighbours = new Dictionary<int, List<int>>();
        for (int f = 0; f < m.Faces.Count; f++)
        {
            var v = m.Faces[f].Verts;
            for (int i = 0; i < v.Length; i++)
            {
                int a = v[i], b = v[(i + 1) % v.Length];
                if (a == b || count[new Edge(a, b)] != 1) continue;
                open.Add((f, a, b));
                (openNeighbours.TryGetValue(a, out var la) ? la : openNeighbours[a] = new List<int>()).Add(b);
                (openNeighbours.TryGetValue(b, out var lb) ? lb : openNeighbours[b] = new List<int>()).Add(a);
            }
        }
        var found = new List<(int, int, int, int, float)>();
        if (open.Count == 0) return found;

        // Open-edge vertices sorted along x, so each edge only looks at the ones within its span.
        var candidates = openNeighbours.Keys.OrderBy(v => m.Positions[v].X).ToArray();
        var xs = candidates.Select(v => m.Positions[v].X).ToArray();
        foreach (var (f, a, b) in open)
        {
            Vector3 pa = m.Positions[a], pb = m.Positions[b], d = pb - pa;
            float len2 = d.LengthSquared();
            if (len2 <= epsilon * epsilon) continue;
            int lo = LowerBound(xs, MathF.Min(pa.X, pb.X) - epsilon);
            float xMax = MathF.Max(pa.X, pb.X) + epsilon;
            var loop = m.Faces[f].Verts;
            for (int i = lo; i < candidates.Length && xs[i] <= xMax; i++)
            {
                int v = candidates[i];
                if (v == a || v == b || loop.Contains(v)) continue;
                var p = m.Positions[v];
                float t = Vector3.Dot(p - pa, d) / len2;
                float tol = epsilon / MathF.Sqrt(len2);
                if (t <= tol || t >= 1 - tol || Vector3.Distance(pa + d * t, p) > epsilon) continue;
                // It's a seam only if v carries on along this edge's line with an open edge of its own.
                bool along = openNeighbours[v].Any(w => w != v && LineDistance(m.Positions[w], pa, d, len2) <= epsilon);
                if (along) found.Add((f, a, b, v, t));
            }
        }
        return found;
    }

    static float LineDistance(Vector3 p, Vector3 origin, Vector3 d, float len2) =>
        Vector3.Distance(origin + d * (Vector3.Dot(p - origin, d) / len2), p);

    static int LowerBound(float[] sorted, float x)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (sorted[mid] < x) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}
