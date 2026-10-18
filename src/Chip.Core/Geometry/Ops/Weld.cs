using System.Numerics;

namespace Chip.Core.Geometry;

public static partial class MeshOps
{
    /// <summary>
    /// Merges vertices that landed on each other (Hammer behaviour when dragging a vertex onto another) and drops
    /// faces that collapse below three corners. Returns old→new vertex and face maps (-1 = removed), or null if nothing merged.
    /// </summary>
    public static (int[] VertexMap, int[] FaceMap)? CollapseCoincident(PolyMesh m, float epsilon)
    {
        var weld = new int[m.Positions.Count];
        bool merged = false;
        for (int i = 0; i < weld.Length; i++)
        {
            weld[i] = i;
            for (int j = 0; j < i; j++)
                if (weld[j] == j && Vector3.DistanceSquared(m.Positions[i], m.Positions[j]) <= epsilon * epsilon)
                {
                    weld[i] = j;
                    merged = true;
                    break;
                }
        }
        if (!merged) return null;

        // Soft edges are stored by vertex pair, so translate them through the weld before faces change.
        m.SoftEdges = m.SoftEdges.Where(e => weld[e.A] != weld[e.B]).Select(e => new Edge(weld[e.A], weld[e.B])).ToHashSet();

        var faceMap = new int[m.Faces.Count];
        var kept = new List<Face>();
        for (int f = 0; f < m.Faces.Count; f++)
        {
            var face = m.Faces[f];
            // Carry each corner's UV along with its vertex while collapsing repeats.
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
