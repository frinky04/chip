using System.Numerics;

namespace Chip.Core.Geometry;

/// <summary>Outcome of a face merge: old→new maps (-1 = removed), the merged faces' new indices, and regions left alone.</summary>
public sealed record MergeFacesResult(int[] VertexMap, int[] FaceMap, List<int> Merged, int Skipped);

public static partial class MeshOps
{
    /// <summary>Splits a face set into edge-connected regions (each sorted ascending).</summary>
    public static List<List<int>> FaceRegions(PolyMesh m, IEnumerable<int> faces)
    {
        var set = faces.ToHashSet();
        var edgeFaces = Topology.EdgeFaces(m);
        var seen = new HashSet<int>();
        var regions = new List<List<int>>();
        foreach (var start in set.Order())
        {
            if (!seen.Add(start)) continue;
            var region = new List<int>();
            var stack = new Stack<int>([start]);
            while (stack.Count > 0)
            {
                int f = stack.Pop();
                region.Add(f);
                foreach (var e in m.Faces[f].Edges())
                    foreach (var g in edgeFaces[e])
                        if (set.Contains(g) && seen.Add(g)) stack.Push(g);
            }
            region.Sort();
            regions.Add(region);
        }
        return regions;
    }

    /// <summary>
    /// Merge Faces: dissolves the edges shared by selected faces, turning each connected region into one n-gon that keeps
    /// the surface (material, mapping) of <paramref name="keep"/> when it is in the region, else of the region's first
    /// face. Regions whose outline isn't a single loop (holes, pinches) are skipped.
    /// </summary>
    public static MergeFacesResult MergeFaces(PolyMesh m, IEnumerable<int> faces, int? keep = null) =>
        MergeFaceGroups(m, FaceRegions(m, faces).Select(r => keep is { } k && r.Contains(k) ? r.Where(f => f != k).Prepend(k).ToList() : r));

    /// <summary>
    /// Merges each group of faces into one face (the group's first face keeps its index slot and surface). Groups must be
    /// edge-connected and disjoint. Corners left collinear and unused by any other face are dropped.
    /// </summary>
    public static MergeFacesResult MergeFaceGroups(PolyMesh m, IEnumerable<IReadOnlyList<int>> groups)
    {
        var remove = new HashSet<int>();
        var mains = new List<int>();
        int skipped = 0;
        foreach (var group in groups)
        {
            if (group.Count < 2) continue;
            if (MergeLoop(m, group) is not { } merged) { skipped++; continue; }
            int main = group[0];
            m.Faces[main].Verts = merged.Verts;
            m.Faces[main].UVs = merged.UVs;
            mains.Add(main);
            foreach (var f in group.Skip(1)) remove.Add(f);
        }

        var faceMap = new int[m.Faces.Count];
        var kept = new List<Face>();
        for (int f = 0; f < m.Faces.Count; f++)
        {
            faceMap[f] = remove.Contains(f) ? -1 : kept.Count;
            if (!remove.Contains(f)) kept.Add(m.Faces[f]);
        }
        m.Faces = kept;
        DissolveStraightVertices(m, mains.SelectMany(f => m.Faces[faceMap[f]].Verts).ToHashSet());
        var vertexMap = RemoveUnusedVertices(m);
        return new MergeFacesResult(vertexMap, faceMap, mains.Select(f => faceMap[f]).ToList(), skipped);
    }

    /// <summary>The outline of a face region as one loop (with corner UVs when every face has them), or null.</summary>
    static (int[] Verts, Vector2[]? UVs)? MergeLoop(PolyMesh m, IReadOnlyList<int> group)
    {
        var directed = new HashSet<(int, int)>();
        foreach (var f in group)
        {
            var v = m.Faces[f].Verts;
            for (int i = 0; i < v.Length; i++) directed.Add((v[i], v[(i + 1) % v.Length]));
        }
        bool withUVs = group.All(f => m.Faces[f].UVs != null);
        var next = new Dictionary<int, (int To, Vector2 UV)>();
        foreach (var f in group)
        {
            var face = m.Faces[f];
            var v = face.Verts;
            for (int i = 0; i < v.Length; i++)
            {
                int a = v[i], b = v[(i + 1) % v.Length];
                if (directed.Contains((b, a))) continue;
                if (!next.TryAdd(a, (b, withUVs ? face.UVs![i] : default))) return null; // pinch: two outlines meet at a vertex
            }
        }
        if (next.Count < 3) return null;
        var loop = new List<(int V, Vector2 UV)>();
        int start = next.Keys.Min(), cur = start;
        do
        {
            if (!next.TryGetValue(cur, out var step) || loop.Count > next.Count) return null;
            loop.Add((cur, step.UV));
            cur = step.To;
        } while (cur != start);
        if (loop.Count != next.Count) return null; // more than one outline: the region has a hole
        return (loop.Select(c => c.V).ToArray(), withUVs ? loop.Select(c => c.UV).ToArray() : null);
    }

    /// <summary>
    /// Removes each candidate vertex that sits straight-through on an edge in every face using it, with the same two
    /// neighbours in all of them (left behind when the edges it split were dissolved). Faces stay at three corners or more.
    /// </summary>
    static void DissolveStraightVertices(PolyMesh m, IReadOnlySet<int> candidates)
    {
        var vertexFaces = Topology.VertexFaces(m);
        foreach (var v in candidates)
        {
            var uses = vertexFaces[v];
            if (uses.Count == 0) continue;
            (int Prev, int Next)? pair = null;
            bool ok = true;
            foreach (var f in uses)
            {
                var verts = m.Faces[f].Verts;
                int i = Array.IndexOf(verts, v);
                int prev = verts[(i + verts.Length - 1) % verts.Length], next = verts[(i + 1) % verts.Length];
                var d1 = m.Positions[v] - m.Positions[prev];
                var d2 = m.Positions[next] - m.Positions[v];
                bool straight = Vector3.Cross(d1, d2).Length() <= 1e-5f * d1.Length() * d2.Length() && Vector3.Dot(d1, d2) > 0;
                var key = (Math.Min(prev, next), Math.Max(prev, next));
                if (!straight || verts.Length <= 3 || verts.Count(x => x == v) > 1 || (pair is { } p && p != key)) { ok = false; break; }
                pair = key;
            }
            if (!ok) continue;
            foreach (var f in uses)
            {
                var face = m.Faces[f];
                int i = Array.IndexOf(face.Verts, v);
                face.Verts = face.Verts.Where((_, k) => k != i).ToArray();
                face.UVs = face.UVs?.Where((_, k) => k != i).ToArray();
            }
            var soft = m.SoftEdges.Where(e => e.A == v || e.B == v).ToList();
            if (soft.Count == 2) m.SoftEdges.Add(new Edge(pair!.Value.Prev, pair.Value.Next));
            m.SoftEdges.ExceptWith(soft);
        }
    }
}
