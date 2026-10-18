namespace Chip.Core.Geometry;

public static partial class MeshOps
{
    /// <summary>
    /// Extrudes open (boundary) edges: each gets a zero-area quad continuing its face, built on duplicated end vertices
    /// that connected edges share. Edges used by two faces are ignored. Returns the new outer edges (what the caller
    /// should select and move) and the new faces; world-align the faces once they have been moved.
    /// </summary>
    public static (List<Edge> NewEdges, List<int> SideFaces) ExtrudeEdges(PolyMesh m, IEnumerable<Edge> edges)
    {
        var edgeFaces = Topology.EdgeFaces(m);
        var dup = new Dictionary<int, int>();
        int Dup(int v)
        {
            if (dup.TryGetValue(v, out int nv)) return nv;
            nv = m.Positions.Count;
            m.Positions.Add(m.Positions[v]);
            return dup[v] = nv;
        }

        var newEdges = new List<Edge>();
        var sides = new List<int>();
        foreach (var e in edges.Distinct())
        {
            if (!edgeFaces.TryGetValue(e, out var fs) || fs.Count != 1) continue;
            var face = m.Faces[fs[0]];
            var v = face.Verts;
            int i = Array.IndexOf(v, e.A);
            bool forward = v[(i + 1) % v.Length] == e.B;
            int from = forward ? e.A : e.B, to = forward ? e.B : e.A;
            int nf = Dup(from), nt = Dup(to);
            sides.Add(m.Faces.Count);
            m.Faces.Add(new Face([to, from, nf, nt], face.Material, face.Map));
            newEdges.Add(new Edge(nf, nt));
        }
        return (newEdges, sides);
    }
}
