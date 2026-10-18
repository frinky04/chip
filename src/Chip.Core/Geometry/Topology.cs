using System.Numerics;

namespace Chip.Core.Geometry;

/// <summary>Read-only adjacency queries shared by ops, selection tools and normals. Build once per edit, not per call.</summary>
public static class Topology
{
    /// <summary>Faces using each edge (1 = boundary, 2 = manifold interior, more = non-manifold).</summary>
    public static Dictionary<Edge, List<int>> EdgeFaces(PolyMesh m)
    {
        var map = new Dictionary<Edge, List<int>>();
        for (int f = 0; f < m.Faces.Count; f++)
            foreach (var e in m.Faces[f].Edges())
            {
                if (!map.TryGetValue(e, out var list)) map[e] = list = new List<int>(2);
                list.Add(f);
            }
        return map;
    }

    /// <summary>Faces touching each vertex.</summary>
    public static List<int>[] VertexFaces(PolyMesh m)
    {
        var map = new List<int>[m.Positions.Count];
        for (int i = 0; i < map.Length; i++) map[i] = new List<int>(4);
        for (int f = 0; f < m.Faces.Count; f++)
            foreach (var v in m.Faces[f].Verts.Distinct()) map[v].Add(f);
        return map;
    }

    /// <summary>Faces sharing an edge with <paramref name="face"/>.</summary>
    public static IEnumerable<int> FaceNeighbors(PolyMesh m, int face, Dictionary<Edge, List<int>> edgeFaces) =>
        m.Faces[face].Edges().SelectMany(e => edgeFaces[e]).Where(f => f != face).Distinct();

    /// <summary>
    /// Smooth-shading normal for every face corner. Faces joined across <see cref="PolyMesh.SoftEdges"/> form smoothing
    /// groups; a corner averages the (area-weighted) normals of faces in its group that share the vertex. With no soft edges
    /// this is exactly the flat face normal.
    /// </summary>
    public static Vector3[][] CornerNormals(PolyMesh m)
    {
        var faceNormals = new Vector3[m.Faces.Count];
        var weighted = new Vector3[m.Faces.Count];
        for (int f = 0; f < m.Faces.Count; f++)
        {
            faceNormals[f] = m.FaceNormal(f);
            weighted[f] = faceNormals[f] * MathF.Max(FaceArea(m, f), 1e-6f);
        }
        var result = new Vector3[m.Faces.Count][];
        if (m.SoftEdges.Count == 0)
        {
            for (int f = 0; f < m.Faces.Count; f++) result[f] = Enumerable.Repeat(faceNormals[f], m.Faces[f].Verts.Length).ToArray();
            return result;
        }

        // Union faces across soft edges into smoothing groups.
        var parent = Enumerable.Range(0, m.Faces.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        foreach (var (e, faces) in EdgeFaces(m))
            if (m.SoftEdges.Contains(e))
                for (int i = 1; i < faces.Count; i++) parent[Find(faces[i])] = Find(faces[0]);

        var vertexFaces = VertexFaces(m);
        for (int f = 0; f < m.Faces.Count; f++)
        {
            var verts = m.Faces[f].Verts;
            var normals = new Vector3[verts.Length];
            int group = Find(f);
            for (int i = 0; i < verts.Length; i++)
            {
                var sum = Vector3.Zero;
                foreach (var g in vertexFaces[verts[i]])
                    if (Find(g) == group) sum += weighted[g];
                normals[i] = sum.LengthSquared() > 1e-12f ? Vector3.Normalize(sum) : faceNormals[f];
            }
            result[f] = normals;
        }
        return result;
    }

    public static float FaceArea(PolyMesh m, int face)
    {
        var v = m.Faces[face].Verts;
        var sum = Vector3.Zero;
        for (int i = 0; i < v.Length; i++) sum += Vector3.Cross(m.Positions[v[i]], m.Positions[v[(i + 1) % v.Length]]);
        return sum.Length() * 0.5f;
    }
}
