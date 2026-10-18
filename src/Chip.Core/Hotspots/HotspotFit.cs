using System.Numerics;

namespace Chip.Core.Hotspots;

/// <summary>Whether tiling patches take part: never, as candidates alongside the rest, or exclusively.</summary>
public enum HotspotTiling { No, Allow, Only }

/// <summary>How a region is flattened before it is fitted (Scythe's hotspot mapping modes).</summary>
public enum HotspotMapping { Automatic, Square, Conforming, FollowQuads }

/// <summary>Apply options shared by Shift+H, Shift+F and the editor's apply buttons.</summary>
public sealed class HotspotSettings
{
    public HotspotTiling Tiling { get; set; } = HotspotTiling.Allow;
    /// <summary>Never mirror or turn patches at random, even where their flags allow it.</summary>
    public bool DisallowRandom { get; set; }
    /// <summary>Write UVs only; faces keep their material.</summary>
    public bool DontApplyMaterial { get; set; }
    public HotspotMapping Mapping { get; set; } = HotspotMapping.Automatic;
}

/// <summary>A patch chosen for a region: which one, whether it is turned 90°, and how well it fits (lower is better).</summary>
public readonly record struct PatchFit(int Patch, bool Rotated, float Score);

/// <summary>Random variation picked for a region from its seed, limited by the patch's flags.</summary>
public readonly record struct PatchVariation(bool Rotate180, bool MirrorX, bool MirrorY);

/// <summary>
/// Picks the patch that best fits a region, measured in texels at the material's density.
/// <para>Priority: (1) aspect ratio, weighted <see cref="AspectWeight"/>; (2) size, so a 2 m floor prefers a patch near
/// 1024 px over a 256 px one with the same shape; (3) upright beats turned 90° (<see cref="RotationPenalty"/>), and turning
/// is only considered for patches with Allow Rotation; (4) earlier patches win exact ties.</para>
/// <para>Tiling patches repeat along their long axis, so for them only the cross dimension is matched for size, and a
/// region at least as elongated as the patch fits its shape perfectly. Shorter regions are penalised like any other
/// aspect mismatch, which keeps trim strips for strips.</para>
/// </summary>
public static class HotspotFit
{
    public const float AspectWeight = 2f;
    public const float RotationPenalty = 0.01f;

    /// <summary>
    /// The best patch for a region of <paramref name="regionTexels"/> (width along the region's U, height along V) on a
    /// <paramref name="texWidth"/> × <paramref name="texHeight"/> texture, or null when no patch qualifies.
    /// </summary>
    public static PatchFit? Best(HotspotAsset asset, Vector2 regionTexels, int texWidth, int texHeight, HotspotTiling tiling)
    {
        PatchFit? best = null;
        for (int i = 0; i < asset.Patches.Count; i++)
        {
            var p = asset.Patches[i];
            bool tiles = p.TileAxis != TileAxis.None;
            if (tiling == HotspotTiling.No && tiles) continue;
            if (tiling == HotspotTiling.Only && !tiles) continue;
            if (Orient(asset, i, regionTexels, texWidth, texHeight, tiling) is { } fit && (best == null || fit.Score < best.Value.Score))
                best = fit;
        }
        return best;
    }

    /// <summary>The better orientation of one given patch (upright, or turned when it allows rotation).</summary>
    public static PatchFit? Orient(HotspotAsset asset, int patch, Vector2 regionTexels, int texWidth, int texHeight, HotspotTiling tiling = HotspotTiling.Allow)
    {
        if (patch < 0 || patch >= asset.Patches.Count) return null;
        var p = asset.Patches[patch];
        var axis = tiling == HotspotTiling.No ? TileAxis.None : p.TileAxis;
        var fit = new PatchFit(patch, false, Score(p, axis, regionTexels, texWidth, texHeight, rotated: false));
        if (p.AllowRotation)
        {
            float turned = Score(p, axis, regionTexels, texWidth, texHeight, rotated: true);
            if (turned < fit.Score) fit = new PatchFit(patch, true, turned);
        }
        return fit;
    }

    /// <summary>Fit error of a patch for a region; 0 is a perfect fit.</summary>
    public static float Score(HotspotPatch p, TileAxis axis, Vector2 regionTexels, int texWidth, int texHeight, bool rotated)
    {
        float rw = MathF.Max(rotated ? regionTexels.Y : regionTexels.X, 1e-3f);
        float rh = MathF.Max(rotated ? regionTexels.X : regionTexels.Y, 1e-3f);
        float pw = MathF.Max(p.W * texWidth, 1e-3f), ph = MathF.Max(p.H * texHeight, 1e-3f);
        float aspect, size;
        switch (axis)
        {
            case TileAxis.U:
                aspect = MathF.Max(0, MathF.Log(pw / ph / (rw / rh)));
                size = MathF.Abs(MathF.Log(rh / ph));
                break;
            case TileAxis.V:
                aspect = MathF.Max(0, MathF.Log(ph / pw / (rh / rw)));
                size = MathF.Abs(MathF.Log(rw / pw));
                break;
            default:
                aspect = MathF.Abs(MathF.Log(rw / rh / (pw / ph)));
                size = 0.5f * MathF.Abs(MathF.Log(rw * rh / (pw * ph)));
                break;
        }
        return AspectWeight * aspect + size + (rotated ? RotationPenalty : 0);
    }

    /// <summary>
    /// A stable seed for a region, from its mesh id and lowest face index, so re-applying gives the same result.
    /// (Not <see cref="HashCode"/>, which is randomised per process.)
    /// </summary>
    public static ulong Seed(int meshId, int face) => Mix(((ulong)(uint)meshId << 32) | (uint)face);

    /// <summary>Random 180° turn and mirrors, each only where the patch allows it and random variation is on.</summary>
    public static PatchVariation Variation(HotspotPatch p, ulong seed, bool disallowRandom)
    {
        if (disallowRandom) return default;
        ulong r = Mix(seed ^ 0x9E3779B97F4A7C15UL);
        return new PatchVariation(p.AllowRotation && (r & 1) != 0, p.MirrorX && (r & 2) != 0, p.MirrorY && (r & 4) != 0);
    }

    /// <summary>SplitMix64 finaliser.</summary>
    static ulong Mix(ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
