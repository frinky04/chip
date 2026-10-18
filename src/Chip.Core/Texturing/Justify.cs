using System.Numerics;

namespace Chip.Core.Texturing;

public enum JustifyMode { Left, Right, Top, Bottom, Center }

/// <summary>Which extent a fit makes span exactly one tile.</summary>
public enum FitMode { Both, Width, Height }

/// <summary>
/// Fit options from Scythe's Modify Texture panel. <see cref="KeepRatio"/> scales both axes by the same factor (so the
/// texture keeps its current aspect); <see cref="TreatAsOne"/> fits all faces as one region instead of each on its own.
/// </summary>
public readonly record struct FitOptions(FitMode Mode = FitMode.Both, bool KeepRatio = false, bool TreatAsOne = false);

/// <summary>
/// UV-space adjustments of a face set: justify, fit, shift, scale, rotate and flip. Each works on projected and explicit
/// faces alike (see <see cref="UvSpace"/>), per face or across all faces as one region. V points down the image, so
/// "top" is the smallest V.
/// </summary>
public static class Justify
{
    /// <summary>Moves the texture so its edge (or centre) lines up with the faces' UV bounds.</summary>
    public static void Align(IReadOnlyList<FaceRef> faces, JustifyMode mode, TextureSize size, bool treatAsOne = false)
    {
        foreach (var group in Groups(faces, treatAsOne))
        {
            var (min, max) = UvSpace.Bounds(group, size);
            var b = mode switch
            {
                JustifyMode.Left => new Vector2(-min.X, 0),
                JustifyMode.Right => new Vector2(1 - max.X, 0),
                JustifyMode.Top => new Vector2(0, -min.Y),
                JustifyMode.Bottom => new Vector2(0, 1 - max.Y),
                _ => new Vector2(0.5f) - (min + max) * 0.5f,
            };
            foreach (var f in group) UvSpace.ApplyAffine(f, Vector2.One, b, size);
        }
    }

    /// <summary>
    /// Scale and shift so the faces' UV bounds become one tile, starting at (0, 0). Fit Width/Height touch only that
    /// axis unless Keep Ratio is on, which scales the other axis by the same factor (aligned to the top/left).
    /// Keep Ratio on a Both fit uses the smaller factor, so the whole region fits in one tile without stretching.
    /// </summary>
    public static void Fit(IReadOnlyList<FaceRef> faces, FitOptions options, TextureSize size)
    {
        foreach (var group in Groups(faces, options.TreatAsOne))
        {
            var (min, max) = UvSpace.Bounds(group, size);
            var (a, b) = FitTransform(min, max, options);
            foreach (var f in group) UvSpace.ApplyAffine(f, a, b, size);
        }
    }

    /// <summary>The per-axis <c>uv' = a uv + b</c> a fit applies to a region with the given UV bounds.</summary>
    public static (Vector2 A, Vector2 B) FitTransform(Vector2 min, Vector2 max, FitOptions options)
    {
        var ext = max - min;
        float ku = ext.X > 1e-6f ? 1 / ext.X : 1, kv = ext.Y > 1e-6f ? 1 / ext.Y : 1;
        var a = options.Mode switch
        {
            FitMode.Width => new Vector2(ku, options.KeepRatio ? ku : 1),
            FitMode.Height => new Vector2(options.KeepRatio ? kv : 1, kv),
            _ => options.KeepRatio ? new Vector2(MathF.Min(ku, kv)) : new Vector2(ku, kv),
        };
        // Axes left alone (a = 1 without Keep Ratio) keep their offset too.
        bool moveU = options.Mode != FitMode.Height || options.KeepRatio;
        bool moveV = options.Mode != FitMode.Width || options.KeepRatio;
        var b = new Vector2(moveU ? -min.X * a.X : 0, moveV ? -min.Y * a.Y : 0);
        return (a, b);
    }

    /// <summary>Shifts by a number of texels (positive U moves the image left on the face, positive V moves it up).</summary>
    public static void Shift(IReadOnlyList<FaceRef> faces, Vector2 texels, TextureSize size)
    {
        foreach (var f in faces)
        {
            var (w, h) = size(f.Face.Material);
            UvSpace.ApplyAffine(f, Vector2.One, texels / new Vector2(w, h), size);
        }
    }

    /// <summary>
    /// Makes the texture <paramref name="factor"/> times bigger on the faces (2 = each texel covers twice the area),
    /// about each face's UV centre, or the region's centre when treated as one.
    /// </summary>
    public static void Scale(IReadOnlyList<FaceRef> faces, Vector2 factor, TextureSize size, bool treatAsOne = false)
    {
        var a = Vector2.One / factor;
        foreach (var group in Groups(faces, treatAsOne))
        {
            var (min, max) = UvSpace.Bounds(group, size);
            var c = (min + max) * 0.5f;
            foreach (var f in group) UvSpace.ApplyAffine(f, a, c * (Vector2.One - a), size);
        }
    }

    /// <summary>Rotates the texture clockwise by <paramref name="degrees"/> about each face's UV centre (or the region's).</summary>
    public static void Rotate(IReadOnlyList<FaceRef> faces, float degrees, TextureSize size, bool treatAsOne = false)
    {
        foreach (var group in Groups(faces, treatAsOne))
        {
            var (min, max) = UvSpace.Bounds(group, size);
            foreach (var f in group) UvSpace.Rotate(f, degrees, (min + max) * 0.5f, size);
        }
    }

    /// <summary>Mirrors the UVs within their own bounds, so the face keeps covering the same part of the texture.</summary>
    public static void Flip(IReadOnlyList<FaceRef> faces, bool horizontal, TextureSize size, bool treatAsOne = false)
    {
        foreach (var group in Groups(faces, treatAsOne))
        {
            var (min, max) = UvSpace.Bounds(group, size);
            var a = horizontal ? new Vector2(-1, 1) : new Vector2(1, -1);
            var b = horizontal ? new Vector2(min.X + max.X, 0) : new Vector2(0, min.Y + max.Y);
            foreach (var f in group) UvSpace.ApplyAffine(f, a, b, size);
        }
    }

    static IEnumerable<IReadOnlyList<FaceRef>> Groups(IReadOnlyList<FaceRef> faces, bool asOne) =>
        asOne ? [faces] : faces.Select(f => (IReadOnlyList<FaceRef>)[f]);
}
