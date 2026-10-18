namespace Chip.Core.IO;

/// <summary>
/// Per-document export options beyond scale, path and default collision (which live on <c>Document</c> itself).
/// Saved in the <c>.chip</c> file only when something differs from the defaults, which match the original exporter.
/// </summary>
public sealed class ExportSettings
{
    /// <summary>Smoothed normals across soft edges. Off exports every face flat.</summary>
    public bool Smoothing = true;
    /// <summary>Hidden meshes and entities are exported too (hiding is an editor view state, as in Hammer).</summary>
    public bool IncludeHidden = true;
    /// <summary>Images go inside the <c>.glb</c>. Off, the file references the image files instead (copied beside it when needed).</summary>
    public bool EmbedTextures = true;
    /// <summary>Watch mode: every successful save also re-exports to the document's export path.</summary>
    public bool ExportOnSave;
    /// <summary>Chip material name to an engine resource path (e.g. <c>res://materials/brick.tres</c>), written to glTF material extras.</summary>
    public Dictionary<string, string> MaterialRemap = new(StringComparer.OrdinalIgnoreCase);

    public bool IsDefault => Smoothing && IncludeHidden && EmbedTextures && !ExportOnSave && MaterialRemap.Count == 0;

    /// <summary>The engine path a material is remapped to, or null.</summary>
    public string? Remap(string material) =>
        MaterialRemap.TryGetValue(material, out var path) && !string.IsNullOrWhiteSpace(path) ? path.Trim() : null;

    public ExportSettings Clone() => new()
    {
        Smoothing = Smoothing, IncludeHidden = IncludeHidden, EmbedTextures = EmbedTextures, ExportOnSave = ExportOnSave,
        MaterialRemap = new Dictionary<string, string>(MaterialRemap, StringComparer.OrdinalIgnoreCase),
    };
}
