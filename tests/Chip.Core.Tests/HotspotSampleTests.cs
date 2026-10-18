using Chip.Core.Hotspots;
using Chip.Core.Scene;

namespace Chip.Core.Tests;

/// <summary>
/// The bundled sample (<c>assets/hotspots/hotspot_concrete.png</c> + <c>.hotspot.json</c>): a procedural concrete-panel
/// trim sheet, so hotspots work out of the box. The generator lives here, not in the app. Regenerate with
/// <c>$env:CHIP_WRITE_HOTSPOT_SAMPLE=1; dotnet test --filter HotspotSample</c>.
/// </summary>
public class HotspotSampleTests
{
    public const string MaterialName = "hotspot_concrete";
    const int Size = 1024;

    /// <summary>Patch layout in pixels at 1024²: (x, y, w, h, flags "RXYT", inset).</summary>
    static readonly (int X, int Y, int W, int H, string Flags, float Inset)[] Layout =
    [
        (0, 0, 512, 512, "RXY", 4),      // large square panel
        (512, 0, 512, 256, "RXY", 4),    // 2:1 panel
        (512, 256, 256, 256, "RXY", 4),  // small square panel
        (768, 256, 256, 256, "R", 4),    // vent
        (0, 512, 512, 128, "RXY", 3),    // 4:1 panel
        (512, 512, 512, 128, "X", 3),    // 4:1 panel
        (0, 640, 1024, 128, "XT", 3),    // 8:1 tiling band
        (0, 768, 1024, 64, "YT", 2),     // grooved trim
        (0, 832, 1024, 64, "T", 2),      // bolted trim
        (0, 896, 1024, 32, "T", 2),      // thin trim
        (0, 928, 1024, 32, "T", 2),      // thin trim
        (0, 960, 1024, 64, "YT", 2),     // kerb trim
    ];

    public static HotspotAsset Asset() => new()
    {
        Name = "Concrete panels",
        ReferenceWidth = Size,
        ReferenceHeight = Size,
        Materials = [MaterialName],
        Patches = Layout.Select(l => new HotspotPatch
        {
            X = l.X / (float)Size, Y = l.Y / (float)Size, W = l.W / (float)Size, H = l.H / (float)Size,
            AllowRotation = l.Flags.Contains('R'), MirrorX = l.Flags.Contains('X'), MirrorY = l.Flags.Contains('Y'),
            Tiling = l.Flags.Contains('T'), Inset = l.Inset,
        }).ToList(),
    };

    static string? AssetsFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Chip.slnx"))) return Path.Combine(dir.FullName, "assets", "hotspots");
        return null;
    }

    [Fact]
    public void Bundled_sample_matches_the_generator_and_its_texture()
    {
        var folder = AssetsFolder();
        Assert.NotNull(folder);
        if (Environment.GetEnvironmentVariable("CHIP_WRITE_HOTSPOT_SAMPLE") == "1")
        {
            Directory.CreateDirectory(folder!);
            File.WriteAllBytes(Path.Combine(folder!, MaterialName + ".png"), MaterialLibrary.EncodePng(Size, Size, Pixels()));
            Asset().Save(Path.Combine(folder!, MaterialName + HotspotAsset.Extension));
        }
        var loaded = HotspotAsset.Load(Path.Combine(folder!, MaterialName + HotspotAsset.Extension));
        Assert.Equal(Asset().Serialize(), loaded.Serialize());
        Assert.True(MaterialLibrary.TryReadImageSize(Path.Combine(folder!, MaterialName + ".png"), out int w, out int h));
        Assert.Equal((Size, Size), (w, h));
        Assert.All(loaded.Patches.Where(p => p.Tiling), p => Assert.Equal(TileAxis.U, p.TileAxis));
    }

    // ---------------------------------------------------------------- texture

    /// <summary>Top-down RGBA8. Noise tiles with the texture, so the full-width trims repeat seamlessly along U.</summary>
    public static byte[] Pixels()
    {
        var rgba = new byte[Size * Size * 4];
        var owner = new int[Size * Size];
        Array.Fill(owner, -1);
        for (int i = 0; i < Layout.Length; i++)
        {
            var l = Layout[i];
            for (int y = l.Y; y < l.Y + l.H; y++)
                for (int x = l.X; x < l.X + l.W; x++) owner[y * Size + x] = i;
        }
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float v = Concrete(x, y);
                int i = owner[y * Size + x];
                if (i >= 0) v = Patch(i, x, y, v);
                // Warm-grey concrete tint.
                int o = (y * Size + x) * 4;
                rgba[o] = Byte(v * 1.02f);
                rgba[o + 1] = Byte(v);
                rgba[o + 2] = Byte(v * 0.95f);
                rgba[o + 3] = 255;
            }
        return rgba;
    }

    static byte Byte(float v) => (byte)Math.Clamp((int)MathF.Round(v), 0, 255);

    static float Concrete(int x, int y)
    {
        float n = Fbm(x / 64f, y / 64f, 16, 5);
        float pores = Hash(x, y, 7) > 0.985f ? -38 * Hash(x, y, 8) : 0;
        return 150 + (n - 0.5f) * 46 + pores;
    }

    static float Patch(int index, int x, int y, float v)
    {
        var l = Layout[index];
        float lx = x - l.X, ly = y - l.Y;
        bool tiles = l.Flags.Contains('T');
        float t = ly / l.H;
        // Panel tone varies per patch so neighbouring panels read apart.
        v += (Hash(index, 3, 11) - 0.5f) * 16;

        if (index == 3) // vent: horizontal louvres
        {
            float m = 28;
            if (lx > m && lx < l.W - m && ly > m && ly < l.H - m)
            {
                float k = (ly - m) % 24;
                v = k < 12 ? 52 + k * 2 : v - 18 + (k - 12) * 1.5f;
            }
        }
        else if (!tiles)
        {
            // Rain streaks from the top edge (gives the panels a visible "up").
            float streak = Fbm(x / 6f, 0.5f, 1024 / 6, 2);
            v -= MathF.Max(0, streak - 0.45f) * 70 * MathF.Pow(1 - t, 2.2f);
            // Form-tie holes near the corners.
            float r = MathF.Max(4, MathF.Min(l.W, l.H) * 0.035f);
            float ix = MathF.Min(l.W * 0.12f, 60), iy = MathF.Min(l.H * 0.18f, 60);
            foreach (var (cx, cy) in new[] { (ix, iy), (l.W - ix, iy), (ix, l.H - iy), (l.W - ix, l.H - iy) })
            {
                float d = MathF.Sqrt((lx - cx) * (lx - cx) + (ly - cy) * (ly - cy));
                if (d < r) v = 48 + d / r * 30;
                else if (d < r + 2.5f) v += 22;
            }
        }
        else
        {
            // Trims: profiled bands along U.
            switch (index)
            {
                case 6: // band with a recessed centre and a groove
                    if (t > 0.3f && t < 0.7f) v -= 14;
                    if (MathF.Abs(t - 0.3f) < 0.02f) v += 26;
                    if (MathF.Abs(t - 0.7f) < 0.02f) v -= 34;
                    break;
                case 7: // two grooves
                    if (MathF.Abs(t - 0.35f) < 0.05f || MathF.Abs(t - 0.65f) < 0.05f) v = 70 + 40 * MathF.Abs(t - 0.5f);
                    break;
                case 8: // bolts every 128 px
                {
                    float bx = lx % 128 - 64, by = ly - l.H / 2f;
                    float d = MathF.Sqrt(bx * bx + by * by);
                    if (d < 9) v = 120 + (by < 0 ? 40 : -20) * (1 - d / 9);
                    else if (d < 11) v = 60;
                    break;
                }
                case 9: v -= 10; break;
                case 10: if (t > 0.45f && t < 0.55f) v -= 40; break;
                case 11: // kerb: bright top chamfer, dark base
                    if (t < 0.2f) v += 30 * (1 - t / 0.2f);
                    if (t > 0.8f) v -= 40 * ((t - 0.8f) / 0.2f);
                    break;
            }
        }

        // Bevel and seam at the rect edges (only across a tiling strip, never along it).
        float left = tiles ? float.MaxValue : lx, right = tiles ? float.MaxValue : l.W - 1 - lx;
        float top = ly, bottom = l.H - 1 - ly;
        float edge = MathF.Min(MathF.Min(left, right), MathF.Min(top, bottom));
        if (edge < 2) return v * 0.35f;
        if (edge < 7)
        {
            if (top == edge || left == edge) v += 24;
            else v -= 26;
        }
        return v;
    }

    static float Hash(int x, int y, int seed)
    {
        uint h = (uint)(x * 374761393 + y * 668265263 + seed * 2147483647);
        h = (h ^ (h >> 13)) * 1274126177;
        return ((h ^ (h >> 16)) & 0xFFFFFF) / (float)0xFFFFFF;
    }

    /// <summary>Value noise on a lattice that wraps every <paramref name="period"/> cells, so it tiles.</summary>
    static float Noise(float x, float y, int period)
    {
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        fx = fx * fx * (3 - 2 * fx);
        fy = fy * fy * (3 - 2 * fy);
        int Wrap(int a) => ((a % period) + period) % period;
        float H(int a, int b) => Hash(Wrap(a), Wrap(b), 1);
        float a0 = H(x0, y0) + (H(x0 + 1, y0) - H(x0, y0)) * fx;
        float a1 = H(x0, y0 + 1) + (H(x0 + 1, y0 + 1) - H(x0, y0 + 1)) * fx;
        return a0 + (a1 - a0) * fy;
    }

    static float Fbm(float x, float y, int period, int octaves)
    {
        float sum = 0, amp = 0.5f, norm = 0;
        for (int o = 0; o < octaves; o++)
        {
            sum += Noise(x, y, period) * amp;
            norm += amp;
            x *= 2; y *= 2; period *= 2; amp *= 0.5f;
        }
        return sum / norm;
    }
}
