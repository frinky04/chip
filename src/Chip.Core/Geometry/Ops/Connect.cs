using System.Numerics;

namespace Chip.Core.Geometry;

public static partial class MeshOps
{
    /// <summary>
    /// Connect in vertex mode: every face holding two or more of <paramref name="verts"/> is split along chords between
    /// them (in loop order). Existing face indices stay; extra pieces are appended. Returns the new edges.
    /// </summary>
    public static List<Edge> ConnectVertices(PolyMesh m, IEnumerable<int> verts)
    {
        var set = verts.ToHashSet();
        var chords = new List<Edge>();
        int count = m.Faces.Count;
        for (int f = 0; f < count; f++)
        {
            var v = m.Faces[f].Verts;
            var corners = Enumerable.Range(0, v.Length).Where(i => set.Contains(v[i])).ToList();
            if (corners.Count >= 2) chords.AddRange(SplitFaceAtCorners(m, f, corners));
        }
        return chords.Distinct().ToList();
    }

    /// <summary>
    /// Connect in edge mode (edge-ring connect): each selected edge gets a midpoint, and faces holding two or more
    /// midpoints are split between them. Faces with a single midpoint keep it as an extra corner so the mesh stays
    /// closed. Soft edges that were split stay soft on both halves. Returns the new edges.
    /// </summary>
    public static List<Edge> ConnectEdges(PolyMesh m, IEnumerable<Edge> edges)
    {
        var live = m.Edges().ToHashSet();
        var mids = new Dictionary<Edge, int>();
        foreach (var e in edges.Distinct().Where(live.Contains))
        {
            mids[e] = m.Positions.Count;
            m.Positions.Add((m.Positions[e.A] + m.Positions[e.B]) * 0.5f);
            if (m.SoftEdges.Remove(e))
            {
                m.SoftEdges.Add(new Edge(e.A, mids[e]));
                m.SoftEdges.Add(new Edge(mids[e], e.B));
            }
        }
        if (mids.Count == 0) return [];

        var split = new List<(int Face, List<int> Corners)>();
        for (int f = 0; f < m.Faces.Count; f++)
        {
            var face = m.Faces[f];
            int n = face.Verts.Length;
            var verts = new List<int>(n + 2);
            var uvs = face.UVs != null ? new List<Vector2>(n + 2) : null;
            var corners = new List<int>();
            for (int i = 0; i < n; i++)
            {
                int a = face.Verts[i], b = face.Verts[(i + 1) % n];
                verts.Add(a);
                uvs?.Add(face.UVs![i]);
                if (!mids.TryGetValue(new Edge(a, b), out int mid)) continue;
                corners.Add(verts.Count);
                verts.Add(mid);
                uvs?.Add((face.UVs![i] + face.UVs![(i + 1) % n]) * 0.5f);
            }
            if (corners.Count == 0) continue;
            face.Verts = verts.ToArray();
            face.UVs = uvs?.ToArray();
            if (corners.Count >= 2) split.Add((f, corners));
        }
        var chords = new List<Edge>();
        foreach (var (f, corners) in split) chords.AddRange(SplitFaceAtCorners(m, f, corners));
        return chords.Distinct().ToList();
    }

    /// <summary>
    /// Splits face <paramref name="f"/> along chords between consecutive <paramref name="corners"/> (corner indices).
    /// Each chord cuts off the arc of corners between its ends; with three or more corners the polygon through all of
    /// them remains as the core. The first piece keeps the face index, the rest are appended with the same surface.
    /// </summary>
    static List<Edge> SplitFaceAtCorners(PolyMesh m, int f, List<int> corners)
    {
        var face = m.Faces[f];
        int n = face.Verts.Length;
        corners = corners.Distinct().Order().ToList();
        int k = corners.Count;
        if (k < 2) return [];
        var pieces = new List<List<int>>();
        var chords = new List<Edge>();
        for (int i = 0; i < k; i++)
        {
            int c0 = corners[i], c1 = corners[(i + 1) % k];
            int gap = (c1 - c0 + n) % n;
            if (gap <= 1 || face.Verts[c0] == face.Verts[c1]) continue;
            var piece = new List<int>();
            for (int c = c0; ; c = (c + 1) % n)
            {
                piece.Add(c);
                if (c == c1) break;
            }
            pieces.Add(piece);
            chords.Add(new Edge(face.Verts[c0], face.Verts[c1]));
        }
        if (k >= 3) pieces.Add(corners);
        if (pieces.Count < 2) return [];

        Face Piece(List<int> c) => new(c.Select(i => face.Verts[i]).ToArray(), face.Material, face.Map)
        {
            UVs = face.UVs is { } uv ? c.Select(i => uv[i]).ToArray() : null,
        };
        m.Faces[f] = Piece(pieces[0]);
        foreach (var p in pieces.Skip(1)) m.Faces.Add(Piece(p));
        return chords;
    }
}
