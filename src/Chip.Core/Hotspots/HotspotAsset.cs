using System.Text.Json;
using System.Text.Json.Serialization;
using Chip.Core.IO;

namespace Chip.Core.Hotspots;

/// <summary>Which texture axis a tiling patch repeats along.</summary>
public enum TileAxis { None, U, V }

/// <summary>
/// One rectangle of a hotspot texture (Scythe: a patch). The rect is normalised to 0..1 of the texture (x right, y down,
/// the same space as face UVs), so one asset works for every resolution of the texture.
/// </summary>
public sealed class HotspotPatch
{
    public float X { get; set; }
    public float Y { get; set; }
    public float W { get; set; }
    public float H { get; set; }
    /// <summary>R: the patch may be turned 90° to fit a region (and 180° at random).</summary>
    public bool AllowRotation { get; set; }
    /// <summary>X: the patch may be mirrored horizontally at random.</summary>
    public bool MirrorX { get; set; }
    /// <summary>Y: the patch may be mirrored vertically at random.</summary>
    public bool MirrorY { get; set; }
    /// <summary>T: the patch repeats along its long axis. Only valid when it spans the full width or height (<see cref="CanTile"/>).</summary>
    public bool Tiling { get; set; }
    /// <summary>Pixels (at the asset's reference resolution) kept clear inside the rect, so mip bleeding stays hidden.</summary>
    public float Inset { get; set; }

    const float Span = 1e-3f;

    [JsonIgnore] public bool SpansWidth => X <= Span && X + W >= 1 - Span;
    [JsonIgnore] public bool SpansHeight => Y <= Span && Y + H >= 1 - Span;
    [JsonIgnore] public bool CanTile => SpansWidth || SpansHeight;

    /// <summary>
    /// The axis this patch repeats along: U for full-width strips, V for full-height ones. A patch covering the whole
    /// texture tiles along its longer side. None unless <see cref="Tiling"/> is set and the rect spans the texture.
    /// </summary>
    [JsonIgnore]
    public TileAxis TileAxis => !Tiling ? TileAxis.None
        : SpansWidth && SpansHeight ? (W >= H ? TileAxis.U : TileAxis.V)
        : SpansWidth ? TileAxis.U : SpansHeight ? TileAxis.V : TileAxis.None;

    public HotspotPatch Clone() => (HotspotPatch)MemberwiseClone();

    /// <summary>Clamps the rect into the texture, keeps it at least <paramref name="minSize"/>, and clears Tiling when it can't tile.</summary>
    public void Normalize(float minSize = 1e-4f)
    {
        if (W < 0) { X += W; W = -W; }
        if (H < 0) { Y += H; H = -H; }
        X = Math.Clamp(X, 0, 1 - minSize);
        Y = Math.Clamp(Y, 0, 1 - minSize);
        W = Math.Clamp(W, minSize, 1 - X);
        H = Math.Clamp(H, minSize, 1 - Y);
        if (!float.IsFinite(Inset) || Inset < 0) Inset = 0;
        if (!CanTile) Tiling = false;
    }

    /// <summary>The flags as Scythe's patch list shows them: <c>[R][X][Y][T][4]</c>.</summary>
    public string FlagText() =>
        (AllowRotation ? "[R]" : "") + (MirrorX ? "[X]" : "") + (MirrorY ? "[Y]" : "") + (Tiling ? "[T]" : "") + (Inset > 0 ? $"[{Inset:0.#}]" : "");
}

/// <summary>
/// A hotspot asset (<c>*.hotspot.json</c>): one per texture pattern, shared by every material painted with that pattern.
/// It lives beside the textures in a library folder and lists its <see cref="Materials"/>; applying a hotspot with one of
/// those materials fits faces into its <see cref="Patches"/>.
/// </summary>
public sealed class HotspotAsset
{
    public const string Extension = ".hotspot.json";

    /// <summary>Format version of the file (the asset's own, unrelated to the .chip schema).</summary>
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "hotspot";
    /// <summary>Resolution the insets are measured at. Rects are normalised, so they need no reference.</summary>
    public int ReferenceWidth { get; set; } = 1024;
    public int ReferenceHeight { get; set; } = 1024;
    /// <summary>Material names (file stems) painted with this pattern.</summary>
    public List<string> Materials { get; set; } = new();
    /// <summary>Material shown in the hotspot editor; null uses the first of <see cref="Materials"/>.</summary>
    public string? PreviewMaterial { get; set; }
    public List<HotspotPatch> Patches { get; set; } = new();

    /// <summary>File the asset was loaded from or last saved to.</summary>
    [JsonIgnore] public string? Path { get; set; }

    [JsonIgnore] public string? DisplayMaterial => PreviewMaterial ?? Materials.FirstOrDefault();

    public bool Targets(string material) => Materials.Contains(material, StringComparer.OrdinalIgnoreCase);

    /// <summary>A patch's inset as a fraction of the texture, per axis.</summary>
    public (float X, float Y) InsetFraction(HotspotPatch p) =>
        (p.Inset / Math.Max(ReferenceWidth, 1), p.Inset / Math.Max(ReferenceHeight, 1));

    public HotspotAsset Clone()
    {
        var c = (HotspotAsset)MemberwiseClone();
        c.Materials = new List<string>(Materials);
        c.Patches = Patches.Select(p => p.Clone()).ToList();
        return c;
    }

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    /// <summary>Parses an asset; bad rects are clamped and invalid Tiling flags cleared. Throws <see cref="JsonException"/> on malformed JSON.</summary>
    public static HotspotAsset Parse(string json)
    {
        var a = JsonSerializer.Deserialize<HotspotAsset>(json, Json) ?? throw new JsonException("Empty hotspot asset");
        a.Materials = (a.Materials ?? new()).Where(m => !string.IsNullOrWhiteSpace(m)).ToList();
        a.Patches = (a.Patches ?? new()).Where(p => p != null && float.IsFinite(p.X + p.Y + p.W + p.H) && p.W != 0 && p.H != 0).ToList();
        foreach (var p in a.Patches) p.Normalize();
        if (a.ReferenceWidth <= 0) a.ReferenceWidth = 1024;
        if (a.ReferenceHeight <= 0) a.ReferenceHeight = 1024;
        return a;
    }

    public static HotspotAsset Load(string path)
    {
        var a = Parse(File.ReadAllText(path));
        a.Path = path;
        if (string.IsNullOrWhiteSpace(a.Name)) a.Name = StemOf(path);
        return a;
    }

    public void Save(string path)
    {
        AtomicFile.Write(path, System.Text.Encoding.UTF8.GetBytes(Serialize()));
        Path = path;
    }

    /// <summary>"concrete.hotspot.json" → "concrete".</summary>
    public static string StemOf(string path)
    {
        var file = System.IO.Path.GetFileName(path);
        return file.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? file[..^Extension.Length] : System.IO.Path.GetFileNameWithoutExtension(file);
    }
}
