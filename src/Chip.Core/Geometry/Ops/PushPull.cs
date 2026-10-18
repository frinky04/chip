using System.Numerics;

namespace Chip.Core.Geometry;

public static partial class MeshOps
{
    /// <summary>Area-weighted average normal of a face set (zero if they cancel out).</summary>
    public static Vector3 RegionNormal(PolyMesh m, IEnumerable<int> faces)
    {
        var sum = Vector3.Zero;
        foreach (var f in faces) sum += m.FaceNormal(f) * MathF.Max(Topology.FaceArea(m, f), 1e-6f);
        return sum.LengthSquared() < 1e-12f ? Vector3.Zero : Vector3.Normalize(sum);
    }

    /// <summary>
    /// Push/pull setup (SketchUp-style extrude along <paramref name="dir"/>). An outline vertex whose surrounding faces
    /// all contain the push direction just slides with the region, stretching those faces, because a side face there
    /// would be coplanar with them. Every other outline vertex is duplicated and joined by side faces as in an extrude.
    /// So pushing a box's top changes its height without adding faces, while pulling a patch out of a floor extrudes it.
    /// Face indices stay; returns the vertices to move and the new side faces (zero area until moved).
    /// </summary>
    public static (HashSet<int> MoveVerts, List<int> SideFaces) PushPullFaces(PolyMesh m, IReadOnlyCollection<int> faces, Vector3 dir)
    {
        var region = faces.ToHashSet();
        dir = SafeNormalize(dir);
        var vertexFaces = Topology.VertexFaces(m);
        var directed = new HashSet<(int, int)>();
        foreach (var f in region)
        {
            var v = m.Faces[f].Verts;
            for (int i = 0; i < v.Length; i++) directed.Add((v[i], v[(i + 1) % v.Length]));
        }

        var normals = new Dictionary<int, Vector3>();
        Vector3 Normal(int f) => normals.TryGetValue(f, out var n) ? n : normals[f] = m.FaceNormal(f);
        bool Slides(int v) => vertexFaces[v].All(f => region.Contains(f) || MathF.Abs(Vector3.Dot(Normal(f), dir)) < 1e-3f);

        var remap = new Dictionary<int, int>();
        int Moved(int v)
        {
            if (remap.TryGetValue(v, out int nv)) return nv;
            if (Slides(v)) return remap[v] = v;
            nv = m.Positions.Count;
            m.Positions.Add(m.Positions[v]);
            return remap[v] = nv;
        }

        var sides = new List<Face>();
        foreach (var f in region)
        {
            var face = m.Faces[f];
            var v = face.Verts;
            for (int i = 0; i < v.Length; i++)
            {
                int a = v[i], b = v[(i + 1) % v.Length];
                if (directed.Contains((b, a))) continue;
                int na = Moved(a), nb = Moved(b);
                if (na == a && nb == b) continue;
                int[] loop = na == a ? [a, b, nb] : nb == b ? [a, b, na] : [a, b, nb, na];
                sides.Add(new Face(loop, face.Material, face.Map));
            }
        }
        foreach (var f in region)
            m.Faces[f].Verts = m.Faces[f].Verts.Select(Moved).ToArray();
        var sideIndices = new List<int>();
        foreach (var s in sides)
        {
            sideIndices.Add(m.Faces.Count);
            m.Faces.Add(s);
        }
        var move = region.SelectMany(f => m.Faces[f].Verts).ToHashSet();
        return (move, sideIndices);
    }

    /// <summary>
    /// After a push/pull: merges each side face into neighbours that ended up in the same plane with the same material,
    /// so walls stay single faces. <paramref name="keepApart"/> (the pushed faces) never merge. Returns the merge maps,
    /// or null when nothing merged.
    /// </summary>
    public static MergeFacesResult? MergeCoplanarSides(PolyMesh m, IEnumerable<int> sides, IReadOnlySet<int> keepApart)
    {
        var edgeFaces = Topology.EdgeFaces(m);
        var parent = new Dictionary<int, int>();
        int Find(int x) { while (parent.TryGetValue(x, out int p) && p != x) x = parent[x] = parent.GetValueOrDefault(p, p); return x; }
        bool any = false;
        foreach (var s in sides)
        {
            if (Topology.FaceArea(m, s) < 1e-8f) continue;
            var ns = m.FaceNormal(s);
            var ps = m.Positions[m.Faces[s].Verts[0]];
            foreach (var e in m.Faces[s].Edges())
                foreach (var g in edgeFaces[e])
                {
                    if (g == s || keepApart.Contains(g) || m.Faces[g].Material != m.Faces[s].Material) continue;
                    if (Vector3.Dot(m.FaceNormal(g), ns) < 0.9999f) continue;
                    if (MathF.Abs(Vector3.Dot(m.Positions[m.Faces[g].Verts[0]] - ps, ns)) > 1e-4f) continue;
                    int ra = Find(s), rb = Find(g);
                    if (ra == rb) continue;
                    parent[ra] = parent[rb] = Math.Min(ra, rb);
                    any = true;
                }
        }
        if (!any) return null;
        var groups = parent.Keys.GroupBy(Find).Select(g => (IReadOnlyList<int>)g.Order().ToList()).Where(g => g.Count > 1).ToList();
        return MergeFaceGroups(m, groups);
    }
}
