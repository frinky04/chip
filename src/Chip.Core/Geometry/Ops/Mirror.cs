using System.Numerics;

namespace Chip.Core.Geometry;

public static partial class MeshOps
{
    /// <summary>
    /// Reflects <paramref name="verts"/> across the plane through <paramref name="pivot"/> with normal
    /// <paramref name="axis"/>. Faces entirely inside the set flip winding (with their UVs) so they still face out;
    /// every touched face is re-aligned to the world grid, keeping its scale.
    /// </summary>
    public static void Mirror(PolyMesh m, IReadOnlyCollection<int> verts, Vector3 pivot, Vector3 axis)
    {
        var n = Vector3.Normalize(axis);
        var set = verts.ToHashSet();
        foreach (var v in set) m.Positions[v] -= 2 * Vector3.Dot(m.Positions[v] - pivot, n) * n;
        for (int f = 0; f < m.Faces.Count; f++)
        {
            var face = m.Faces[f];
            if (!face.Verts.Any(set.Contains)) continue;
            if (face.Verts.All(set.Contains))
            {
                Array.Reverse(face.Verts);
                if (face.UVs != null) Array.Reverse(face.UVs);
            }
            face.Map = Texturing.TextureMapping.AlignToGrid(m.FaceNormal(f), face.Map);
        }
    }
}
