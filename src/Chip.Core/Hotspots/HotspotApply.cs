using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Core.Hotspots;

/// <summary>
/// An oriented rectangle around a region's 2D layout (metres). <see cref="Local"/> maps a layout point to 0..1 across it.
/// </summary>
public readonly record struct RegionRect(Vector2 Origin, Vector2 AxisU, Vector2 AxisV, float Width, float Height)
{
    public Vector2 Size => new(Width, Height);

    public Vector2 Local(Vector2 p)
    {
        var d = p - Origin;
        return new Vector2(Vector2.Dot(d, AxisU) / MathF.Max(Width, 1e-9f), Vector2.Dot(d, AxisV) / MathF.Max(Height, 1e-9f));
    }
}

/// <summary>What one region received.</summary>
public sealed record HotspotRegionResult(IReadOnlyList<int> Faces, int Patch, bool Rotated, PatchVariation Variation)
{
    public bool Applied => Patch >= 0;
}

/// <summary>
/// Hotspot texturing (Scythe: Apply Hotspot). Each region is flattened by the mapping mode, measured in texels at the
/// material's density, matched to a patch (<see cref="HotspotFit"/>), varied by a seed derived from its faces, and its
/// corners get explicit UVs inside the patch rect, minus the inset. A region is either one face (Shift+F) or a
/// connected set of coplanar faces (Shift+H), treated as one surface.
/// </summary>
public static class HotspotApply
{
    /// <summary>Faces joined across an edge count as coplanar within this angle.</summary>
    public const float CoplanarDegrees = 1f;
    /// <summary>Automatic mapping keeps the square unwrap while no edge is stretched by more than this (log ratio, ~10%).</summary>
    public const float MaxSquareDistortion = 0.0953f;

    /// <summary>
    /// Applies the hotspot of <paramref name="asset"/> with <paramref name="material"/> to faces of one mesh. Sets the
    /// material unless <see cref="HotspotSettings.DontApplyMaterial"/>. <paramref name="patch"/> forces one patch (the
    /// editor's "apply selected patch"); otherwise each region gets its best fit. Regions no patch fits are left alone
    /// and reported with <see cref="HotspotRegionResult.Patch"/> -1.
    /// </summary>
    public static List<HotspotRegionResult> Apply(MaterialLibrary lib, PolyMesh mesh, IReadOnlyList<int> faces, HotspotAsset asset,
        string material, HotspotSettings settings, bool individual, int reference = -1, int? patch = null)
    {
        var info = lib.Get(material);
        var results = new List<HotspotRegionResult>();
        foreach (var region in Regions(mesh, faces, individual))
        {
            var r = ApplyRegion(mesh, region, asset, info.Width, info.Height, info.TexelsPerMeter, settings, reference, patch);
            results.Add(r);
            if (!r.Applied || settings.DontApplyMaterial) continue;
            foreach (var f in region) lib.Assign(mesh.Faces[f], material);
        }
        return results;
    }

    /// <summary>Fits and writes UVs for one region on a texture of the given size and density (no material change).</summary>
    public static HotspotRegionResult ApplyRegion(PolyMesh mesh, IReadOnlyList<int> region, HotspotAsset asset, int texWidth, int texHeight,
        float texelsPerMeter, HotspotSettings settings, int reference = -1, int? patch = null)
    {
        var layout = Layout(mesh, region, settings.Mapping, reference);
        var rect = Bound(layout.Values.SelectMany(v => v));
        var texels = rect.Size * texelsPerMeter;
        var fit = patch is int forced
            ? HotspotFit.Orient(asset, forced, texels, texWidth, texHeight, settings.Tiling)
            : HotspotFit.Best(asset, texels, texWidth, texHeight, settings.Tiling);
        if (fit is not { } chosen) return new HotspotRegionResult(region, -1, false, default);

        var p = asset.Patches[chosen.Patch];
        var variation = HotspotFit.Variation(p, HotspotFit.Seed(mesh.Id, region.Min()), settings.DisallowRandom);
        var axis = settings.Tiling == HotspotTiling.No ? TileAxis.None : p.TileAxis;
        foreach (var f in region)
            mesh.Faces[f].UVs = layout[f].Select(pt => PatchUv(asset, p, axis, chosen.Rotated, variation, rect.Local(pt), rect.Size, texWidth, texHeight)).ToArray();
        return new HotspotRegionResult(region, chosen.Patch, chosen.Rotated, variation);
    }

    /// <summary>
    /// The UV for a point at <paramref name="st"/> (0..1 across the region rect of <paramref name="regionMetres"/>).
    /// Non-tiling patches stretch the region to fill the inset rect. Tiling patches match the cross axis and repeat
    /// along the other at the same texel scale, starting at the patch's edge (insets apply across only).
    /// </summary>
    public static Vector2 PatchUv(HotspotAsset asset, HotspotPatch p, TileAxis axis, bool rotated, PatchVariation v, Vector2 st,
        Vector2 regionMetres, int texWidth, int texHeight)
    {
        float a = rotated ? st.Y : st.X;
        float b = rotated ? 1 - st.X : st.Y;
        if (v.Rotate180) { a = 1 - a; b = 1 - b; }
        if (v.MirrorX) a = 1 - a;
        if (v.MirrorY) b = 1 - b;
        float rw = MathF.Max(rotated ? regionMetres.Y : regionMetres.X, 1e-6f);
        float rh = MathF.Max(rotated ? regionMetres.X : regionMetres.Y, 1e-6f);

        var (ix, iy) = asset.InsetFraction(p);
        if (axis == TileAxis.U) ix = 0;
        if (axis == TileAxis.V) iy = 0;
        ix = MathF.Min(ix, p.W * 0.45f);
        iy = MathF.Min(iy, p.H * 0.45f);
        var min = new Vector2(p.X + ix, p.Y + iy);
        var size = new Vector2(p.W - 2 * ix, p.H - 2 * iy);
        switch (axis)
        {
            case TileAxis.U:
            {
                float texelsPerMetre = size.Y * texHeight / rh;
                return new Vector2(min.X + a * rw * texelsPerMetre / texWidth, min.Y + b * size.Y);
            }
            case TileAxis.V:
            {
                float texelsPerMetre = size.X * texWidth / rw;
                return new Vector2(min.X + a * size.X, min.Y + b * rh * texelsPerMetre / texHeight);
            }
            default:
                return min + new Vector2(a, b) * size;
        }
    }

    /// <summary>
    /// The regions to fit: every face alone when <paramref name="individual"/>, else faces joined across shared edges
    /// whose normals stay within <see cref="CoplanarDegrees"/> of the region's first face.
    /// </summary>
    public static List<List<int>> Regions(PolyMesh mesh, IReadOnlyList<int> faces, bool individual)
    {
        var valid = faces.Where(f => f >= 0 && f < mesh.Faces.Count).Distinct().Order().ToList();
        if (individual) return valid.Select(f => new List<int> { f }).ToList();
        var set = valid.ToHashSet();
        var edgeFaces = Topology.EdgeFaces(mesh);
        float cos = MathF.Cos(CoplanarDegrees * MathF.PI / 180f);
        var done = new HashSet<int>();
        var regions = new List<List<int>>();
        foreach (var start in valid)
        {
            if (!done.Add(start)) continue;
            var n0 = mesh.FaceNormal(start);
            var region = new List<int> { start };
            var stack = new Stack<int>([start]);
            while (stack.Count > 0)
                foreach (var e in mesh.Faces[stack.Pop()].Edges())
                    foreach (var n in edgeFaces[e])
                        if (set.Contains(n) && !done.Contains(n) && Vector3.Dot(mesh.FaceNormal(n), n0) >= cos)
                        {
                            done.Add(n);
                            region.Add(n);
                            stack.Push(n);
                        }
            region.Sort();
            regions.Add(region);
        }
        return regions;
    }

    /// <summary>
    /// The region flattened to metres. Automatic uses the square unwrap unless it stretches some edge by more than
    /// <see cref="MaxSquareDistortion"/>, then falls back to conforming (true shape). Follow quads starts from
    /// <paramref name="reference"/> (the active face) when it is in the region.
    /// </summary>
    public static Dictionary<int, Vector2[]> Layout(PolyMesh mesh, IReadOnlyList<int> region, HotspotMapping mapping, int reference = -1)
    {
        int start = region.Contains(reference) ? reference : -1;
        switch (mapping)
        {
            case HotspotMapping.Conforming:
                return Unwrap.Conforming(mesh, region, start);
            case HotspotMapping.FollowQuads:
                return Unwrap.FollowQuads(mesh, region, QuadSpacing.Length, start);
            case HotspotMapping.Square:
                return Unwrap.FollowQuads(mesh, region, QuadSpacing.Even, start, square: true);
            default:
                var square = Unwrap.FollowQuads(mesh, region, QuadSpacing.Even, start, square: true);
                return Distortion(mesh, square) <= MaxSquareDistortion ? square : Unwrap.Conforming(mesh, region, start);
        }
    }

    /// <summary>Largest edge stretch of a layout against the real edge lengths, as |log(layout / world)|.</summary>
    public static float Distortion(PolyMesh mesh, Dictionary<int, Vector2[]> layout)
    {
        float worst = 0;
        foreach (var (f, pts) in layout)
        {
            var verts = mesh.Faces[f].Verts;
            for (int i = 0; i < verts.Length; i++)
            {
                int j = (i + 1) % verts.Length;
                float world = Vector3.Distance(mesh.Positions[verts[i]], mesh.Positions[verts[j]]);
                float flat = Vector2.Distance(pts[i], pts[j]);
                if (world < 1e-6f || flat < 1e-6f) { if (MathF.Abs(world - flat) > 1e-4f) return float.PositiveInfinity; continue; }
                worst = MathF.Max(worst, MathF.Abs(MathF.Log(flat / world)));
            }
        }
        return worst;
    }

    /// <summary>
    /// The smallest-area rectangle around the points, turned at most 45° from the layout's own axes (so walls stay upright
    /// and only skewed regions, like a diagonal floor strip, get a turned frame). Ties keep the layout's axes.
    /// </summary>
    public static RegionRect Bound(IEnumerable<Vector2> points)
    {
        var pts = points.ToList();
        if (pts.Count == 0) return new RegionRect(Vector2.Zero, Vector2.UnitX, Vector2.UnitY, 0, 0);
        var hull = Hull(pts);
        var best = RectAt(pts, 0);
        float bestArea = best.Width * best.Height;
        for (int i = 0; i < hull.Count; i++)
        {
            var d = hull[(i + 1) % hull.Count] - hull[i];
            if (d.LengthSquared() < 1e-12f) continue;
            float angle = MathF.Atan2(d.Y, d.X);
            float quarter = MathF.PI / 2;
            angle -= MathF.Round(angle / quarter) * quarter; // into [-45°, 45°]
            var r = RectAt(pts, angle);
            float area = r.Width * r.Height;
            if (area < bestArea * 0.999f - 1e-9f) { best = r; bestArea = area; }
        }
        return best;
    }

    static RegionRect RectAt(List<Vector2> pts, float angle)
    {
        var u = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        var v = new Vector2(-u.Y, u.X);
        float u0 = float.MaxValue, u1 = float.MinValue, v0 = float.MaxValue, v1 = float.MinValue;
        foreach (var p in pts)
        {
            float a = Vector2.Dot(p, u), b = Vector2.Dot(p, v);
            u0 = MathF.Min(u0, a); u1 = MathF.Max(u1, a);
            v0 = MathF.Min(v0, b); v1 = MathF.Max(v1, b);
        }
        return new RegionRect(u * u0 + v * v0, u, v, u1 - u0, v1 - v0);
    }

    /// <summary>Convex hull (Andrew's monotone chain), counter-clockwise.</summary>
    static List<Vector2> Hull(List<Vector2> points)
    {
        var p = points.Distinct().OrderBy(q => q.X).ThenBy(q => q.Y).ToList();
        if (p.Count < 3) return p;
        static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var hull = new List<Vector2>();
        foreach (var q in p)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], q) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(q);
        }
        int lower = hull.Count + 1;
        for (int i = p.Count - 2; i >= 0; i--)
        {
            while (hull.Count >= lower && Cross(hull[^2], hull[^1], p[i]) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p[i]);
        }
        hull.RemoveAt(hull.Count - 1);
        return hull;
    }
}
