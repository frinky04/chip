using System.Numerics;

namespace Chip.Core.Geometry;

/// <summary>Outcome of a dissolve: old→new maps (-1 = removed), how many elements went, and how many were left alone.</summary>
public sealed record DissolveResult(int[] VertexMap, int[] FaceMap, int Dissolved, int Skipped);

public static partial class MeshOps
{
    /// <summary>Faces count as one plane for dissolving when their normals are within about a degree.</summary>
    const float CoplanarDot = 0.9998f;

    /// <summary>
    /// Removes vertices without removing the faces round them (Blender's Dissolve Vertices). A vertex between just two
    /// edges leaves every face using it, so those two edges become one: a stray point left on a straight edge simply
    /// goes. A vertex where more edges meet first merges the faces round it into one n-gon (when they lie in one plane;
    /// otherwise it is skipped, since that would bend one face round a corner), and then leaves that face. Faces that
    /// drop below three corners are removed, along with any straight-through corners the merge left behind.
    /// </summary>
    public static DissolveResult DissolveVertices(PolyMesh m, IEnumerable<int> verts)
    {
        var dead = new HashSet<int>();
        var merged = new List<int>();
        int dissolved = 0, skipped = 0;
        foreach (int v in verts.Distinct())
        {
            var uses = Enumerable.Range(0, m.Faces.Count).Where(f => !dead.Contains(f) && m.Faces[f].Verts.Contains(v)).ToList();
            if (uses.Count == 0 || uses.Any(f => m.Faces[f].Verts.Count(x => x == v) > 1)) { skipped++; continue; }
            var around = new HashSet<int>();
            foreach (int f in uses)
            {
                var loop = m.Faces[f].Verts;
                int i = Array.IndexOf(loop, v);
                around.Add(loop[(i + loop.Length - 1) % loop.Length]);
                around.Add(loop[(i + 1) % loop.Length]);
            }

            if (around.Count > 2)
            {
                var n0 = m.FaceNormal(uses[0]);
                if (uses.Any(f => Vector3.Dot(m.FaceNormal(f), n0) < CoplanarDot) || MergeLoop(m, uses) is not { } outline) { skipped++; continue; }
                m.Faces[uses[0]].Verts = outline.Verts;
                m.Faces[uses[0]].UVs = outline.UVs;
                foreach (int f in uses.Skip(1)) dead.Add(f);
                merged.Add(uses[0]);
                uses = [uses[0]];
            }
            else if (m.SoftEdges.Contains(new Edge(v, around.First())) && m.SoftEdges.Contains(new Edge(v, around.Last())))
                m.SoftEdges.Add(new Edge(around.First(), around.Last()));

            // Whatever still uses the vertex has it as a plain corner between two neighbours: take it out.
            foreach (int f in uses)
            {
                var face = m.Faces[f];
                int i = Array.IndexOf(face.Verts, v);
                if (i < 0) continue; // an inner vertex: the merge already dissolved it
                if (face.Verts.Length <= 3)
                {
                    if (!merged.Contains(f)) dead.Add(f); // a merged region keeps its last corner rather than vanish
                    continue;
                }
                face.Verts = face.Verts.Where((_, k) => k != i).ToArray();
                face.UVs = face.UVs?.Where((_, k) => k != i).ToArray();
            }
            m.SoftEdges.RemoveWhere(e => e.A == v || e.B == v);
            dissolved++;
        }

        var faceMap = new int[m.Faces.Count];
        var kept = new List<Face>();
        for (int f = 0; f < m.Faces.Count; f++)
        {
            faceMap[f] = dead.Contains(f) ? -1 : kept.Count;
            if (!dead.Contains(f)) kept.Add(m.Faces[f]);
        }
        m.Faces = kept;
        DissolveStraightVertices(m, merged.Where(f => faceMap[f] >= 0).SelectMany(f => m.Faces[faceMap[f]].Verts).ToHashSet());
        return new DissolveResult(RemoveUnusedVertices(m), faceMap, dissolved, skipped);
    }

    /// <summary>
    /// Removes edges by merging the two faces on each (Merge Faces for edges): faces joined through selected edges become
    /// one n-gon, and endpoints left straight-through go too. Open edges, edges with more than two faces, and merges that
    /// would enclose a hole are skipped.
    /// </summary>
    public static DissolveResult DissolveEdges(PolyMesh m, IEnumerable<Edge> edges)
    {
        var edgeFaces = Topology.EdgeFaces(m);
        var parent = Enumerable.Range(0, m.Faces.Count).ToArray();
        int Find(int x) => parent[x] == x ? x : parent[x] = Find(parent[x]);
        var joined = new List<int>(); // a face of each edge's pair
        int skipped = 0;
        foreach (var e in edges.Distinct())
        {
            if (!edgeFaces.TryGetValue(e, out var fs) || fs.Count != 2 || fs[0] == fs[1]) { skipped++; continue; }
            parent[Find(fs[1])] = Find(fs[0]);
            joined.Add(fs[0]);
        }
        var groups = Enumerable.Range(0, m.Faces.Count).GroupBy(Find).Where(g => g.Count() > 1).Select(g => (IReadOnlyList<int>)g.Order().ToList()).ToList();
        var roots = joined.Select(Find).ToList();
        var mains = groups.ToDictionary(g => Find(g[0]), g => g[0]);
        var r = MergeFaceGroups(m, groups);
        var ok = r.Merged.ToHashSet();
        int dissolved = roots.Count(root => ok.Contains(r.FaceMap[mains[root]]));
        return new DissolveResult(r.VertexMap, r.FaceMap, dissolved, skipped + roots.Count - dissolved);
    }
}
