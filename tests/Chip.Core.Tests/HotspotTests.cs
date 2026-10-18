using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Hotspots;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using static Chip.Core.Tests.TexturingTests;

namespace Chip.Core.Tests;

/// <summary>Hotspot assets, patch fitting and apply: SPEC §9.</summary>
public class HotspotTests
{
    const string Mat = HotspotSampleTests.MaterialName;
    const int Tex = 1024;

    static MaterialLibrary Library()
    {
        var lib = new MaterialLibrary();
        lib.Add(new MaterialInfo(Mat, null, Tex, Tex));
        lib.Add(new MaterialInfo("dev", null, 512, 512));
        return lib;
    }

    /// <summary>A vertical quad facing +Z, <paramref name="w"/> wide and <paramref name="h"/> tall.</summary>
    static PolyMesh Wall(float w, float h, int id = 1)
    {
        var m = new PolyMesh { Id = id };
        m.Positions.AddRange([new(0, 0, 0), new(w, 0, 0), new(w, h, 0), new(0, h, 0)]);
        m.Faces.Add(new Face([0, 1, 2, 3], "dev", TextureMapping.AlignToGrid(Vector3.UnitZ, TextureMapping.Default)));
        Assert.True(m.FaceNormal(0).Z > 0.99f);
        return m;
    }

    static int Pick(float wMetres, float hMetres, HotspotTiling tiling = HotspotTiling.Allow, HotspotAsset? asset = null) =>
        HotspotFit.Best(asset ?? HotspotSampleTests.Asset(), new Vector2(wMetres, hMetres) * TextureMapping.TexelsPerMeter, Tex, Tex, tiling)!.Value.Patch;

    // ---------------------------------------------------------------- fitting

    [Fact]
    public void Square_floor_picks_the_large_square_panel()
    {
        Assert.Equal(0, Pick(2, 2));   // 1024 px region: the 512 px square beats the 256 px ones on size
        Assert.Equal(2, Pick(0.5f, 0.5f)); // 256 px region: the small square
    }

    [Fact]
    public void Wide_wall_picks_the_4_to_1_panel_and_a_2_to_1_wall_the_2_to_1_panel()
    {
        Assert.Equal(4, Pick(4, 1));
        Assert.Equal(1, Pick(2, 1));
    }

    [Fact]
    public void Long_strip_picks_a_tiling_trim_unless_tiling_is_off()
    {
        Assert.Equal(7, Pick(8, 0.125f)); // 4096 x 64 px: the 64 px trim, repeating along U
        int off = Pick(8, 0.125f, HotspotTiling.No);
        Assert.False(HotspotSampleTests.Asset().Patches[off].Tiling);
        Assert.True(HotspotSampleTests.Asset().Patches[Pick(2, 2, HotspotTiling.Only)].Tiling);
    }

    [Fact]
    public void Tall_wall_turns_a_rotatable_patch_but_stays_upright_when_rotation_is_off()
    {
        var asset = HotspotSampleTests.Asset();
        var fit = HotspotFit.Best(asset, new Vector2(1, 4) * 512, Tex, Tex, HotspotTiling.No)!.Value;
        Assert.True(fit.Rotated);
        Assert.True(asset.Patches[fit.Patch].AllowRotation);

        foreach (var p in asset.Patches) p.AllowRotation = false;
        var upright = HotspotFit.Best(asset, new Vector2(1, 4) * 512, Tex, Tex, HotspotTiling.No)!.Value;
        Assert.False(upright.Rotated);
        Assert.Null(HotspotFit.Orient(asset, 99, Vector2.One, Tex, Tex));
    }

    [Fact]
    public void Tiling_is_only_valid_for_patches_spanning_the_texture()
    {
        var inside = new HotspotPatch { X = 0.1f, Y = 0, W = 0.5f, H = 0.1f, Tiling = true };
        Assert.False(inside.CanTile);
        Assert.Equal(TileAxis.None, inside.TileAxis);
        inside.Normalize();
        Assert.False(inside.Tiling);

        Assert.Equal(TileAxis.U, new HotspotPatch { X = 0, Y = 0.2f, W = 1, H = 0.1f, Tiling = true }.TileAxis);
        Assert.Equal(TileAxis.V, new HotspotPatch { X = 0.3f, Y = 0, W = 0.1f, H = 1, Tiling = true }.TileAxis);
        Assert.Equal(TileAxis.None, new HotspotPatch { X = 0, Y = 0.2f, W = 1, H = 0.1f, Tiling = false }.TileAxis);

        var asset = new HotspotAsset { Patches = [new HotspotPatch { X = 0.1f, W = 0.5f, H = 0.1f, Tiling = true }] };
        Assert.Null(HotspotFit.Best(asset, new Vector2(4096, 64), Tex, Tex, HotspotTiling.Only));
    }

    [Fact]
    public void Seeds_are_deterministic_and_vary_between_faces()
    {
        Assert.Equal(HotspotFit.Seed(3, 7), HotspotFit.Seed(3, 7));
        var p = new HotspotPatch { AllowRotation = true, MirrorX = true, MirrorY = true };
        var variations = Enumerable.Range(0, 64).Select(f => HotspotFit.Variation(p, HotspotFit.Seed(1, f), false)).Distinct().Count();
        Assert.True(variations > 4, $"only {variations} distinct variations");
        Assert.Equal(default, HotspotFit.Variation(p, HotspotFit.Seed(1, 5), disallowRandom: true));
        var locked = new HotspotPatch();
        Assert.All(Enumerable.Range(0, 32), f => Assert.Equal(default, HotspotFit.Variation(locked, HotspotFit.Seed(1, f), false)));
    }

    // ---------------------------------------------------------------- apply

    [Fact]
    public void Apply_writes_uvs_inside_the_chosen_rect_minus_its_inset_and_sets_the_material()
    {
        var lib = Library();
        var asset = HotspotSampleTests.Asset();
        var m = Wall(4, 1);
        var r = HotspotApply.Apply(lib, m, [0], asset, Mat, new HotspotSettings(), individual: false);
        Assert.Single(r);
        Assert.Equal(4, r[0].Patch);
        Assert.Equal(Mat, m.Faces[0].Material);
        var p = asset.Patches[4];
        var (ix, iy) = asset.InsetFraction(p);
        var uvs = m.Faces[0].UVs!;
        Assert.All(uvs, uv =>
        {
            Assert.InRange(uv.X, p.X + ix - 1e-4f, p.X + p.W - ix + 1e-4f);
            Assert.InRange(uv.Y, p.Y + iy - 1e-4f, p.Y + p.H - iy + 1e-4f);
        });
        var (min, max) = UvSpace.Bounds(uvs);
        AssertNear(new Vector2(p.W - 2 * ix, p.H - 2 * iy), max - min); // the region fills the inset rect
    }

    [Fact]
    public void Upright_wall_keeps_the_top_of_the_patch_at_the_top_of_the_wall()
    {
        var asset = HotspotSampleTests.Asset();
        var m = Wall(4, 1);
        HotspotApply.Apply(Library(), m, [0], asset, Mat, new HotspotSettings { DisallowRandom = true }, individual: true);
        var uvs = m.Faces[0].UVs!;
        // Corners 2 and 3 are at the top (y = 1): smaller V (the image's top).
        Assert.True(uvs[3].Y < uvs[0].Y && uvs[2].Y < uvs[1].Y);
        Assert.True(uvs[1].X > uvs[0].X); // left to right, not mirrored
    }

    [Fact]
    public void Tiling_trim_repeats_along_u_at_the_cross_scale()
    {
        var asset = HotspotSampleTests.Asset();
        var m = Wall(8, 0.125f);
        var r = HotspotApply.Apply(Library(), m, [0], asset, Mat, new HotspotSettings { DisallowRandom = true }, individual: true);
        var p = asset.Patches[r[0].Patch];
        Assert.Equal(TileAxis.U, p.TileAxis);
        var (min, max) = UvSpace.Bounds(m.Faces[0].UVs!);
        var (_, iy) = asset.InsetFraction(p);
        float cross = p.H - 2 * iy;
        AssertNear(cross, max.Y - min.Y);
        // Same texel scale along the strip: 8 m long / 0.125 m tall = 64 cross heights.
        AssertNear(cross * 64, max.X - min.X, 0.01f);
        Assert.True(max.X > 1); // repeats past the texture's edge
    }

    [Fact]
    public void Reapplying_is_stable_and_disallow_random_removes_variation()
    {
        var lib = Library();
        var asset = HotspotSampleTests.Asset();
        var a = Wall(2, 2, id: 5);
        var b = Wall(2, 2, id: 5);
        HotspotApply.Apply(lib, a, [0], asset, Mat, new HotspotSettings(), individual: true);
        HotspotApply.Apply(lib, b, [0], asset, Mat, new HotspotSettings(), individual: true);
        HotspotApply.Apply(lib, b, [0], asset, Mat, new HotspotSettings(), individual: true);
        Assert.Equal(a.Faces[0].UVs!, b.Faces[0].UVs!);

        var results = Enumerable.Range(1, 32).Select(id =>
        {
            var w = Wall(2, 2, id);
            return HotspotApply.Apply(lib, w, [0], asset, Mat, new HotspotSettings(), individual: true)[0].Variation;
        }).ToList();
        Assert.True(results.Distinct().Count() > 1, "random variation never kicked in");
        var plain = Wall(2, 2, 9);
        Assert.Equal(default, HotspotApply.Apply(lib, plain, [0], asset, Mat, new HotspotSettings { DisallowRandom = true }, individual: true)[0].Variation);
    }

    [Fact]
    public void Dont_apply_material_keeps_the_face_material()
    {
        var m = Wall(2, 2);
        HotspotApply.Apply(Library(), m, [0], HotspotSampleTests.Asset(), Mat, new HotspotSettings { DontApplyMaterial = true }, individual: true);
        Assert.Equal("dev", m.Faces[0].Material);
        Assert.NotNull(m.Faces[0].UVs);
    }

    [Fact]
    public void Coplanar_connected_faces_form_one_region_and_individual_mode_splits_them()
    {
        var strip = FloorStrip(1, 1, 1, 1); // 4 m x 1 m floor in four quads
        strip.Id = 2;
        var all = Enumerable.Range(0, 4).ToList();
        Assert.Single(HotspotApply.Regions(strip, all, individual: false));
        Assert.Equal(4, HotspotApply.Regions(strip, all, individual: true).Count);

        var asset = HotspotSampleTests.Asset();
        var r = HotspotApply.Apply(Library(), strip, all, asset, Mat, new HotspotSettings { DisallowRandom = true }, individual: false);
        Assert.Single(r);
        Assert.Equal(4, r[0].Patch); // the strip as a whole is 4:1
        // Shared corners agree, so the region reads as one panel.
        AssertSeamless(strip, 0, 1);
        AssertSeamless(strip, 2, 3);

        var box = Box(Vector3.Zero, new Vector3(2, 1, 2));
        var boxFaces = Enumerable.Range(0, box.Faces.Count).ToList();
        Assert.Equal(6, HotspotApply.Regions(box, boxFaces, individual: false).Count); // no two box sides are coplanar
    }

    [Fact]
    public void Automatic_mapping_falls_back_to_conforming_when_square_would_stretch()
    {
        var m = Wall(4, 1);
        var auto = HotspotApply.Layout(m, [0], HotspotMapping.Automatic);
        var square = HotspotApply.Layout(m, [0], HotspotMapping.Square);
        Assert.True(HotspotApply.Distortion(m, square) > HotspotApply.MaxSquareDistortion);
        Assert.True(HotspotApply.Distortion(m, auto) < 1e-3f);
        var rect = HotspotApply.Bound(auto[0]);
        AssertNear(4, rect.Width);
        AssertNear(1, rect.Height);

        var sq = Wall(1, 1);
        Assert.True(HotspotApply.Distortion(sq, HotspotApply.Layout(sq, [0], HotspotMapping.Automatic)) < 1e-3f);
        Assert.True(HotspotApply.Distortion(m, HotspotApply.Layout(m, [0], HotspotMapping.FollowQuads)) < 1e-3f);
    }

    [Fact]
    public void Bound_turns_with_a_diagonal_strip_but_not_with_an_upright_rectangle()
    {
        var upright = HotspotApply.Bound([new(0, 0), new(4, 0), new(4, 1), new(0, 1)]);
        AssertNear(Vector2.UnitX, upright.AxisU);
        var c = MathF.Cos(0.5f);
        var s = MathF.Sin(0.5f);
        Vector2 Rot(float x, float y) => new(x * c - y * s, x * s + y * c);
        var turned = HotspotApply.Bound([Rot(0, 0), Rot(4, 0), Rot(4, 1), Rot(0, 1)]);
        AssertNear(4, turned.Width);
        AssertNear(1, turned.Height);
    }

    [Fact]
    public void Forced_patch_is_used_even_when_another_fits_better()
    {
        var m = Wall(4, 1);
        var r = HotspotApply.Apply(Library(), m, [0], HotspotSampleTests.Asset(), Mat, new HotspotSettings(), individual: true, patch: 3);
        Assert.Equal(3, r[0].Patch);
    }

    [Fact]
    public void Only_tiling_with_no_tiling_patches_leaves_faces_alone()
    {
        var asset = new HotspotAsset { Materials = [Mat], Patches = [new HotspotPatch { W = 0.5f, H = 0.5f }] };
        var m = Wall(2, 2);
        var r = HotspotApply.Apply(Library(), m, [0], asset, Mat, new HotspotSettings { Tiling = HotspotTiling.Only }, individual: true);
        Assert.False(r[0].Applied);
        Assert.Null(m.Faces[0].UVs);
        Assert.Equal("dev", m.Faces[0].Material);
    }

    // ---------------------------------------------------------------- asset

    [Fact]
    public void Asset_round_trips_through_json()
    {
        var a = HotspotSampleTests.Asset();
        a.PreviewMaterial = "preview";
        a.Patches[2].Inset = 6.5f;
        var b = HotspotAsset.Parse(a.Serialize());
        Assert.Equal(a.Serialize(), b.Serialize());
        Assert.Equal("preview", b.DisplayMaterial);
        Assert.Equal(a.Patches.Count, b.Patches.Count);
        Assert.Equal("[R][X][Y][6.5]", b.Patches[2].FlagText());
        Assert.Equal("[X][T][3]", b.Patches[6].FlagText());
    }

    [Fact]
    public void Parse_clamps_bad_rects_and_drops_invalid_tiling()
    {
        const string json = """
            { "name": "x", "materials": ["a", ""], "patches": [
              { "x": -0.2, "y": 0.5, "w": 0.6, "h": 0.9, "tiling": true, "inset": -3 },
              { "x": 0.1, "y": 0.1, "w": 0, "h": 0.2 } ] }
            """;
        var a = HotspotAsset.Parse(json);
        Assert.Equal(["a"], a.Materials);
        var p = Assert.Single(a.Patches);
        Assert.Equal(0, p.X);
        Assert.True(p.Y + p.H <= 1 + 1e-6f);
        Assert.False(p.Tiling);
        Assert.Equal(0, p.Inset);
    }

    [Fact]
    public void Library_scans_folders_for_assets_and_maps_materials()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chip-hotspot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        try
        {
            HotspotSampleTests.Asset().Save(Path.Combine(dir, "sub", "concrete" + HotspotAsset.Extension));
            File.WriteAllText(Path.Combine(dir, "broken" + HotspotAsset.Extension), "{ not json");
            var lib = new HotspotLibrary();
            lib.Scan([dir, Path.Combine(dir, "missing")]);
            var asset = Assert.Single(lib.Assets);
            Assert.Single(lib.Errors);
            Assert.Same(asset, lib.ForMaterial(Mat.ToUpperInvariant()));
            Assert.Null(lib.ForMaterial("dev"));
            Assert.Equal("concrete", HotspotAsset.StemOf(asset.Path!));
        }
        finally { Directory.Delete(dir, true); }
    }
}
