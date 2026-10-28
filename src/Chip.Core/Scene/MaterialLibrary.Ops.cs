using Chip.Core.Geometry;
using Chip.Core.Texturing;

namespace Chip.Core.Scene;

/// <summary>Material edits over faces and documents that respect per-material texel density. Tool materials are <see cref="Surfaces"/>.</summary>
public sealed partial class MaterialLibrary
{
    /// <summary>Removes a material added with <see cref="Add"/>. False when there was none.</summary>
    public bool Remove(string name)
    {
        if (_manual.RemoveAll(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) == 0) return false;
        Rescan();
        return true;
    }

    /// <summary>
    /// Gives a face a new material. When the two materials' texel densities differ, the mapping scale is rescaled by
    /// their ratio, so a face at the old material's density lands at the new one's (manual scale tweaks carry over).
    /// Explicit UVs are in tile space and stay as they are.
    /// </summary>
    public void Assign(Face face, string material)
    {
        float ratio = ScaleFor(material) / ScaleFor(face.Material);
        if (MathF.Abs(ratio - 1) > 1e-6f)
        {
            face.Map.ScaleU *= ratio;
            face.Map.ScaleV *= ratio;
        }
        face.Material = material;
    }

    /// <summary>Replaces <paramref name="from"/> with <paramref name="to"/> on every face of the document. Returns the face count.</summary>
    public int Replace(Document doc, string from, string to)
    {
        int n = 0;
        foreach (var mesh in doc.Meshes)
            foreach (var face in mesh.Faces)
                if (string.Equals(face.Material, from, StringComparison.OrdinalIgnoreCase))
                {
                    Assign(face, to);
                    n++;
                }
        return n;
    }

    /// <summary>Resets a face's mapping scale to its material's density, keeping axes, rotation, shift and flips.</summary>
    public void ApplyDensity(Face face)
    {
        float s = ScaleFor(face.Material);
        face.Map.ScaleU = face.Map.ScaleU < 0 ? -s : s;
        face.Map.ScaleV = face.Map.ScaleV < 0 ? -s : s;
    }

    /// <summary>World-aligns faces (dominant axis) at each face's material density. Density-aware form of <see cref="MeshOps.AlignToGrid"/>.</summary>
    public void AlignToGrid(PolyMesh mesh, IEnumerable<int> faces)
    {
        foreach (var f in faces)
            mesh.Faces[f].Map = TextureMapping.AlignToGrid(mesh.FaceNormal(f), MappingFor(mesh.Faces[f].Material));
    }

    /// <summary>Face count per material across the document, most used first.</summary>
    public static IReadOnlyList<(string Material, int Faces)> Usage(Document doc) =>
        doc.Meshes.SelectMany(m => m.Faces).GroupBy(f => f.Material, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToList();
}
