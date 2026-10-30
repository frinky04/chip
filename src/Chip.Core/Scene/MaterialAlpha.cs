namespace Chip.Core.Scene;

/// <summary>How a material's image alpha draws, named after glTF's <c>alphaMode</c>.</summary>
public enum AlphaMode
{
    /// <summary>Alpha is ignored.</summary>
    Opaque,
    /// <summary>Cut out: texels below <see cref="MaterialAlpha.Cutoff"/> are holes, the rest solid (fences, foliage, grates).</summary>
    Mask,
    /// <summary>See-through by alpha, sorted and drawn after everything solid (glass, water, decals).</summary>
    Blend,
}

/// <summary>Reads an image's alpha into an <see cref="AlphaMode"/>, and prepares cut-out pixels for filtering.</summary>
public static class MaterialAlpha
{
    /// <summary>glTF's default <c>alphaCutoff</c>; the viewport cuts at the same value.</summary>
    public const float Cutoff = 0.5f;

    /// <summary>
    /// Opaque when every texel is fully opaque. Otherwise Mask when partial alpha only lines the edges between solid and
    /// empty (antialiasing, however thin and dense the cut-out), Blend when it fills regions of its own (glass, smoke):
    /// more than 5 % of texels partial with all four neighbours partial too.
    /// </summary>
    public static AlphaMode Classify(ReadOnlySpan<byte> rgba, int width, int height)
    {
        int n = width * height;
        if (n <= 0 || rgba.Length < n * 4) return AlphaMode.Opaque;
        bool any = false;
        for (int i = 3; i < n * 4 && !any; i += 4) any = rgba[i] != 255;
        if (!any) return AlphaMode.Opaque;
        int inside = 0;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (Partial(rgba, width, height, x, y) && Partial(rgba, width, height, x - 1, y) && Partial(rgba, width, height, x + 1, y)
                    && Partial(rgba, width, height, x, y - 1) && Partial(rgba, width, height, x, y + 1)) inside++;
        return inside > n / 20 ? AlphaMode.Blend : AlphaMode.Mask;
    }

    /// <summary>Clearly between empty and solid; wraps at the edges like the GL sampler.</summary>
    static bool Partial(ReadOnlySpan<byte> rgba, int width, int height, int x, int y) =>
        rgba[(((y + height) % height) * width + (x + width) % width) * 4 + 3] is > 16 and < 240;

    /// <summary>
    /// Gives fully transparent texels the colour of their nearest visible neighbours (in place). Image editors leave
    /// white, black or stale colour under alpha 0, which filtering and mipmaps bleed into the edges of a cut-out as a
    /// halo. Grows the visible area a few texels; anything further gets the visible texels' average.
    /// </summary>
    public static void Bleed(Span<byte> rgba, int width, int height, int passes = 8)
    {
        int n = width * height;
        if (rgba.Length < n * 4) return;
        var known = new bool[n];
        long r = 0, g = 0, b = 0;
        int count = 0, unknown = 0;
        for (int i = 0; i < n; i++)
        {
            if (rgba[i * 4 + 3] == 0) { unknown++; continue; }
            known[i] = true;
            r += rgba[i * 4]; g += rgba[i * 4 + 1]; b += rgba[i * 4 + 2];
            count++;
        }
        if (unknown == 0) return;
        if (count == 0) return; // nothing visible: no colour to spread

        var next = new List<int>();
        for (int pass = 0; pass < passes && unknown > 0; pass++)
        {
            next.Clear();
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x;
                    if (known[i]) continue;
                    int sr = 0, sg = 0, sb = 0, k = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            // Wrap like the GL sampler does, so tiling seams get the colour from across the edge.
                            int j = (y + dy + height) % height * width + (x + dx + width) % width;
                            if (!known[j]) continue;
                            sr += rgba[j * 4]; sg += rgba[j * 4 + 1]; sb += rgba[j * 4 + 2];
                            k++;
                        }
                    if (k == 0) continue;
                    rgba[i * 4] = (byte)(sr / k); rgba[i * 4 + 1] = (byte)(sg / k); rgba[i * 4 + 2] = (byte)(sb / k);
                    next.Add(i);
                }
            if (next.Count == 0) break;
            foreach (var i in next) known[i] = true; // after the pass, so each pass grows by exactly one texel
            unknown -= next.Count;
        }
        if (unknown == 0) return;
        byte ar = (byte)(r / count), ag = (byte)(g / count), ab = (byte)(b / count);
        for (int i = 0; i < n; i++)
            if (!known[i]) { rgba[i * 4] = ar; rgba[i * 4 + 1] = ag; rgba[i * 4 + 2] = ab; }
    }
}
