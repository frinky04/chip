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
    /// Smooth-shading normal for every face corner. Around each vertex, faces joined across <see cref="PolyMesh.SoftEdges"/>
    /// that meet at that vertex form a fan; a corner averages the (area-weighted) normals of its fan. Hard edges split fans
    /// per vertex, so a single hard seam through an otherwise soft surface still creases. If a fan contains
    /// <see cref="Face.Dominant"/> faces, they keep their flat normal and the other faces take the dominant faces' average
    /// (Scythe dominant normals). With no soft edges this is exactly the flat face normal.
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
        for (int f = 0; f < m.Faces.Count; f++) result[f] = Enumerable.Repeat(faceNormals[f], m.Faces[f].Verts.Length).ToArray();
        if (m.SoftEdges.Count == 0) return result;

        var vertexFaces = VertexFaces(m);
        var parent = new List<int>();
        var firstOnEdge = new Dictionary<int, int>();
        for (int v = 0; v < vertexFaces.Length; v++)
        {
            var fan = vertexFaces[v];
            if (fan.Count < 2) continue;

            // Local union-find over the faces at v, joined where two of them share a soft edge (v, w).
            parent.Clear();
            for (int i = 0; i < fan.Count; i++) parent.Add(i);
            int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            firstOnEdge.Clear();
            for (int i = 0; i < fan.Count; i++)
            {
                var verts = m.Faces[fan[i]].Verts;
                for (int c = 0; c < verts.Length; c++)
                {
                    if (verts[c] != v) continue;
                    foreach (int w in (ReadOnlySpan<int>)[verts[(c + verts.Length - 1) % verts.Length], verts[(c + 1) % verts.Length]])
                    {
                        if (w == v || !m.SoftEdges.Contains(new Edge(v, w))) continue;
                        if (firstOnEdge.TryGetValue(w, out int j)) parent[Find(i)] = Find(j);
                        else firstOnEdge[w] = i;
                    }
                }
            }
            if (firstOnEdge.Count == 0) continue;

            for (int i = 0; i < fan.Count; i++)
            {
                int f = fan[i];
                if (m.Faces[f].Dominant) continue; // dominant faces stay flat
                int root = Find(i);
                Vector3 all = Vector3.Zero, dominant = Vector3.Zero;
                for (int j = 0; j < fan.Count; j++)
                {
                    if (Find(j) != root) continue;
                    all += weighted[fan[j]];
                    if (m.Faces[fan[j]].Dominant) dominant += weighted[fan[j]];
                }
                var sum = dominant.LengthSquared() > 1e-12f ? dominant : all;
                var n = sum.LengthSquared() > 1e-12f ? Vector3.Normalize(sum) : faceNormals[f];
                var verts = m.Faces[f].Verts;
                for (int c = 0; c < verts.Length; c++)
                    if (verts[c] == v) result[f][c] = n;
            }
        }
        return result;
    }

    /// <summary>
    /// Angle in degrees between the normals of the two faces on <paramref name="e"/> (0 = coplanar), or null when the edge
    /// isn't shared by exactly two faces (open or non-manifold edges have no dihedral angle).
    /// </summary>
    public static float? EdgeAngle(PolyMesh m, Edge e, Dictionary<Edge, List<int>> edgeFaces)
    {
        if (!edgeFaces.TryGetValue(e, out var faces) || faces.Count != 2) return null;
        float dot = Math.Clamp(Vector3.Dot(m.FaceNormal(faces[0]), m.FaceNormal(faces[1])), -1f, 1f);
        return MathF.Acos(dot) * 180f / MathF.PI;
    }

    public static float FaceArea(PolyMesh m, int face)
    {
        var v = m.Faces[face].Verts;
        var sum = Vector3.Zero;
        for (int i = 0; i < v.Length; i++) sum += Vector3.Cross(m.Positions[v[i]], m.Positions[v[(i + 1) % v.Length]]);
        return sum.Length() * 0.5f;
    }
}
