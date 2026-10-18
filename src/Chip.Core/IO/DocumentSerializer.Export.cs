namespace Chip.Core.IO;

/// <summary>
/// The optional <c>export</c> block of a <c>.chip</c> file (stream J). Absent when every option is at its default, so
/// files that don't use these options stay byte-identical; no version bump because older files simply lack it.
/// </summary>
public static partial class DocumentSerializer
{
    sealed record ExportDto(bool Smoothing = true, bool IncludeHidden = true, bool EmbedTextures = true, bool ExportOnSave = false,
        Dictionary<string, string>? MaterialRemap = null, bool BakeCollision = true, bool Tessellate = false, float TessellateSpacing = 1f,
        bool CollisionHulls = false);

    static ExportDto? ExportToDto(ExportSettings s) => s.IsDefault ? null : new ExportDto(
        s.Smoothing, s.IncludeHidden, s.EmbedTextures, s.ExportOnSave,
        s.MaterialRemap.Count > 0 ? s.MaterialRemap.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToDictionary() : null,
        s.BakeCollision, s.Tessellate, s.TessellateSpacing, s.CollisionHulls);

    static ExportSettings ExportFromDto(ExportDto? dto)
    {
        var s = new ExportSettings();
        if (dto == null) return s;
        s.Smoothing = dto.Smoothing;
        s.IncludeHidden = dto.IncludeHidden;
        s.EmbedTextures = dto.EmbedTextures;
        s.ExportOnSave = dto.ExportOnSave;
        s.BakeCollision = dto.BakeCollision;
        s.CollisionHulls = dto.CollisionHulls;
        s.Tessellate = dto.Tessellate;
        s.TessellateSpacing = dto.TessellateSpacing > 0 ? dto.TessellateSpacing : 1f;
        if (dto.MaterialRemap != null)
            foreach (var (k, v) in dto.MaterialRemap)
                if (!string.IsNullOrWhiteSpace(k)) s.MaterialRemap[k] = v ?? "";
        return s;
    }
}
