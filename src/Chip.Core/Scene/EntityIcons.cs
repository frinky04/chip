using System.Buffers.Binary;
using System.Text;

namespace Chip.Core.Scene;

/// <summary>
/// The icon names a manifest class may give (<c>"icon": "door"</c>): those of the Tabler icon font the editor draws
/// with (tabler.io/icons), read from the font's own glyph names so every icon in it can be used. Empty when the font
/// isn't beside the executable, and then names go unchecked.
/// </summary>
public static class EntityIcons
{
    /// <summary>The editor's glyph ranges are 16-bit: icons past this codepoint can't be drawn.</summary>
    public const int MaxDrawable = 0xFFFF;

    static string _fontPath = Path.Combine(AppContext.BaseDirectory, "assets", "fonts", "tabler-icons.ttf");
    static IReadOnlyDictionary<string, int>? _names;

    public static string FontPath
    {
        get => _fontPath;
        set { _fontPath = value; _names = null; }
    }

    /// <summary>Icon name → codepoint.</summary>
    public static IReadOnlyDictionary<string, int> Names => _names ??= Read(_fontPath);

    public static int? Codepoint(string name) => Names.TryGetValue(name, out var c) ? c : null;

    /// <summary>Why a manifest's icon can't be drawn, or null when it can (or when there's no font to say).</summary>
    public static string? Problem(string name) =>
        Names.Count == 0 ? null
        : Codepoint(name) is not { } c ? $"icon \"{name}\" isn't a Tabler icon (names as on tabler.io/icons: \"door\", \"bulb\", \"device-desktop\")"
        : c > MaxDrawable ? $"icon \"{name}\" is one of the newest Tabler icons, which Chip can't draw yet; pick another"
        : null;

    /// <summary>Glyph names from the <c>post</c> table (format 2), joined to codepoints through the <c>cmap</c> (format 12).</summary>
    static Dictionary<string, int> Read(string path)
    {
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        byte[] f;
        try { f = File.ReadAllBytes(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return names; }
        try
        {
            var (cmap, _) = Table(f, "cmap");
            var (post, postLength) = Table(f, "post");
            if (cmap < 0 || post < 0 || U32(f, post) != 0x00020000) return names;

            int glyphs = U16(f, post + 32);
            var index = new int[glyphs];
            for (int g = 0; g < glyphs; g++) index[g] = U16(f, post + 34 + g * 2);
            // Pascal strings to the table's end; empty ones count (Tabler's first is).
            var custom = new List<string>();
            for (int p = post + 34 + glyphs * 2, end = post + postLength; p < end && p + 1 + f[p] <= end; p += 1 + f[p])
                custom.Add(Encoding.ASCII.GetString(f, p + 1, f[p]));

            for (int t = 0, count = U16(f, cmap + 2); t < count; t++)
            {
                int sub = cmap + (int)U32(f, cmap + 8 + t * 8);
                if (U16(f, sub) != 12) continue;
                for (int g = 0, groups = (int)U32(f, sub + 12); g < groups; g++)
                {
                    int at = sub + 16 + g * 12;
                    uint start = U32(f, at), end = U32(f, at + 4), glyph = U32(f, at + 8);
                    for (uint c = start; c <= end; c++, glyph++)
                        // Indices below 258 are the standard Macintosh names (.notdef, space…), never an icon.
                        if (glyph < glyphs && index[glyph] >= 258 && index[glyph] - 258 < custom.Count)
                            names.TryAdd(custom[index[glyph] - 258], (int)c);
                }
                break;
            }
        }
        catch (ArgumentOutOfRangeException) { names.Clear(); } // a truncated font: no names rather than some
        return names;
    }

    /// <summary>The table's offset and length, or -1.</summary>
    static (int Offset, int Length) Table(byte[] f, string tag)
    {
        for (int i = 0, count = U16(f, 4); i < count; i++)
            if (Encoding.ASCII.GetString(f, 12 + i * 16, 4) == tag) return ((int)U32(f, 12 + i * 16 + 8), (int)U32(f, 12 + i * 16 + 12));
        return (-1, 0);
    }

    static int U16(byte[] f, int at) => BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(at, 2));
    static uint U32(byte[] f, int at) => BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(at, 4));
}
