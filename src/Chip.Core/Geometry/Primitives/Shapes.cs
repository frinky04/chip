using System.Numerics;

namespace Chip.Core.Geometry.Primitives;

/// <summary>
/// Shape generators in the frame's local space: X = run, Y = up, Z = across, roughly filling [0, size].
/// <see cref="Primitive.Build"/> then fits the result to the box exactly and fixes the winding, so generators only
/// need consistent (not necessarily outward) face loops.
/// </summary>
public static partial class Primitive
{
    /// <summary>Unfitted, unwound local mesh for <paramref name="shape"/>; options must already be clamped.</summary>
    static PolyMesh BuildLocal(PrimitiveShape shape, PrimitiveOptions o, Vector3 size)
    {
        var m = new PolyMesh();
        float w = size.X, h = size.Y, d = size.Z;
        switch (shape)
        {
            case PrimitiveShape.Box:
                AppendPrism(m, [new(0, 0, 0), new(w, 0, 0), new(w, h, 0), new(0, h, 0)], new Vector3(0, 0, d));
                break;
            case PrimitiveShape.Wedge:
                // Low edge at the start of the run, full height at its end.
                AppendPrism(m, [new(0, 0, 0), new(w, 0, 0), new(w, h, 0)], new Vector3(0, 0, d));
                break;
            case PrimitiveShape.Stairs:
                AppendPrism(m, StairProfile(o.Steps, w, h), new Vector3(0, 0, d));
                break;
            case PrimitiveShape.Cylinder:
                AppendPrism(m, Ring(o.Sides, w * 0.5f, d * 0.5f, 0, 2 * MathF.PI, closed: true)
                    .Select(p => new Vector3(p.X + w * 0.5f, 0, p.Y + d * 0.5f)).ToList(), new Vector3(0, h, 0));
                break;
            case PrimitiveShape.Arch:
                AppendPrism(m, ArchProfile(o, w, h), new Vector3(0, 0, d));
                break;
            case PrimitiveShape.Sphere:
                AppendSphere(m, o.SphereRings, o.SphereSegments, size);
                break;
            case PrimitiveShape.Torus:
                AppendTorus(m, o.TorusSegments, o.TorusSides, o.TorusTube, size);
                break;
            case PrimitiveShape.SpiralStairs:
                AppendSpiral(m, o, size);
                break;
        }
        return m;
    }

    /// <summary>
    /// <paramref name="n"/> points on an ellipse (radii rx, ry) from angle a0 to a1. A closed ring is offset by half a step so
    /// that, for counts divisible by four, flat sides face the axes (an 8-sided cylinder sits flush on the grid).
    /// </summary>
    static List<Vector2> Ring(int n, float rx, float ry, float a0, float a1, bool closed)
    {
        var pts = new List<Vector2>(n + 1);
        int count = closed ? n : n + 1;
        for (int i = 0; i < count; i++)
        {
            float a = closed ? a0 + (i + 0.5f) * (a1 - a0) / n : a0 + i * (a1 - a0) / n;
            pts.Add(new Vector2(MathF.Cos(a) * rx, MathF.Sin(a) * ry));
        }
        return pts;
    }

    /// <summary>Side profile of straight stairs in the X/Y plane: floor, back wall, then treads and risers down to the front.</summary>
    static List<Vector3> StairProfile(int steps, float w, float h)
    {
        var pts = new List<Vector3> { new(0, 0, 0), new(w, 0, 0) };
        for (int k = steps; k >= 1; k--)
        {
            pts.Add(new Vector3(w * k / steps, h * k / steps, 0));
            pts.Add(new Vector3(w * (k - 1) / steps, h * k / steps, 0));
        }
        return pts;
    }

    /// <summary>
    /// Arch band in the X/Y plane: an elliptical arc whose chord spans the width and whose crown reaches the height,
    /// with an inner arc offset by the thickness. Both legs end on the floor.
    /// </summary>
    static List<Vector3> ArchProfile(PrimitiveOptions o, float w, float h)
    {
        // Doubles: shallow arcs come from a huge circle, and float loses the crown height to cancellation.
        int n = o.ArchSegments;
        double half = o.ArchDegrees * Math.PI / 360, s = Math.Sin(half), c = Math.Cos(half);
        double rx = w * 0.5, ax = rx / s, ay = h / (1 - c); // outer ellipse semi-axes; its centre sits ay*c below the floor
        double t = Math.Min(o.ArchThickness, 0.95 * Math.Min(h, Math.Min(rx, ax)));
        double bx = ax - t, by = ay - t;
        double inner0 = Math.Asin(Math.Clamp(ay * c / by, -1, 1)); // inner arc angle where it meets the floor

        var pts = new List<Vector3>(2 * n + 2);
        void Add(double ex, double ey, double a, double lift) =>
            pts.Add(new Vector3((float)(rx + ex * Math.Cos(a)), (float)Math.Max(0, ey * Math.Sin(a) - lift), 0));
        for (int k = 0; k <= n; k++) Add(ax, ay, Math.PI / 2 + half - k * 2 * half / n, ay * c);               // outer, left to right
        for (int k = 0; k <= n; k++) Add(bx, by, inner0 + k * (Math.PI - 2 * inner0) / n, ay * c);             // inner, right to left
        return pts;
    }

    /// <summary>UV sphere: a vertex at each pole, triangles around the poles and quads between rings.</summary>
    static void AppendSphere(PolyMesh m, int rings, int segments, Vector3 size)
    {
        var r = size * 0.5f;
        int b = m.Positions.Count;
        m.Positions.Add(new Vector3(r.X, size.Y, r.Z));
        for (int i = 1; i < rings; i++)
        {
            float theta = MathF.PI * i / rings;
            float y = MathF.Cos(theta), rad = MathF.Sin(theta);
            foreach (var p in Ring(segments, rad, rad, 0, 2 * MathF.PI, closed: true))
                m.Positions.Add(new Vector3(r.X + p.X * r.X, r.Y + y * r.Y, r.Z + p.Y * r.Z));
        }
        int bottom = m.Positions.Count;
        m.Positions.Add(new Vector3(r.X, 0, r.Z));

        int V(int ring, int seg) => b + 1 + (ring - 1) * segments + (seg % segments);
        for (int s = 0; s < segments; s++)
        {
            m.Faces.Add(new Face([b, V(1, s + 1), V(1, s)], "", default));
            for (int i = 1; i < rings - 1; i++)
                m.Faces.Add(new Face([V(i, s), V(i, s + 1), V(i + 1, s + 1), V(i + 1, s)], "", default));
            m.Faces.Add(new Face([bottom, V(rings - 1, s), V(rings - 1, s + 1)], "", default));
        }
    }

    /// <summary>Torus lying in the base plane: outer edge on the box sides, tube height equal to the box height.</summary>
    static void AppendTorus(PolyMesh m, int segments, int sides, float tube, Vector3 size)
    {
        var r = size * 0.5f;
        float major = 1 - tube;
        int b = m.Positions.Count;
        var around = Ring(segments, 1, 1, 0, 2 * MathF.PI, closed: true);
        var cross = Ring(sides, 1, 1, 0, 2 * MathF.PI, closed: true);
        foreach (var a in around)
            foreach (var t in cross)
            {
                float rad = major + tube * t.X;
                m.Positions.Add(new Vector3(r.X + a.X * rad * r.X, r.Y + t.Y * r.Y, r.Z + a.Y * rad * r.Z));
            }
        int V(int i, int j) => b + (i % segments) * sides + (j % sides);
        for (int i = 0; i < segments; i++)
            for (int j = 0; j < sides; j++)
                m.Faces.Add(new Face([V(i, j), V(i + 1, j), V(i + 1, j + 1), V(i, j + 1)], "", default));
    }

    /// <summary>
    /// Spiral staircase: one closed annular-sector block per step, climbing <see cref="PrimitiveOptions.SpiralDegrees"/>
    /// around the centre. Treads are one step thick unless <see cref="PrimitiveOptions.SpiralSolid"/>.
    /// </summary>
    static void AppendSpiral(PolyMesh m, PrimitiveOptions o, Vector3 size)
    {
        var r = size * 0.5f;
        int n = o.Steps;
        float total = o.SpiralDegrees * MathF.PI / 180f * (o.SpiralClockwise ? -1 : 1);
        float stepAngle = total / n;
        int arc = Math.Max(1, (int)MathF.Ceiling(MathF.Abs(stepAngle) / (MathF.PI / 12))); // at most 15 degrees per edge
        float stepH = size.Y / n;
        for (int i = 0; i < n; i++)
        {
            float a0 = i * stepAngle, a1 = a0 + stepAngle;
            var outer = Ring(arc, 1, 1, a0, a1, closed: false);
            var loop = outer.Select(p => new Vector3(r.X + p.X * r.X, 0, r.Z + p.Y * r.Z)).ToList();
            if (o.SpiralInner < 0.01f) loop.Add(new Vector3(r.X, 0, r.Z));
            else loop.AddRange(Enumerable.Reverse(outer).Select(p => new Vector3(r.X + p.X * o.SpiralInner * r.X, 0, r.Z + p.Y * o.SpiralInner * r.Z)));
            float y0 = o.SpiralSolid ? 0 : i * stepH, y1 = (i + 1) * stepH;
            AppendPrism(m, loop.Select(p => p with { Y = y0 }).ToList(), new Vector3(0, y1 - y0, 0));
        }
    }
}
