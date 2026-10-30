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
    /// Push/pull setup (SketchUp-style extrude along <paramref name="dir"/>). A connected outline whose surrounding faces
    /// all contain the push direction slides with the region, stretching those faces. Otherwise the whole outline is
    /// duplicated and joined by quads: mixing sliding and fixed corners would cut diagonally across neighbouring faces.
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
        var outline = new Dictionary<int, HashSet<int>>();
        foreach (var (a, b) in directed)
        {
            if (directed.Contains((b, a))) continue;
            if (!outline.ContainsKey(a)) outline[a] = new();
            if (!outline.ContainsKey(b)) outline[b] = new();
            outline[a].Add(b);
            outline[b].Add(a);
        }
        // A non-sliding corner pins the entire connected outline. Decide before changing any face loops or normals.
        var pinned = outline.Keys.Where(v => vertexFaces[v].Any(f =>
            !region.Contains(f) && MathF.Abs(Vector3.Dot(Normal(f), dir)) >= 1e-3f)).ToHashSet();
        var pending = new Queue<int>(pinned);
        while (pending.TryDequeue(out int v))
            foreach (int neighbour in outline[v])
                if (pinned.Add(neighbour)) pending.Enqueue(neighbour);

        var remap = new Dictionary<int, int>();
        int Moved(int v)
        {
            if (remap.TryGetValue(v, out int nv)) return nv;
            if (!pinned.Contains(v)) return remap[v] = v;
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
                sides.Add(face.Derive([a, b, nb, na]));
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

    /// <summary>
    /// Absorbs extrusion strips into adjacent coplanar surfaces, including oppositely wound strips from inward pushes.
    /// The combined outline can fold back over itself: planar untangling removes its cancelled area and stitches the
    /// resulting rim into the other faces. Original surfaces supply material, mapping and tags, rather than the strips.
    /// </summary>
    public static MergeFacesResult? ResolvePushPullSides(PolyMesh m, IReadOnlyCollection<int> sides, IReadOnlySet<int> pushed)
    {
        var sideSet = sides.ToHashSet();
        var edgeFaces = Topology.EdgeFaces(m);
        bool Coplanar(int f, Vector3 normal, Vector3 point) =>
            MathF.Abs(Vector3.Dot(m.FaceNormal(f), normal)) >= 0.9999f &&
            m.Faces[f].Verts.All(v => MathF.Abs(Vector3.Dot(m.Positions[v] - point, normal)) <= 1e-4f);
        var owners = new Dictionary<int, int>();
        foreach (int s in sides)
        {
            if (Topology.FaceArea(m, s) < 1e-8f) continue;
            var normal = m.FaceNormal(s);
            var point = m.Positions[m.Faces[s].Verts[0]];
            foreach (int f in m.Faces[s].Edges().SelectMany(e => edgeFaces[e]))
                if (!sideSet.Contains(f) && !pushed.Contains(f) && Coplanar(f, normal, point))
                { owners[s] = f; break; }
        }
        bool SameSurface(int a, int b) => a == b ||
            (m.Faces[a].UVs == null && m.Faces[b].UVs == null &&
             m.Faces[a].Material == m.Faces[b].Material && m.Faces[a].Map.Equals(m.Faces[b].Map) &&
             m.Faces[a].Tags == m.Faces[b].Tags && m.Faces[a].Dominant == m.Faces[b].Dominant);
        var seen = new HashSet<int>();
        var groups = new List<IReadOnlyList<int>>();
        foreach (int side in sides)
        {
            if (seen.Contains(side) || !owners.TryGetValue(side, out int owner)) continue;
            var normal = m.FaceNormal(side);
            var point = m.Positions[m.Faces[side].Verts[0]];
            var group = new List<int>();
            var pending = new Queue<int>();
            pending.Enqueue(side);
            seen.Add(side);
            while (pending.TryDequeue(out int f))
            {
                group.Add(f);
                foreach (var e in m.Faces[f].Edges())
                    foreach (int g in edgeFaces[e])
                    {
                        if (seen.Contains(g) || pushed.Contains(g)) continue;
                        if (!Coplanar(g, normal, point)) continue;
                        // Keep existing material/tag/UV seams; each strip belongs to the surface at its original edge.
                        int surface = sideSet.Contains(g) ? owners.GetValueOrDefault(g, -1) : g;
                        if (surface < 0 || !SameSurface(owner, surface)) continue;
                        seen.Add(g);
                        pending.Enqueue(g);
                    }
            }
            if (group.Count > 1 && group.Any(f => !sideSet.Contains(f)))
                groups.Add(group.OrderBy(f => sideSet.Contains(f)).ThenBy(f => f).ToArray());
        }
        if (groups.Count == 0) return null;
        var surfaces = groups.Select(g => (Face: g[0], Original: m.Faces[g[0]].Clone(),
            Corners: m.FacePoints(g[0]).ToArray())).ToList();
        var merged = MergeFaceGroups(m, groups);
        foreach (var (face, original, corners) in surfaces)
            if (original.UVs is { } uvs && merged.FaceMap[face] is var target && target >= 0)
                m.Faces[target].UVs = m.Faces[target].Verts.Select(v => InterpolateUV(corners, uvs, m.Positions[v])).ToArray();
        var clean = CleanUp(m, 1e-5f);
        return new MergeFacesResult(
            merged.VertexMap.Select(v => v < 0 ? -1 : clean.VertexMap[v]).ToArray(),
            merged.FaceMap.Select(f => f < 0 ? -1 : clean.FaceMap[f]).ToArray(), [], merged.Skipped);
    }
}
