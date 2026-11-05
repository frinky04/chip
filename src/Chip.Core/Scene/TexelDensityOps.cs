namespace Chip.Core.Scene;

/// <summary>The level's texel density ("1 m is 256 px"): what new faces get, and an explicit step to bring existing faces along.</summary>
public static class TexelDensityOps
{
    /// <summary>
    /// Sets <see cref="Document.TexelDensity"/> to <paramref name="pixelsPerMetre"/> and updates <paramref name="materials"/>'
    /// default. Existing faces keep their mapping, so nothing already placed moves; new faces and resets to material density
    /// use the new value. Run it inside a session step.
    /// </summary>
    public static void Change(Document doc, MaterialLibrary materials, float pixelsPerMetre)
    {
        if (pixelsPerMetre <= 0 || float.IsNaN(pixelsPerMetre) || float.IsInfinity(pixelsPerMetre))
            throw new ArgumentOutOfRangeException(nameof(pixelsPerMetre));
        doc.TexelDensity = pixelsPerMetre;
        materials.DefaultTexelsPerMeter = pixelsPerMetre;
    }

    /// <summary>
    /// Rescales projected faces as if they had been built at density <paramref name="to"/> and not <paramref name="from"/>,
    /// so a texture that tiled every 1 m at 512 px/m tiles every 8 m at 64 px/m, and a hand-tuned scale stays in proportion.
    /// Faces with explicit UVs were placed by hand and stay as they are, as do faces using a texture with its own density.
    /// Returns the number of faces rescaled. Run it inside a session step.
    /// </summary>
    public static int Rescale(Document doc, MaterialLibrary materials, float from, float to)
    {
        if (from <= 0 || to <= 0 || !float.IsFinite(from) || !float.IsFinite(to)) throw new ArgumentOutOfRangeException(nameof(to));
        if (from == to) return 0;
        float ratio = to / from;
        int changed = 0;
        foreach (var mesh in doc.Meshes)
            foreach (var face in mesh.Faces)
            {
                if (face.UVs != null || materials.Get(face.Material).TexelDensity != null) continue;
                face.Map = face.Map with { ScaleU = face.Map.ScaleU / ratio, ScaleV = face.Map.ScaleV / ratio };
                changed++;
            }
        return changed;
    }
}
