using Chip.Core.Geometry;
using Chip.Core.Texturing;

namespace Chip.Core.Scene;

/// <summary>
/// Tool materials (Source/Hammer conventions) change how faces export instead of how they look:
/// <list type="bullet">
/// <item><see cref="NoDraw"/>: never rendered; still solid, so it joins the mesh's collision when the mesh collides.</item>
/// <item><see cref="Clip"/>: collision only (player clip), whatever the mesh's collision setting.</item>
/// <item><see cref="Trigger"/>: a trigger volume, exported as its own collision-only node tagged for the engine.</item>
/// <item><see cref="Skip"/>: dropped from the export entirely.</item>
/// </list>
/// </summary>
public enum ToolMaterial { None, NoDraw, Clip, Trigger, Skip }

/// <summary>Tool materials, and material edits over faces and documents that respect per-material texel density.</summary>
public sealed partial class MaterialLibrary
{
    /// <summary>Names of the bundled tool materials, in browser order.</summary>
    public static readonly string[] ToolNames = ["nodraw", "clip", "trigger", "skip"];

    /// <summary>Which tool material a name is. Accepts Chip's names and Hammer's <c>toolsnodraw</c> style.</summary>
    public static ToolMaterial ToolKind(string name)
    {
        var n = name.StartsWith("tools", StringComparison.OrdinalIgnoreCase) && name.Length > 5 ? name[5..] : name;
        return n.ToLowerInvariant() switch
        {
            "nodraw" => ToolMaterial.NoDraw,
            "clip" or "playerclip" => ToolMaterial.Clip,
            "trigger" => ToolMaterial.Trigger,
            "skip" => ToolMaterial.Skip,
            _ => ToolMaterial.None,
        };
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
