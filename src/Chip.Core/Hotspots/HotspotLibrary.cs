namespace Chip.Core.Hotspots;

/// <summary>
/// The hotspot assets found in the material library's folders (<c>*.hotspot.json</c> beside the textures, recursively).
/// A material belongs to the first asset that lists it, in folder then path order, matching how the material library
/// resolves duplicate names.
/// </summary>
public sealed class HotspotLibrary
{
    readonly List<HotspotAsset> _assets = new();

    public IReadOnlyList<HotspotAsset> Assets => _assets;
    /// <summary>Files that exist but could not be read or parsed in the last scan.</summary>
    public IReadOnlyList<string> Errors { get; private set; } = [];

    /// <summary>Re-reads every <c>*.hotspot.json</c> under the folders. Missing folders are skipped.</summary>
    public void Scan(IEnumerable<string> folders)
    {
        _assets.Clear();
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder)) continue;
            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*" + HotspotAsset.Extension, SearchOption.AllDirectories)
                    .Order(StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files)
            {
                if (!seen.Add(System.IO.Path.GetFullPath(file))) continue;
                try { _assets.Add(HotspotAsset.Load(file)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException)
                {
                    errors.Add(file);
                }
            }
        }
        Errors = errors;
    }

    /// <summary>Adds an asset created in the editor (not yet scanned from disk).</summary>
    public void Add(HotspotAsset asset)
    {
        if (!_assets.Contains(asset)) _assets.Add(asset);
    }

    /// <summary>The asset painted with <paramref name="material"/>, or null.</summary>
    public HotspotAsset? ForMaterial(string? material) =>
        material == null ? null : _assets.FirstOrDefault(a => a.Targets(material));
}
