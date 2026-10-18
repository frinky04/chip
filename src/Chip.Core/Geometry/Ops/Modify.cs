using Chip.Core.Texturing;

namespace Chip.Core.Geometry;

public static partial class MeshOps
{
    /// <summary>Reverses face winding (normals point the other way); explicit UVs are reversed with the corners.</summary>
    public static void FlipFaces(PolyMesh m, IEnumerable<int> faces)
    {
        foreach (var f in faces)
        {
            var face = m.Faces[f];
            Array.Reverse(face.Verts);
            if (face.UVs != null) Array.Reverse(face.UVs);
            face.Map = TextureMapping.AlignToGrid(m.FaceNormal(f), face.Map);
        }
    }

    /// <summary>Copies faces (with fresh vertices) into the same mesh. Returns the new face indices.</summary>
    public static List<int> DuplicateFaces(PolyMesh m, IEnumerable<int> faces)
    {
        var remap = new Dictionary<int, int>();
        var added = new List<int>();
        foreach (var f in faces.ToList())
        {
            var src = m.Faces[f];
            var verts = src.Verts.Select(v =>
            {
                if (!remap.TryGetValue(v, out var nv)) { nv = m.Positions.Count; m.Positions.Add(m.Positions[v]); remap[v] = nv; }
                return nv;
            }).ToArray();
            added.Add(m.Faces.Count);
            m.Faces.Add(new Face(verts, src.Material, src.Map) { UVs = (System.Numerics.Vector2[]?)src.UVs?.Clone(), Dominant = src.Dominant });
        }
        return added;
    }

    public static void SnapToGrid(PolyMesh m, IEnumerable<int> verts, float grid)
    {
        foreach (var v in verts) m.Positions[v] = Snap(m.Positions[v], grid);
    }
}
