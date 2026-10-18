using System.Numerics;

namespace Chip.Core.Geometry;

public static partial class MeshOps
{
    /// <summary>Removes faces and any vertices left unused. Vertex indices are compacted.</summary>
    public static void DeleteFaces(PolyMesh m, IEnumerable<int> faces)
    {
        var kill = faces.ToHashSet();
        m.Faces = m.Faces.Where((_, i) => !kill.Contains(i)).ToList();
        RemoveUnusedVertices(m);
    }

    /// <summary>Compacts away vertices no face uses. Returns old→new vertex indices (-1 = removed).</summary>
    public static int[] RemoveUnusedVertices(PolyMesh m)
    {
        var used = m.Faces.SelectMany(f => f.Verts).ToHashSet();
        var remap = new int[m.Positions.Count];
        var kept = new List<Vector3>();
        for (int i = 0; i < m.Positions.Count; i++)
        {
            remap[i] = used.Contains(i) ? kept.Count : -1;
            if (used.Contains(i)) kept.Add(m.Positions[i]);
        }
        Reindex(m, remap, kept);
        return remap;
    }
}
