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
    /// <summary>How this material's alpha draws, when set by hand; null detects it from the image (<see cref="MaterialLibrary.AlphaOf"/>).</summary>
    public AlphaMode? Alpha { get; init; }
    /// <summary>Faces draw from both sides (glTF <c>doubleSided</c>): single-plane fences, foliage cards. Off culls back faces.</summary>
    public bool DoubleSided { get; init; }
    /// <summary>
    /// The image's real size when it differs from <see cref="Width"/> × <see cref="Height"/>, else null. Those two are the
    /// size UVs and tiling use, which stay pinned (the side-car's <c>"size"</c>) when the image is resized.
    /// </summary>
    public (int Width, int Height)? Pixels { get; init; }
    /// <summary>The image file's last write time (UTC ticks) when it was scanned, so a texture on the GPU can tell it is stale; 0 for no file.</summary>
    public long Modified { get; init; }
    /// <summary>Shared with every material of one library, so changing the level's density needs no republish.</summary>
    internal DensityDefault? Base { get; init; }

    public float TexelsPerMeter => TexelDensity is float d && d > 0 ? d : Base?.Value ?? TextureMapping.TexelsPerMeter;
    /// <summary>Texture-mapping scale (metres per texel) for new and realigned faces.</summary>
    public float Scale => 1f / TexelsPerMeter;
    /// <summary>Metres covered by one repeat of the texture.</summary>
    public float TileWidth => Width * Scale;
    public float TileHeight => Height * Scale;
    /// <summary>The surface this material is (a tool material such as clip, or one of the game's), or null.</summary>
    public Surface? Surface => Surfaces.Of(Name);
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
/// code-added materials, then the project's folder (<see cref="ProjectFolder"/>), then folders in the order they were
/// added, then files by relative path.
/// </summary>
public sealed partial class MaterialLibrary
{
    /// <summary>Extra texture folders, ';'-separated.</summary>
    public const string EnvVar = "CHIP_TEXTURE_DIRS";
    /// <summary>
    /// Per-folder side-car holding per-texture overrides:
    /// <c>{ "texelDensity": { "brick": 256 }, "alpha": { "glass": "blend" }, "doubleSided": { "fence": true }, "size": { "brick": [2048, 2048] } }</c>.
    /// </summary>
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
    List<string> _scanOrder = new();
    /// <summary>Each folder's side-car, by library folder (<see cref="MaterialInfo.Source"/>); "" holds code-added materials' overrides.</summary>
    readonly Dictionary<string, Overrides> _overrides = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Alpha read from image pixels, by file path; the write time notices edits.</summary>
    readonly Dictionary<string, (DateTime Stamp, AlphaMode Mode)> _detected = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<MaterialInfo> All { get; private set; } = [];
    /// <summary>Library folders (full paths) in scan order.</summary>
    public IReadOnlyList<string> Folders => _scanOrder;
    /// <summary>The open project's texture folder (full path), scanned first; null without one. It needn't exist yet.</summary>
    public string? ProjectFolder { get; private set; }
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
        OrderFolders();
        return true;
    }

    public bool RemoveFolder(string folder)
    {
        var full = Normalize(folder);
        if (_folders.RemoveAll(f => string.Equals(f, full, StringComparison.OrdinalIgnoreCase)) == 0) return false;
        OrderFolders();
        return true;
    }

    /// <summary>
    /// Makes <paramref name="folder"/> the project's texture folder (null for none), in place of the last one; call
    /// <see cref="Rescan"/> afterwards. A folder also added with <see cref="AddFolder"/> stays in the library when the project
    /// moves on. Returns false if nothing changed.
    /// </summary>
    public bool SetProjectFolder(string? folder)
    {
        var full = folder == null ? null : Normalize(folder);
        if (string.Equals(full, ProjectFolder, StringComparison.OrdinalIgnoreCase)) return false;
        ProjectFolder = full;
        OrderFolders();
        return true;
    }

    void OrderFolders() => _scanOrder = ProjectFolder == null ? [.. _folders]
        : [ProjectFolder, .. _folders.Where(f => !string.Equals(f, ProjectFolder, StringComparison.OrdinalIgnoreCase))];

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

    /// <summary>
    /// Whether a file's path below a library folder passes through a dot folder (<c>.texhist/brick.png</c>). Those belong to
    /// tools, such as an image editor's history, and hold no materials.
    /// </summary>
    public static bool InDotFolder(string relativePath)
    {
        var folders = System.IO.Path.GetDirectoryName(relativePath);
        return !string.IsNullOrEmpty(folders) && folders.Split('/', '\\').Any(s => s.StartsWith('.') && s != "." && s != "..");
    }

    /// <summary>Re-reads every folder (headers and density side-cars). Code-added materials stay.</summary>
    public ScanResult Rescan()
    {
        var found = new List<MaterialInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int duplicates = 0, unreadable = 0, missing = 0;
        foreach (var m in _manual)
            if (seen.Add(m.Name)) found.Add(m);

        foreach (var folder in _scanOrder)
        {
            if (!Directory.Exists(folder))
            {
                // A project's folder only appears once it has textures, which is no one's mistake.
                if (!string.Equals(folder, ProjectFolder, StringComparison.OrdinalIgnoreCase)) missing++;
                continue;
            }
            ReadSidecar(folder);
            var overrides = OverridesOf(folder);
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                    .Where(f => Extensions.Contains(System.IO.Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    .Select(f => System.IO.Path.GetRelativePath(folder, f))
                    .Where(f => !InDotFolder(f))
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
                var size = overrides.Size.TryGetValue(name, out var pinned) ? pinned : (w, h);
                found.Add(new MaterialInfo(name, file, size.Item1, size.Item2)
                {
                    Folder = sub, Source = folder, Modified = Stamp(file).Ticks, Pixels = size == (w, h) ? null : (w, h),
                });
            }
        }
        Publish(found);
        return new ScanResult(found.Count, duplicates, unreadable, missing);
    }

    void Publish(List<MaterialInfo> list)
    {
        for (int i = 0; i < list.Count; i++)
        {
            var overrides = OverridesOf(list[i].Source);
            float? d = overrides.Density.TryGetValue(list[i].Name, out var v) ? v : list[i].TexelDensity;
            AlphaMode? a = overrides.Alpha.TryGetValue(list[i].Name, out var am) ? am : list[i].Alpha;
            bool two = overrides.DoubleSided.Contains(list[i].Name) || list[i].DoubleSided;
            if (d != list[i].TexelDensity || a != list[i].Alpha || two != list[i].DoubleSided || !ReferenceEquals(list[i].Base, _base))
                list[i] = list[i] with { TexelDensity = d, Alpha = a, DoubleSided = two, Base = _base };
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
        var source = Get(name).Source;
        var overrides = OverridesOf(source);
        if (texelsPerMeter is float v) overrides.Density[name] = v; else overrides.Density.Remove(name);
        Publish(All.Select(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase) ? m with { TexelDensity = texelsPerMeter } : m).ToList());
        return Save(source);
    }

    // ---- Pinned size ----

    /// <summary>
    /// Pins each project texture without a pinned size at its current one, and writes the side-car if any were new, so later
    /// resizing the image changes its detail and not how it tiles. The editor calls this; a headless export writes nothing.
    /// Returns false if the side-car could not be written (the pins still apply for this session).
    /// </summary>
    public bool PinProjectSizes()
    {
        if (ProjectFolder is not { } folder) return true;
        var overrides = OverridesOf(folder);
        bool added = false;
        foreach (var m in All)
            if (string.Equals(m.Source, folder, StringComparison.OrdinalIgnoreCase)) added |= overrides.Size.TryAdd(m.Name, (m.Width, m.Height));
        return !added || Save(folder);
    }

    /// <summary>
    /// Gives the project folder this material's settings and tiling size, for a copy of its image about to arrive there, so
    /// faces using its name don't move when the copy takes over. Call it before the copy lands. Returns false if the
    /// side-car could not be written (the settings still apply for this session).
    /// </summary>
    public bool CarryToProject(MaterialInfo material)
    {
        if (ProjectFolder is not { } folder) return true;
        var overrides = OverridesOf(folder);
        if (material.TexelDensity is float density) overrides.Density[material.Name] = density; else overrides.Density.Remove(material.Name);
        if (material.Alpha is AlphaMode alpha) overrides.Alpha[material.Name] = alpha; else overrides.Alpha.Remove(material.Name);
        if (material.DoubleSided) overrides.DoubleSided.Add(material.Name); else overrides.DoubleSided.Remove(material.Name);
        overrides.Size[material.Name] = (material.Width, material.Height);
        try { Directory.CreateDirectory(folder); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            overrides.Unsaved = true;
            return false;
        }
        return Save(folder);
    }

    /// <summary>
    /// Makes a resized material tile by its image's real size again, which changes how it sits on every face using it, and
    /// writes its folder's side-car. Returns false if the side-car could not be written (the change still applies for this session).
    /// </summary>
    public bool ResetSize(string name)
    {
        var material = Get(name);
        if (material.Pixels is not { } pixels) return true;
        OverridesOf(material.Source).Size[material.Name] = pixels;
        Publish(All.Select(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase) ? m with { Width = pixels.Width, Height = pixels.Height, Pixels = null } : m).ToList());
        return Save(material.Source);
    }

    // ---- Alpha ----

    /// <summary>How a material's alpha draws and exports: its override, else what its image's pixels show.</summary>
    public AlphaMode AlphaOf(string name)
    {
        var m = Get(name);
        return m.Alpha ?? DetectAlpha(m.Path);
    }

    /// <summary>The alpha mode an image's pixels show (decoded once per file version). Opaque for no file or an unreadable one.</summary>
    public AlphaMode DetectAlpha(string? path)
    {
        if (path == null || !File.Exists(path)) return AlphaMode.Opaque;
        var stamp = Stamp(path);
        lock (_detected)
            if (_detected.TryGetValue(path, out var hit) && hit.Stamp == stamp) return hit.Mode;
        var mode = AlphaMode.Opaque;
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (!(bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xD8)) // JPEG has no alpha
            {
                var img = StbImageSharp.ImageResult.FromMemory(bytes, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
                mode = MaterialAlpha.Classify(img.Data, img.Width, img.Height);
            }
        }
        catch (Exception) { } // unreadable or unsupported: draws as the missing checker anyway
        lock (_detected) _detected[path] = (stamp, mode);
        return mode;
    }

    /// <summary>Classifies pixels already decoded from <paramref name="path"/> (the viewport's texture upload) and remembers the result.</summary>
    public AlphaMode DetectAlpha(string path, ReadOnlySpan<byte> rgba, int width, int height)
    {
        var mode = MaterialAlpha.Classify(rgba, width, height);
        lock (_detected) _detected[path] = (Stamp(path), mode);
        return mode;
    }

    static DateTime Stamp(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return default; }
    }

    /// <summary>
    /// Overrides (or with null, goes back to detecting) how a material's alpha draws, and writes its folder's side-car.
    /// Returns false if the side-car could not be written (the override still applies for this session).
    /// </summary>
    public bool SetAlphaMode(string name, AlphaMode? mode)
    {
        var source = Get(name).Source;
        var overrides = OverridesOf(source);
        if (mode is AlphaMode m) overrides.Alpha[name] = m; else overrides.Alpha.Remove(name);
        Publish(All.Select(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) ? x with { Alpha = mode } : x).ToList());
        return Save(source);
    }

    // ---- Sides ----

    /// <summary>
    /// Makes a material draw from both sides (or, with false, cull back faces again) and writes its folder's side-car.
    /// Returns false if the side-car could not be written (the change still applies for this session).
    /// </summary>
    public bool SetDoubleSided(string name, bool doubleSided)
    {
        var source = Get(name).Source;
        var overrides = OverridesOf(source);
        if (doubleSided) overrides.DoubleSided.Add(name); else overrides.DoubleSided.Remove(name);
        Publish(All.Select(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) ? x with { DoubleSided = doubleSided } : x).ToList());
        return Save(source);
    }

    // ---- Side-car ----

    /// <summary>What one folder's side-car says, or (under "") what code-added materials were given. Unsaved: its last write failed, so the file is out of date.</summary>
    sealed class Overrides
    {
        public readonly Dictionary<string, float> Density = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, AlphaMode> Alpha = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> DoubleSided = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, (int Width, int Height)> Size = new(StringComparer.OrdinalIgnoreCase);
        public bool Unsaved;
    }

    Overrides OverridesOf(string source)
    {
        if (!_overrides.TryGetValue(source, out var o)) _overrides[source] = o = new Overrides();
        return o;
    }

    /// <summary>
    /// Replaces what the library holds for a folder with its side-car, so an override deleted on disk goes away. An unreadable
    /// file keeps the last good values, and so does a folder whose last write failed (its values only exist here).
    /// </summary>
    void ReadSidecar(string folder)
    {
        if (OverridesOf(folder).Unsaved) return;
        var path = System.IO.Path.Combine(folder, SidecarName);
        var read = new Overrides();
        try
        {
            if (File.Exists(path))
            {
                var root = JsonNode.Parse(File.ReadAllText(path));
                if (root?["texelDensity"] is JsonObject map)
                    foreach (var (name, value) in map)
                        if (value is JsonValue jv && jv.TryGetValue(out float d) && d > 0) read.Density[name] = d;
                if (root?["alpha"] is JsonObject alpha)
                    foreach (var (name, value) in alpha)
                        if (value is JsonValue jv && jv.TryGetValue(out string? s) && Enum.TryParse<AlphaMode>(s, ignoreCase: true, out var mode))
                            read.Alpha[name] = mode;
                if (root?["doubleSided"] is JsonObject sides)
                    foreach (var (name, value) in sides)
                        if (value is JsonValue jv && jv.TryGetValue(out bool on) && on) read.DoubleSided.Add(name);
                if (root?["size"] is JsonObject sizes)
                    foreach (var (name, value) in sizes)
                        if (value is JsonArray { Count: 2 } wh && wh[0] is JsonValue jw && wh[1] is JsonValue jh
                            && jw.TryGetValue(out int w) && jh.TryGetValue(out int h) && w > 0 && h > 0)
                            read.Size[name] = (w, h);
            }
            _overrides[folder] = read;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
    }

    /// <summary>Writes a folder's side-car; "" (code-added materials) has none. False if it could not be written.</summary>
    bool Save(string source)
    {
        if (source.Length == 0) return true;
        bool saved = WriteSidecar(source);
        OverridesOf(source).Unsaved = !saved;
        return saved;
    }

    /// <summary>
    /// Writes everything the folder has, including names with no image right now: an editor can be replacing the file, and a
    /// texture that comes back under its name should keep its settings.
    /// </summary>
    bool WriteSidecar(string folder)
    {
        var path = System.IO.Path.Combine(folder, SidecarName);
        var overrides = OverridesOf(folder);
        var density = new JsonObject();
        foreach (var (name, value) in overrides.Density.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)) density[name] = value;
        var alpha = new JsonObject();
        foreach (var (name, value) in overrides.Alpha.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)) alpha[name] = value.ToString().ToLowerInvariant();
        var sides = new JsonObject();
        foreach (var name in overrides.DoubleSided.Order(StringComparer.OrdinalIgnoreCase)) sides[name] = true;
        var sizes = new JsonObject();
        foreach (var (name, value) in overrides.Size.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)) sizes[name] = new JsonArray(value.Width, value.Height);
        var root = new JsonObject();
        if (density.Count > 0) root["texelDensity"] = density;
        if (alpha.Count > 0) root["alpha"] = alpha;
        if (sides.Count > 0) root["doubleSided"] = sides;
        if (sizes.Count > 0) root["size"] = sizes;
        try
        {
            if (root.Count == 0) { if (File.Exists(path)) File.Delete(path); }
            else AtomicFile.Write(path, System.Text.Encoding.UTF8.GetBytes(root.ToJsonString(new() { WriteIndented = true })));
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
