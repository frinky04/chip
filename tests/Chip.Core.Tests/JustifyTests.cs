using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Texturing;
using static Chip.Core.Tests.TexturingTests;

namespace Chip.Core.Tests;

/// <summary>Justify, fit, shift, scale, rotate and flip maths on projected and explicit faces.</summary>
public class JustifyTests
{
    static List<FaceRef> Refs(PolyMesh m, params int[] faces) => faces.Select(f => new FaceRef(m, f)).ToList();

    static (Vector2 Min, Vector2 Max) B(PolyMesh m, params int[] faces) => UvSpace.Bounds(Refs(m, faces), Size);

    [Theory]
    [InlineData(JustifyMode.Left)]
    [InlineData(JustifyMode.Right)]
    [InlineData(JustifyMode.Top)]
    [InlineData(JustifyMode.Bottom)]
    [InlineData(JustifyMode.Center)]
    public void Justify_aligns_the_texture_edge_with_the_face(JustifyMode mode)
    {
        var m = FloorStrip(0.6f);
        m.Faces[0].Map = m.Faces[0].Map with { ShiftU = 77, ShiftV = -31 };
        var size0 = B(m, 0).Max - B(m, 0).Min;
        Justify.Align(Refs(m, 0), mode, Size);
        Assert.Null(m.Faces[0].UVs);
        var (min, max) = B(m, 0);
        AssertNear(size0, max - min);
        switch (mode)
        {
            case JustifyMode.Left: AssertNear(0, min.X); break;
            case JustifyMode.Right: AssertNear(1, max.X); break;
            case JustifyMode.Top: AssertNear(0, min.Y); break;
            case JustifyMode.Bottom: AssertNear(1, max.Y); break;
            default: AssertNear(new Vector2(0.5f), (min + max) * 0.5f); break;
        }
    }

    [Fact]
    public void Treat_as_one_justifies_the_region_and_keeps_faces_continuous()
    {
        var m = FloorStrip(1, 1);
        Unwrap.Apply(m, [0, 1], UnwrapMode.Conforming, Size);
        Justify.Shift(Refs(m, 0, 1), new Vector2(100, 50), Size);
        Justify.Align(Refs(m, 0, 1), JustifyMode.Left, Size, treatAsOne: true);
        AssertNear(0, B(m, 0, 1).Min.X);
        AssertSeamless(m, 0, 1);

        Justify.Align(Refs(m, 0, 1), JustifyMode.Left, Size, treatAsOne: false);
        AssertNear(0, B(m, 0).Min.X);
        AssertNear(0, B(m, 1).Min.X);
    }

    [Fact]
    public void Fit_both_makes_the_bounds_one_tile()
    {
        var m = FloorStrip(3);
        Justify.Fit(Refs(m, 0), new FitOptions(), Size);
        var (min, max) = B(m, 0);
        AssertNear(Vector2.Zero, min);
        AssertNear(Vector2.One, max);
        Assert.Null(m.Faces[0].UVs);
    }

    [Fact]
    public void Fit_height_with_keep_ratio_spans_floor_to_ceiling_across_walls()
    {
        // Two 2 m x 3 m walls side by side, fitted as one region: V spans exactly one tile, U keeps the aspect.
        var m = Box(Vector3.Zero, new Vector3(2, 3, 2));
        int a = FaceWithNormal(m, Vector3.UnitZ), b = FaceWithNormal(m, Vector3.UnitX);
        Unwrap.Apply(m, [a, b], UnwrapMode.Conforming, Size, reference: a);
        Justify.Fit(Refs(m, a, b), new FitOptions(FitMode.Height, KeepRatio: true, TreatAsOne: true), Size);
        var (min, max) = B(m, a, b);
        AssertNear(0, min.Y);
        AssertNear(1, max.Y);
        AssertNear(4f / 3f, max.X - min.X); // 4 m wide at 1/3 tile per metre
        AssertSeamless(m, a, b);
    }

    [Fact]
    public void Fit_width_without_keep_ratio_leaves_v_alone()
    {
        var m = FloorStrip(2);
        var before = B(m, 0);
        Justify.Fit(Refs(m, 0), new FitOptions(FitMode.Width), Size);
        var (min, max) = B(m, 0);
        AssertNear(0, min.X);
        AssertNear(1, max.X);
        AssertNear(before.Min.Y, min.Y);
        AssertNear(before.Max.Y, max.Y);
    }

    [Theory]
    [InlineData(FitMode.Both, false, 0.25f, 0.5f, -0.25f, -1f)]
    [InlineData(FitMode.Both, true, 0.25f, 0.25f, -0.25f, -0.5f)]
    [InlineData(FitMode.Width, true, 0.25f, 0.25f, -0.25f, -0.5f)]
    [InlineData(FitMode.Height, false, 1f, 0.5f, 0f, -1f)]
    public void Fit_transform_maths(FitMode mode, bool keep, float au, float av, float bu, float bv)
    {
        var (a, b) = Justify.FitTransform(new Vector2(1, 2), new Vector2(5, 4), new FitOptions(mode, keep));
        AssertNear(new Vector2(au, av), a);
        AssertNear(new Vector2(bu, bv), b);
    }

    [Fact]
    public void Shift_moves_by_texels_on_both_representations()
    {
        var m = FloorStrip(1, 1);
        Unwrap.Apply(m, [1], UnwrapMode.UseExisting, Size);
        var before0 = UvAt(m, 0, 0);
        var before1 = UvAt(m, 1, 2);
        Justify.Shift(Refs(m, 0, 1), new Vector2(128, -64), Size);
        AssertNear(before0 + new Vector2(0.25f, -0.125f), UvAt(m, 0, 0));
        AssertNear(before1 + new Vector2(0.25f, -0.125f), UvAt(m, 1, 2));
        AssertNear(128, m.Faces[0].Map.ShiftU);
    }

    [Fact]
    public void Scale_grows_the_texture_about_the_face_centre()
    {
        var m = FloorStrip(2);
        var (min0, max0) = B(m, 0);
        Justify.Scale(Refs(m, 0), new Vector2(2, 4), Size);
        var (min, max) = B(m, 0);
        AssertNear((max0 - min0) / new Vector2(2, 4), max - min);
        AssertNear((min0 + max0) * 0.5f, (min + max) * 0.5f);
        AssertNear(2 * TextureMapping.DefaultScale, m.Faces[0].Map.ScaleU, 1e-6f);
    }

    [Fact]
    public void Rotate_quarter_turn_swaps_extents_about_the_centre()
    {
        var m = FloorStrip(2);
        foreach (bool bake in new[] { false, true })
        {
            var mm = m.Clone();
            if (bake) UvSpace.Bake(new FaceRef(mm, 0), Size);
            var (min0, max0) = B(mm, 0);
            Justify.Rotate(Refs(mm, 0), 90, Size);
            var (min, max) = B(mm, 0);
            AssertNear(new Vector2((max0 - min0).Y, (max0 - min0).X), max - min);
            AssertNear((min0 + max0) * 0.5f, (min + max) * 0.5f);
        }
    }

    [Fact]
    public void Flip_mirrors_within_the_bounds()
    {
        var m = FloorStrip(2);
        var before = UvSpace.Get(new FaceRef(m, 0), Size);
        var (min, max) = B(m, 0);
        Justify.Flip(Refs(m, 0), horizontal: true, Size);
        var after = UvSpace.Get(new FaceRef(m, 0), Size);
        for (int i = 0; i < 4; i++)
        {
            AssertNear(min.X + max.X - before[i].X, after[i].X);
            AssertNear(before[i].Y, after[i].Y);
        }
        Assert.Null(m.Faces[0].UVs);
        Justify.Flip(Refs(m, 0), horizontal: false, Size);
        AssertNear(min.Y + max.Y - before[0].Y, UvAt(m, 0, m.Faces[0].Verts[0]).Y);
    }
}
