namespace Chip.Core.Geometry;

public static partial class MeshOps
{
    /// <summary>
    /// Extrudes a face region: the selected faces move onto duplicated vertices and every region border
    /// edge gets a bridging quad. Selected face indices are unchanged. Returns the vertices the caller should move
    /// and the new side faces, which have zero area until moved; world-align them afterwards with <see cref="AlignToGrid"/>.
    /// </summary>
    public static (HashSet<int> MoveVerts, List<int> SideFaces) ExtrudeFaces(PolyMesh m, IReadOnlyCollection<int> faces)
    {
        var region = faces.ToHashSet();
        var directed = new HashSet<(int, int)>();
        foreach (var f in region)
        {
            var v = m.Faces[f].Verts;
            for (int i = 0; i < v.Length; i++) directed.Add((v[i], v[(i + 1) % v.Length]));
        }

        var remap = new Dictionary<int, int>();
        int Dup(int v)
        {
            if (!remap.TryGetValue(v, out var nv))
            {
                nv = m.Positions.Count;
                m.Positions.Add(m.Positions[v]);
                remap[v] = nv;
            }
            return nv;
        }

        var sides = new List<Face>();
        foreach (var f in region)
        {
            var face = m.Faces[f];
            var v = face.Verts;
            for (int i = 0; i < v.Length; i++)
            {
                int a = v[i], b = v[(i + 1) % v.Length];
                if (directed.Contains((b, a))) continue; // interior edge of the region
                sides.Add(face.Derive([a, b, Dup(b), Dup(a)]));
            }
        }
        foreach (var f in region)
            m.Faces[f].Verts = m.Faces[f].Verts.Select(Dup).ToArray(); // corner order unchanged, so UVs still line up
        var sideIndices = new List<int>();
        foreach (var s in sides)
        {
            sideIndices.Add(m.Faces.Count);
            m.Faces.Add(s);
        }
        return (remap.Values.ToHashSet(), sideIndices);
    }
}
