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
    /// <summary>
    /// Bake world collision (Source 2 style): the colliders of Default-collision meshes are merged, welded, stripped of
    /// faces that touch back to back, merged where coplanar, and turned into convex hulls where a mesh is convex. Off,
    /// each mesh's render geometry is its collider (<c>-col</c>), as before.
    /// </summary>
    public bool BakeCollision = true;
    /// <summary>
    /// With <see cref="BakeCollision"/>: closed convex meshes become their own convex hulls instead of joining the merged
    /// world mesh. Hulls are robust and fast, but separate shapes keep the seams between them (floor tiles), so it's off.
    /// </summary>
    public bool CollisionHulls;
    /// <summary>Cut render faces into a world-aligned grid for per-vertex lighting. Colliders always use the original geometry.</summary>
    public bool Tessellate;
    /// <summary>Grid spacing in metres for <see cref="Tessellate"/>.</summary>
    public float TessellateSpacing = 1f;
    /// <summary>Chip material name to an engine resource path (e.g. <c>res://materials/brick.tres</c>), written to glTF material extras.</summary>
    public Dictionary<string, string> MaterialRemap = new(StringComparer.OrdinalIgnoreCase);

    public bool IsDefault => Smoothing && IncludeHidden && EmbedTextures && !ExportOnSave && MaterialRemap.Count == 0
        && BakeCollision && !CollisionHulls && !Tessellate && TessellateSpacing == 1f;

    /// <summary>The engine path a material is remapped to, or null.</summary>
    public string? Remap(string material) =>
        MaterialRemap.TryGetValue(material, out var path) && !string.IsNullOrWhiteSpace(path) ? path.Trim() : null;

    public ExportSettings Clone() => new()
    {
        Smoothing = Smoothing, IncludeHidden = IncludeHidden, EmbedTextures = EmbedTextures, ExportOnSave = ExportOnSave,
        BakeCollision = BakeCollision, CollisionHulls = CollisionHulls, Tessellate = Tessellate, TessellateSpacing = TessellateSpacing,
        MaterialRemap = new Dictionary<string, string>(MaterialRemap, StringComparer.OrdinalIgnoreCase),
    };
}
