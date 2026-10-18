namespace Chip.Core.Scene;

/// <summary>The level's texel density ("1 m is 64 px"): changing it keeps every face that follows it in step.</summary>
public static class TexelDensityOps
{
    /// <summary>
    /// Sets <see cref="Document.TexelDensity"/> to <paramref name="pixelsPerMetre"/> and rescales the faces whose material
    /// follows the level (no density of its own): projected faces change their metres-per-texel scale, explicit UVs
    /// scale by the same ratio, so a texture that tiled every 1 m at 512 px/m tiles every 8 m at 64 px/m. Faces using a
    /// texture with its own density are left alone. Updates <paramref name="materials"/>' default too.
    /// Returns the number of faces rescaled. Run it inside a session step.
    /// </summary>
    public static int Change(Document doc, MaterialLibrary materials, float pixelsPerMetre)
    {
        if (pixelsPerMetre <= 0 || float.IsNaN(pixelsPerMetre) || float.IsInfinity(pixelsPerMetre))
            throw new ArgumentOutOfRangeException(nameof(pixelsPerMetre));
        float old = doc.TexelDensity;
        doc.TexelDensity = pixelsPerMetre;
        materials.DefaultTexelsPerMeter = pixelsPerMetre;
        if (old == pixelsPerMetre) return 0;

        float uv = pixelsPerMetre / old;   // texture repeats per metre scale with the density
        int changed = 0;
        foreach (var mesh in doc.Meshes)
            foreach (var face in mesh.Faces)
            {
                if (materials.Get(face.Material).TexelDensity != null) continue;
                if (face.UVs is { } uvs)
                    for (int i = 0; i < uvs.Length; i++) uvs[i] *= uv;
                face.Map = face.Map with { ScaleU = face.Map.ScaleU / uv, ScaleV = face.Map.ScaleV / uv };
                changed++;
            }
        return changed;
    }
}
