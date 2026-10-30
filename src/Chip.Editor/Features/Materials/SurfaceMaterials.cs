using Chip.Core.Scene;
using Chip.Editor.Framework;

namespace Chip.Editor.Features.Materials;

/// <summary>
/// Keeps the game's surfaces (<c>chip.project.json</c>) in the material library, so they're picked and applied like any
/// material. Built-in tool surfaces come from the bundled <c>assets/tools</c> images; a project surface (or a built-in it
/// restyles) is added in code, with its image or a tile drawn from its look, and removed when the project drops it.
/// </summary>
public sealed class SurfaceMaterials
{
    int _revision = -1;
    readonly HashSet<string> _added = new(StringComparer.OrdinalIgnoreCase);

    public void Update(EditorContext ctx)
    {
        if (EntityClasses.Revision == _revision) return;
        _revision = EntityClasses.Revision;
        var lib = ctx.Materials;
        var surfaces = (EntityClasses.Project?.Surfaces ?? []).ToList();
        if (!surfaces.Any(s => string.Equals(s.Name, "sky", StringComparison.OrdinalIgnoreCase)))
            surfaces.Add(Surfaces.BuiltIn.Single(s => s.Name == "sky"));
        foreach (var name in _added.Where(n => !surfaces.Any(s => string.Equals(s.Name, n, StringComparison.OrdinalIgnoreCase))).ToList())
        {
            lib.Remove(name);
            _added.Remove(name);
        }
        foreach (var s in surfaces)
        {
            int w = 64, h = 64;
            string? image = s.Look.Image is { } p && File.Exists(p) && MaterialLibrary.TryReadImageSize(p, out w, out h) ? p : null;
            if (image == null) (w, h) = (64, 64);
            lib.Add(new MaterialInfo(s.Name, image, w, h));
            _added.Add(s.Name);
        }
        ctx.Session.Touch(); // material sizes may have changed: rebuild the scene's UVs
    }
}
