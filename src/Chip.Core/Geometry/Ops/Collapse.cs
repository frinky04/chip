using System.Numerics;

namespace Chip.Core.Geometry;

public static partial class MeshOps
{
    /// <summary>
    /// Collapses each group of vertices to its centre (Hammer "Collapse"). Faces that drop below three corners are
    /// removed. Returns old→new vertex and face maps (-1 = removed) and the surviving vertex of each group.
    /// </summary>
    public static (int[] VertexMap, int[] FaceMap, List<int> Points) Collapse(PolyMesh m, IEnumerable<IEnumerable<int>> groups)
    {
        var weld = Enumerable.Range(0, m.Positions.Count).ToArray();
        var reps = new List<int>();
        foreach (var g in groups)
        {
            var verts = g.Distinct().Where(v => v >= 0 && v < m.Positions.Count).ToList();
            if (verts.Count == 0) continue;
            var centre = verts.Aggregate(Vector3.Zero, (s, v) => s + m.Positions[v]) / verts.Count;
            int rep = verts.Min();
            m.Positions[rep] = centre;
            foreach (var v in verts) weld[v] = rep;
            reps.Add(rep);
        }
        var (vmap, fmap) = MergeVertices(m, weld);
        return (vmap, fmap, reps.Select(r => vmap[r]).Where(r => r >= 0).Distinct().ToList());
    }

    /// <summary>
    /// Applies a weld map (vertex → representative, where a representative maps to itself): faces follow their
    /// corners, repeated corners collapse (explicit UVs travel with their corner), faces under three corners go away,
    /// and unused vertices are compacted. Returns old→new vertex and face maps (-1 = removed).
    /// </summary>
    public static (int[] VertexMap, int[] FaceMap) MergeVertices(PolyMesh m, int[] weld)
    {
        weld = weld.Select(w => { for (int guard = 0; weld[w] != w && guard < weld.Length; guard++) w = weld[w]; return w; }).ToArray();
        m.SoftEdges = m.SoftEdges.Where(e => weld[e.A] != weld[e.B]).Select(e => new Edge(weld[e.A], weld[e.B])).ToHashSet();
        var faceMap = new int[m.Faces.Count];
        var kept = new List<Face>();
        for (int f = 0; f < m.Faces.Count; f++)
        {
            var face = m.Faces[f];
            var loop = new List<(int V, int Corner)>();
            for (int i = 0; i < face.Verts.Length; i++)
            {
                int v = weld[face.Verts[i]];
                if (loop.Count == 0 || loop[^1].V != v) loop.Add((v, i));
            }
            while (loop.Count > 1 && loop[0].V == loop[^1].V) loop.RemoveAt(loop.Count - 1);
            if (loop.Select(c => c.V).Distinct().Count() < 3) { faceMap[f] = -1; continue; }
            if (face.UVs is { } uvs) face.UVs = loop.Select(c => uvs[c.Corner]).ToArray();
            face.Verts = loop.Select(c => c.V).ToArray();
            faceMap[f] = kept.Count;
            kept.Add(face);
        }
        m.Faces = kept;
        var compact = RemoveUnusedVertices(m);
        return (weld.Select(w => compact[w]).ToArray(), faceMap);
    }
}
