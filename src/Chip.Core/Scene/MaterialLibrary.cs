using System.Text.Json.Nodes;
using Chip.Core.IO;
using Chip.Core.Texturing;

namespace Chip.Core.Scene;

/// <summary>
/// A material is one image file; its pixel size sets texel density (Scythe reads Base Color size the same way).
/// Density comes from the level (<see cref="MaterialLibrary.DefaultTexelsPerMeter"/>, "1 m is 256 px") unless
/// <see cref="TexelDensity"/> overrides it for this texture.
/// </summary>
public sealed record MaterialInfo(string Name, string? Path, int Width, int Height)
{
    /// <summary>Sub-folder below its library folder, '/'-separated; "" at the root.</summary>
    public string Folder { get; init; } = "";
    /// <summary>The library folder this material was scanned from (full path), or "" for materials added in code.</summary>
    public string Source { get; init; } = "";
    /// <summary>Pixels per metre for this material; null uses its library's default (the level's density).</summary>
    public float? TexelDensity { get; init; }
    /// <summary>Shared with every material of one library, so changing the level's density needs no republish.</summary>
    internal DensityDefault? Base { get; init; }

    public float TexelsPerMeter => TexelDensity is float d && d > 0 ? d : Base?.Value ?? TextureMapping.TexelsPerMeter;
    /// <summary>Texture-mapping scale (metres per texel) for new and realigned faces.</summary>
    public float Scale => 1f / TexelsPerMeter;
    /// <summary>Metres covered by one repeat of the texture.</summary>
    public float TileWidth => Width * Scale;
    public float TileHeight => Height * Scale;
    public ToolMaterial Tool => MaterialLibrary.ToolKind(Name);
}

/// <summary>A library's default texel density (px per metre), shared by reference with its materials.</summary>
internal sealed class DensityDefault
{
    public float Value = TextureMapping.TexelsPerMeter;
}

/// <summary>Result of scanning the library folders.</summary>
public readonly record struct ScanResult(int Materials, int Duplicates, int Unreadable, int MissingFolders);

/// <summary>
/// Materials keyed by name (file stem, see <see cref="MaterialName"/>). Unknown names resolve to a "missing" material so
/// documents always load. Sources are folders scanned recursively for PNG, JPG and TGA (sizes read from file headers,
/// pixels are never decoded here), plus materials added in code. When two files share a name, the first wins:
/// code-added materials, then folders in the order they were added, then files by relative path.
/// </summary>
public sealed partial class MaterialLibrary
{
    /// <summary>Extra texture folders, ';'-separated.</summary>
    public const string EnvVar = "CHIP_TEXTURE_DIRS";
    /// <summary>Per-folder side-car holding texel density overrides: <c>{ "texelDensity": { "brick": 256 } }</c>.</summary>
    public const string SidecarName = "chip-materials.json";
    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".tga"];
    public static readonly MaterialInfo Missing = new("missing", null, 64, 64);

    /// <summary>The placeholder for unknown names (compare with this, not with <see cref="Missing"/>: it carries this library's density).</summary>
    public static bool IsMissing(MaterialInfo m) => m.Path == null && string.Equals(m.Name, Missing.Name, StringComparison.Ordinal);

    readonly DensityDefault _base = new();
    MaterialInfo? _missing;

    /// <summary>
    /// Pixels per metre for every material without its own override: the open level's texel density
    /// (<see cref="Document.TexelDensity"/>). Defaults to <see cref="TextureMapping.TexelsPerMeter"/>.
    /// </summary>
    public float DefaultTexelsPerMeter
    {
        get => _base.Value;
        set
        {
            if (value <= 0 || float.IsNaN(value)) throw new ArgumentOutOfRangeException(nameof(value));
            if (value == _base.Value) return;
            _base.Value = value;
            Version++;
        }
    }

    readonly Dictionary<string, MaterialInfo> _byName = new(StringComparer.OrdinalIgnoreCase);
    readonly List<MaterialInfo> _manual = new();
    readonly List<string> _folders = new();
    readonly Dictionary<string, float> _density = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<MaterialInfo> All { get; private set; } = [];
    /// <summary>Library folders (full paths) in scan order.</summary>
    public IReadOnlyList<string> Folders => _folders;
    /// <summary>Changes whenever the contents change (scan, add, density), so views can refresh caches.</summary>
    public int Version { get; private set; }

    /// <summary>A library over one folder, scanned now. Falls back to a "default" material if it holds no images.</summary>
    public static MaterialLibrary LoadFolder(string folder)
    {
        var lib = new MaterialLibrary();
        lib.AddFolder(folder);
        lib.Rescan();
        if (lib.All.Count == 0) lib.Add(Missing with { Name = "default" });
        return lib;
    }

    /// <summary>Folders listed in <see cref="EnvVar"/> (or the given value): ';'-separated, quotes and blanks ignored.</summary>
    public static IReadOnlyList<string> EnvironmentFolders(string? value = null)
    {
        value ??= Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrWhiteSpace(value)) return [];
        return value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Trim('"').Trim()).Where(p => p.Length > 0).ToList();
    }

    /// <summary>Adds a folder to scan (call <see cref="Rescan"/> afterwards). Returns false if it is already listed.</summary>
    public bool AddFolder(string folder)
    {
        var full = Normalize(folder);
        if (_folders.Contains(full, StringComparer.OrdinalIgnoreCase)) return false;
        _folders.Add(full);
        return true;
    }

    public bool RemoveFolder(string folder)
    {
        var full = Normalize(folder);
        return _folders.RemoveAll(f => string.Equals(f, full, StringComparison.OrdinalIgnoreCase)) > 0;
    }

    static string Normalize(string folder) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(folder));

    /// <summary>Adds (or replaces) a material by name. It survives rescans and wins over scanned files of the same name.</summary>
    public void Add(MaterialInfo info)
    {
        _manual.RemoveAll(m => string.Equals(m.Name, info.Name, StringComparison.OrdinalIgnoreCase));
        _manual.Add(info);
        var list = All.ToList();
        int i = list.FindIndex(m => string.Equals(m.Name, info.Name, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) list[i] = info; else list.Add(info);
        Publish(list);
    }

    public MaterialInfo Get(string name) => _byName.GetValueOrDefault(name) ?? (_missing ??= Missing with { Base = _base });

    public bool Contains(string name) => _byName.ContainsKey(name);

    /// <summary>Re-reads every folder (headers and density side-cars). Code-added materials stay.</summary>
    public ScanResult Rescan()
    {
        var found = new List<MaterialInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int duplicates = 0, unreadable = 0, missing = 0;
        foreach (var m in _manual)
            if (seen.Add(m.Name)) found.Add(m);

        foreach (var folder in _folders)
        {
            if (!Directory.Exists(folder)) { missing++; continue; }
            ReadSidecar(folder);
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                    .Where(f => Extensions.Contains(System.IO.Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    .Select(f => System.IO.Path.GetRelativePath(folder, f))
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { missing++; continue; }

            foreach (var rel in files)
            {
                if (MaterialName(System.IO.Path.GetFileNameWithoutExtension(rel)) is not { } name) continue;
                if (seen.Contains(name)) { duplicates++; continue; }
                var file = System.IO.Path.Combine(folder, rel);
                if (!TryReadImageSize(file, out int w, out int h)) { unreadable++; continue; }
                seen.Add(name);
                var sub = System.IO.Path.GetDirectoryName(rel)?.Replace('\\', '/') ?? "";
                found.Add(new MaterialInfo(name, file, w, h) { Folder = sub, Source = folder });
            }
        }
        Publish(found);
        return new ScanResult(found.Count, duplicates, unreadable, missing);
    }

    void Publish(List<MaterialInfo> list)
    {
        for (int i = 0; i < list.Count; i++)
        {
            float? d = _density.TryGetValue(list[i].Name, out var v) ? v : list[i].TexelDensity;
            if (d != list[i].TexelDensity || !ReferenceEquals(list[i].Base, _base)) list[i] = list[i] with { TexelDensity = d, Base = _base };
        }
        _byName.Clear();
        foreach (var m in list) _byName.TryAdd(m.Name, m);
        All = list;
        Version++;
    }

    // ---- Texel density ----

    /// <summary>Texture-mapping scale (metres per texel) for faces using this material.</summary>
    public float ScaleFor(string name) => Get(name).Scale;

    /// <summary>A world-aligned default mapping at the material's texel density, for new geometry.</summary>
    public TextureMapping MappingFor(string name)
    {
        float s = ScaleFor(name);
        return TextureMapping.Default with { ScaleU = s, ScaleV = s };
    }

    /// <summary>
    /// Overrides (or with null, resets) a material's density in px/m and writes its folder's side-car.
    /// Returns false if the side-car could not be written (the override still applies for this session).
    /// </summary>
    public bool SetTexelDensity(string name, float? texelsPerMeter)
    {
        if (texelsPerMeter is float d && (d <= 0 || float.IsNaN(d))) throw new ArgumentOutOfRangeException(nameof(texelsPerMeter));
        if (texelsPerMeter is float v) _density[name] = v; else _density.Remove(name);
        Publish(All.Select(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase) ? m with { TexelDensity = texelsPerMeter } : m).ToList());
        var source = Get(name).Source;
        return source.Length == 0 || WriteSidecar(source);
    }

    void ReadSidecar(string folder)
    {
        var path = System.IO.Path.Combine(folder, SidecarName);
        if (!File.Exists(path)) return;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path))?["texelDensity"] is JsonObject map)
                foreach (var (name, value) in map)
                    if (value is JsonValue jv && jv.TryGetValue(out float d) && d > 0) _density[name] = d;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
    }

    bool WriteSidecar(string folder)
    {
        var path = System.IO.Path.Combine(folder, SidecarName);
        var map = new JsonObject();
        foreach (var m in All.Where(m => m.Source == folder && m.TexelDensity != null).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
            map[m.Name] = m.TexelDensity!.Value;
        try
        {
            if (map.Count == 0) { if (File.Exists(path)) File.Delete(path); }
            else AtomicFile.Write(path, System.Text.Encoding.UTF8.GetBytes(new JsonObject { ["texelDensity"] = map }.ToJsonString(new() { WriteIndented = true })));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    // ---- Names ----

    static readonly string[] ColorSuffixes = ["_basecolor", "_base_color", "_albedo", "_diffuse", "_diff", "_color"];
    // Only suffixes that never end a plain texture name ("concrete_rough" and "sheet_metal" are real materials).
    static readonly string[] OtherMapSuffixes =
    [
        "_normal", "_normalgl", "_normaldx", "_nrm", "_roughness", "_metallic", "_metalness", "_ao", "_ambientocclusion",
        "_occlusion", "_height", "_displacement", "_disp", "_specular", "_glossiness", "_emissive", "_emission", "_opacity",
        "_orm", "_arm",
    ];

    /// <summary>
    /// Material name for an image's file stem. Base colour aliases are stripped (<c>brick_Albedo</c>, <c>brick_BaseColor</c>
    /// → <c>brick</c>); other PBR maps (<c>brick_Normal</c>, <c>brick_Roughness</c>…) return null so they are not listed.
    /// </summary>
    public static string? MaterialName(string stem)
    {
        foreach (var s in OtherMapSuffixes)
            if (stem.Length > s.Length && stem.EndsWith(s, StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var s in ColorSuffixes)
            if (stem.Length > s.Length && stem.EndsWith(s, StringComparison.OrdinalIgnoreCase)) return stem[..^s.Length];
        return stem;
    }
}
