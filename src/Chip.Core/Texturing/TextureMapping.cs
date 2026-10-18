using System.Numerics;

namespace Chip.Core.Texturing;

/// <summary>
/// Planar projection for one face (Hammer-style face edit). UVs are derived from world position:
/// u = (dot(p, U') / ScaleU + ShiftU) / textureWidth, where U' is UAxis rotated by Rotation within the U/V plane.
/// Scale is metres per texel; the default is <see cref="TexelsPerMeter"/> px/m, so a 512 px texture tiles every 1 m.
/// Shift is in texels. V points down the image.
/// </summary>
public struct TextureMapping
{
    public const float TexelsPerMeter = 512f;
    public const float DefaultScale = 1f / TexelsPerMeter;

    public Vector3 UAxis;
    public Vector3 VAxis;
    public float ShiftU, ShiftV;
    public float ScaleU, ScaleV;
    public float Rotation; // degrees

    public static TextureMapping Default => new()
    {
        UAxis = Vector3.UnitX, VAxis = -Vector3.UnitY, ScaleU = DefaultScale, ScaleV = DefaultScale,
    };

    public readonly (Vector3 U, Vector3 V) EffectiveAxes()
    {
        float r = Rotation * MathF.PI / 180f, c = MathF.Cos(r), s = MathF.Sin(r);
        return (UAxis * c + VAxis * s, VAxis * c - UAxis * s);
    }

    public readonly Vector2 Project(Vector3 p, int texWidth, int texHeight)
    {
        var (u, v) = EffectiveAxes();
        return new Vector2(
            (Vector3.Dot(p, u) / Safe(ScaleU) + ShiftU) / texWidth,
            (Vector3.Dot(p, v) / Safe(ScaleV) + ShiftV) / texHeight);
    }

    static float Safe(float s) => MathF.Abs(s) < 1e-6f ? 1e-6f : s;

    /// <summary>World-aligned projection on the face's dominant axis. Keeps scale, resets shift and rotation.</summary>
    public static TextureMapping AlignToGrid(Vector3 normal, TextureMapping keep)
    {
        var n = DominantAxis(normal);
        var v = MathF.Abs(n.Y) > 0.5f ? Vector3.UnitZ : -Vector3.UnitY;
        return With(keep, Vector3.Cross(n, v), v);
    }

    /// <summary>Projection in the face plane; texture "down" follows world down projected onto the face.</summary>
    public static TextureMapping AlignToFace(Vector3 normal, TextureMapping keep)
    {
        var n = Vector3.Normalize(normal);
        var down = MathF.Abs(n.Y) > 0.99f ? Vector3.UnitZ : -Vector3.UnitY;
        var v = Vector3.Normalize(down - n * Vector3.Dot(down, n));
        return With(keep, Vector3.Cross(n, v), v);
    }

    static TextureMapping With(TextureMapping keep, Vector3 u, Vector3 v) => new()
    {
        UAxis = u, VAxis = v,
        ScaleU = keep.ScaleU == 0 ? DefaultScale : MathF.Abs(keep.ScaleU),
        ScaleV = keep.ScaleV == 0 ? DefaultScale : MathF.Abs(keep.ScaleV),
    };

    /// <summary>Scale and shift so the texture covers the given points exactly once.</summary>
    public readonly TextureMapping FitTo(IEnumerable<Vector3> points, int texWidth, int texHeight)
    {
        var (u, v) = EffectiveAxes();
        float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
        foreach (var p in points)
        {
            float du = Vector3.Dot(p, u), dv = Vector3.Dot(p, v);
            minU = MathF.Min(minU, du); maxU = MathF.Max(maxU, du);
            minV = MathF.Min(minV, dv); maxV = MathF.Max(maxV, dv);
        }
        var m = this;
        if (maxU - minU < 1e-4f || maxV - minV < 1e-4f) return m;
        m.ScaleU = (maxU - minU) / texWidth;
        m.ScaleV = (maxV - minV) / texHeight;
        m.ShiftU = -minU / m.ScaleU;
        m.ShiftV = -minV / m.ScaleV;
        return m;
    }

    /// <summary>Keeps UVs glued to a face moved by a rigid transform (rotation + translation, no scale).</summary>
    public readonly TextureMapping MovedRigidly(Matrix4x4 rigid)
    {
        var r = Quaternion.CreateFromRotationMatrix(rigid);
        var t = rigid.Translation;
        var m = this;
        m.UAxis = Vector3.Transform(UAxis, r);
        m.VAxis = Vector3.Transform(VAxis, r);
        var (u, v) = m.EffectiveAxes();
        m.ShiftU -= Vector3.Dot(t, u) / Safe(ScaleU);
        m.ShiftV -= Vector3.Dot(t, v) / Safe(ScaleV);
        return m;
    }

    public static Vector3 DominantAxis(Vector3 n)
    {
        float ax = MathF.Abs(n.X), ay = MathF.Abs(n.Y), az = MathF.Abs(n.Z);
        if (ay >= ax && ay >= az) return new Vector3(0, MathF.Sign(n.Y) == 0 ? 1 : MathF.Sign(n.Y), 0);
        if (ax >= az) return new Vector3(MathF.Sign(n.X), 0, 0);
        return new Vector3(0, 0, MathF.Sign(n.Z));
    }
}
