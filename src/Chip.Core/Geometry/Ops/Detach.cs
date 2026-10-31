namespace Chip.Core.Geometry;

public static partial class MeshOps
{
    /// <summary>
    /// A new mesh holding copies of <paramref name="faces"/> (compact vertices, same surfaces and UVs, soft edges between
    /// copied faces, same flags). The source is unchanged; Detach and Extract delete the faces afterwards.
    /// The copy has no id yet: add it to the document.
    /// </summary>
    public static PolyMesh CopyFaces(PolyMesh m, IEnumerable<int> faces)
    {
        var copy = new PolyMesh { Name = m.Name, Collision = m.Collision };
        var map = new Dictionary<int, int>();
        foreach (var f in faces.Distinct().Order())
        {
            var src = m.Faces[f];
            var verts = src.Verts.Select(v =>
            {
                if (!map.TryGetValue(v, out int nv)) { nv = copy.Positions.Count; copy.Positions.Add(m.Positions[v]); map[v] = nv; }
                return nv;
            }).ToArray();
            var face = src.Clone();
            face.Verts = verts;
            copy.Faces.Add(face);
        }
        var live = copy.Edges().ToHashSet();
        foreach (var e in m.SoftEdges)
            if (map.TryGetValue(e.A, out int a) && map.TryGetValue(e.B, out int b) && live.Contains(new Edge(a, b)))
                copy.SoftEdges.Add(new Edge(a, b));
        return copy;
    }

    /// <summary>Merge Meshes: appends every face of <paramref name="others"/> to <paramref name="target"/> (vertices are not welded).</summary>
    public static void MergeMeshes(PolyMesh target, IEnumerable<PolyMesh> others)
    {
        foreach (var o in others)
        {
            if (o == target) continue;
            int offset = target.Positions.Count;
            target.Positions.AddRange(o.Positions);
            foreach (var f in o.Faces)
            {
                var face = f.Clone();
                face.Verts = f.Verts.Select(v => v + offset).ToArray();
                target.Faces.Add(face);
            }
            foreach (var e in o.SoftEdges) target.SoftEdges.Add(new Edge(e.A + offset, e.B + offset));
        }
    }
}
