using System.Numerics;

namespace Chip.Core.Geometry;

/// <summary>A 2D point in double precision, for work in a face's plane.</summary>
internal readonly record struct P2(double X, double Y)
{
    public static P2 operator +(P2 a, P2 b) => new(a.X + b.X, a.Y + b.Y);
    public static P2 operator -(P2 a, P2 b) => new(a.X - b.X, a.Y - b.Y);
    public static P2 operator *(P2 a, double s) => new(a.X * s, a.Y * s);
    public static double Cross(P2 a, P2 b) => a.X * b.Y - a.Y * b.X;
    public static double Dot(P2 a, P2 b) => a.X * b.X + a.Y * b.Y;
    public double Length => Math.Sqrt(X * X + Y * Y);

    /// <summary>Distance from <paramref name="p"/> to segment a-b.</summary>
    public static double SegmentDistance(P2 p, P2 a, P2 b)
    {
        var ab = b - a;
        double len2 = Dot(ab, ab);
        double t = len2 > 0 ? Math.Clamp(Dot(p - a, ab) / len2, 0, 1) : 0;
        return (a + ab * t - p).Length;
    }

    /// <summary>Twice the signed area of a loop: positive when counter-clockwise.</summary>
    public static double LoopArea(IReadOnlyList<P2> loop)
    {
        double a = 0;
        for (int i = 0; i < loop.Count; i++) a += Cross(loop[i], loop[(i + 1) % loop.Count]);
        return a;
    }

    /// <summary>Winding number of the loop around <paramref name="p"/> (counter-clockwise counts positive).</summary>
    public static int Winding(IReadOnlyList<P2> loop, P2 p)
    {
        int w = 0;
        for (int i = 0; i < loop.Count; i++)
        {
            var a = loop[i]; var b = loop[(i + 1) % loop.Count];
            if (a.Y <= p.Y) { if (b.Y > p.Y && Cross(b - a, p - a) > 0) w++; }
            else if (b.Y <= p.Y && Cross(b - a, p - a) < 0) w--;
        }
        return w;
    }
}

/// <summary>
/// A face's plane as a 2D frame: (U, V, Normal) is right-handed, so a loop that is counter-clockwise seen from the front
/// has positive area in 2D. The normal is Newell's, so a slightly warped face still gets a sensible frame; a loop whose
/// area cancels out (a symmetric bow tie) falls back to the plane of its widest corner triangle.
/// </summary>
internal readonly struct FaceFrame
{
    public readonly Vector3 Origin, U, V, Normal;

    public FaceFrame(PolyMesh m, int face)
    {
        var verts = m.Faces[face].Verts;
        var P = m.Positions;
        Origin = P[verts[0]];
        // Newell's normal, relative to the first corner for precision far from the world origin.
        var n = Vector3.Zero;
        float reach = 0;
        for (int i = 0; i < verts.Length; i++)
        {
            var a = P[verts[i]] - Origin; var b = P[verts[(i + 1) % verts.Length]] - Origin;
            n += new Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
            reach = MathF.Max(reach, a.LengthSquared());
        }
        if (n.LengthSquared() <= 1e-12f * reach * reach)
        {
            n = Vector3.Zero;
            for (int i = 1; i < verts.Length; i++)
                for (int j = i + 1; j < verts.Length; j++)
                {
                    var c = Vector3.Cross(P[verts[i]] - Origin, P[verts[j]] - Origin);
                    if (c.LengthSquared() > n.LengthSquared()) n = c;
                }
        }
        Normal = n.LengthSquared() > 1e-30f ? Vector3.Normalize(n) : Vector3.UnitY;
        U = Vector3.Normalize(Vector3.Cross(Normal, MathF.Abs(Normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        V = Vector3.Cross(Normal, U);
    }

    public P2 To2(Vector3 p)
    {
        var r = p - Origin;
        return new P2((double)r.X * U.X + (double)r.Y * U.Y + (double)r.Z * U.Z, (double)r.X * V.X + (double)r.Y * V.Y + (double)r.Z * V.Z);
    }

    public Vector3 To3(P2 p) => Origin + U * (float)p.X + V * (float)p.Y;

    public P2[] Loop(PolyMesh m, int face)
    {
        var verts = m.Faces[face].Verts;
        var loop = new P2[verts.Length];
        for (int i = 0; i < verts.Length; i++) loop[i] = To2(m.Positions[verts[i]]);
        return loop;
    }

    /// <summary>
    /// Length below which two points of this loop count as one: a millionth of its size, but never finer than float
    /// positions this far from the world origin can resolve.
    /// </summary>
    public static double Epsilon(ReadOnlySpan<P2> loop, Vector3 origin)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var p in loop)
        {
            x0 = Math.Min(x0, p.X); x1 = Math.Max(x1, p.X);
            y0 = Math.Min(y0, p.Y); y1 = Math.Max(y1, p.Y);
        }
        double size = Math.Max(x1 - x0, y1 - y0);
        double reach = origin.Length() + size;
        return Math.Max(Math.Min(Math.Max(size * 1e-6, reach * 4e-7), size * 1e-2), 1e-12);
    }
}
