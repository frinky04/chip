using System.Runtime.InteropServices;
using ImGuiNET;

namespace Chip.Editor.UI;

/// <summary>
/// IBM Plex Mono at two weights and two sizes. Hierarchy comes from weight, not caps.
/// Glyph ranges cover Latin, punctuation (dashes, ellipsis), arrows, math (×, ±) and geometric shapes.
/// </summary>
public static class Fonts
{
    /// <summary>Pixel sizes after display scaling: 16 logical px body, 13 logical px small.</summary>
    public static float BodySize { get; private set; } = 14;
    public static float SmallSize { get; private set; } = 12;

    public static ImFontPtr Body, Strong, Small, Icons;
    public static float IconSize { get; private set; } = 18;

    static readonly ushort[] Ranges =
    [
        0x0020, 0x00FF, // Basic Latin + Latin-1 (× · °)
        0x2010, 0x2027, // dashes, quotes, bullet, ellipsis
        0x2190, 0x21FF, // arrows
        0x2200, 0x22FF, // math operators
        0x25A0, 0x25FF, // geometric shapes
        0,
    ];
    static GCHandle _rangesHandle, _iconRangesHandle;

    /// <summary>Adds the fonts to the atlas. Call from ImGuiController's configure callback, before the atlas builds.</summary>
    public static void Load(string fontDir, float scale)
    {
        BodySize = MathF.Round(16 * scale);
        SmallSize = MathF.Round(13 * scale);
        IconSize = MathF.Round(18 * scale);
        var io = ImGui.GetIO();
        var regular = Path.Combine(fontDir, "IBMPlexMono-Regular.ttf");
        var semibold = Path.Combine(fontDir, "IBMPlexMono-SemiBold.ttf");
        if (!File.Exists(regular) || !File.Exists(semibold))
        {
            Body = Strong = Small = Icons = io.Fonts.AddFontDefault();
            return;
        }
        if (!_rangesHandle.IsAllocated) _rangesHandle = GCHandle.Alloc(Ranges, GCHandleType.Pinned); // atlas keeps the pointer
        var ranges = _rangesHandle.AddrOfPinnedObject();
        // stb_truetype ignores hinting, so give it room: 3x horizontal oversampling for even sub-pixel placement,
        // whole-pixel advances (monospace columns stay sharp), and a slight coverage boost so light-on-dark
        // strokes don't render thin and broken.
        unsafe
        {
            var cfg = new ImFontConfigPtr(ImGuiNative.ImFontConfig_ImFontConfig());
            cfg.OversampleH = 3;
            cfg.OversampleV = 2;
            cfg.PixelSnapH = true;
            cfg.RasterizerMultiply = 1.12f;
            Body = io.Fonts.AddFontFromFileTTF(regular, BodySize, cfg, ranges); // first font = ImGui default
            Strong = io.Fonts.AddFontFromFileTTF(semibold, BodySize, cfg, ranges);
            Small = io.Fonts.AddFontFromFileTTF(regular, SmallSize, cfg, ranges);

            // Icon font: only the glyphs the UI uses, so the atlas stays small.
            var iconFile = Path.Combine(fontDir, "tabler-icons.ttf");
            if (File.Exists(iconFile))
            {
                var iconRanges = UI.Icons.Codepoints.Distinct().Order().SelectMany(c => new[] { c, c }).Append((ushort)0).ToArray();
                if (!_iconRangesHandle.IsAllocated) _iconRangesHandle = GCHandle.Alloc(iconRanges, GCHandleType.Pinned);
                Icons = io.Fonts.AddFontFromFileTTF(iconFile, IconSize, cfg, _iconRangesHandle.AddrOfPinnedObject());
            }
            else Icons = Body;
            cfg.Destroy(); // the atlas keeps its own copy
        }
    }
}
