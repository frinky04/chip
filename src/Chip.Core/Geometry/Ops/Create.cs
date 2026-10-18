using System.Numerics;
using Chip.Core.Texturing;

namespace Chip.Core.Geometry;

public static partial class MeshOps
{
    public static PolyMesh CreateBox(Vector3 a, Vector3 b, string material, TextureMapping scaleFrom)
    {
        var min = Vector3.Min(a, b);
        var max = Vector3.Max(a, b);
        var m = new PolyMesh { Name = "block" };
        for (int i = 0; i < 8; i++)
            m.Positions.Add(new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z));
        int[][] loops =
        [
            [0, 4, 6, 2], // -X
            [1, 3, 7, 5], // +X
            [0, 1, 5, 4], // -Y
            [2, 6, 7, 3], // +Y
            [0, 2, 3, 1], // -Z
            [4, 5, 7, 6], // +Z
        ];
        foreach (var l in loops) m.Faces.Add(new Face(l, material, default));
        AlignToGrid(m, Enumerable.Range(0, m.Faces.Count), scaleFrom);
        return m;
    }

    /// <summary>World-aligns the given faces, keeping each face's scale (or <paramref name="scaleFrom"/>'s when given).</summary>
    public static void AlignToGrid(PolyMesh m, IEnumerable<int> faces, TextureMapping? scaleFrom = null)
    {
        foreach (var f in faces) m.Faces[f].Map = TextureMapping.AlignToGrid(m.FaceNormal(f), scaleFrom ?? m.Faces[f].Map);
    }
}
