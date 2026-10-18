using System.Numerics;
using Chip.Core.Texturing;

namespace Chip.Core.Geometry.Primitives;

/// <summary>
/// Creation of new meshes: the block tool shapes, open rectangles and extruded polygons. Every result is closed
/// (except <see cref="Rectangle"/>), wound outward, world-aligned (<see cref="MeshOps.AlignToGrid"/>) and uses one material.
/// </summary>
public static partial class Primitive
{
    public static string Name(PrimitiveShape shape) => shape switch
    {
        PrimitiveShape.Box => "block",
        PrimitiveShape.SpiralStairs => "spiral stairs",
        _ => shape.ToString().ToLowerInvariant(),
    };

    /// <summary>Builds <paramref name="shape"/> so that it exactly fills <paramref name="frame"/>.</summary>
    public static PolyMesh Build(PrimitiveShape shape, PrimitiveOptions options, PrimitiveFrame frame, string material, TextureMapping scaleFrom)
    {
        if (shape == PrimitiveShape.Box)
        {
            var box = MeshOps.CreateBox(frame.Min, frame.Max, material, scaleFrom);
            box.Name = Name(shape);
            return box;
        }
        var o = options.Clone();
        o.Clamp();
        var m = BuildLocal(shape, o, frame.Size);
        FitTo(m, frame.Size);
        for (int i = 0; i < m.Positions.Count; i++) m.Positions[i] = frame.ToWorld(m.Positions[i]);
        m.Name = Name(shape);
        if (o.Smooth)
        {
            switch (shape)
            {
                case PrimitiveShape.Cylinder: Soften(m, Enumerable.Range(2, m.Faces.Count - 2), 180); break;
                case PrimitiveShape.Arch: Soften(m, Enumerable.Range(2, m.Faces.Count - 2), 60); break;
                case PrimitiveShape.Sphere or PrimitiveShape.Torus: Soften(m, Enumerable.Range(0, m.Faces.Count), 180); break;
            }
        }
        return Finish(m, material, scaleFrom);
    }

    /// <summary>
    /// A closed prism: <paramref name="loop"/> (a planar polygon, convex or concave, either winding) swept by
    /// <paramref name="extrude"/>. Returns null when fewer than three distinct points remain.
    /// </summary>
    public static PolyMesh? Prism(IReadOnlyList<Vector3> loop, Vector3 extrude, string material, TextureMapping scaleFrom)
    {
        var pts = Dedupe(loop);
        if (pts.Count < 3 || extrude.LengthSquared() < 1e-12f) return null;
        var m = new PolyMesh { Name = "prism" };
        AppendPrism(m, pts, extrude);
        return Finish(m, material, scaleFrom);
    }

    /// <summary>
    /// A single open quad spanning corners <paramref name="a"/> and <paramref name="b"/> on the axis plane whose normal is
    /// closest to <paramref name="facing"/>; the face points along that normal. Returns null for a degenerate rectangle.
    /// </summary>
    public static PolyMesh? Rectangle(Vector3 a, Vector3 b, Vector3 facing, string material, TextureMapping scaleFrom)
    {
        var n = TextureMapping.DominantAxis(facing);
        var (u, v) = BaseAxes(n);
        var d = b - a;
        float du = Vector3.Dot(d, u), dv = Vector3.Dot(d, v);
        if (MathF.Abs(du) < 1e-5f || MathF.Abs(dv) < 1e-5f) return null;
        var m = new PolyMesh { Name = "plane" };
        m.Positions.AddRange([a, a + u * du, a + u * du + v * dv, a + v * dv]);
        m.Faces.Add(new Face([0, 1, 2, 3], material, default));
        if (Vector3.Dot(m.FaceNormal(0), n) < 0) Array.Reverse(m.Faces[0].Verts);
        MeshOps.AlignToGrid(m, [0], scaleFrom);
        return m;
    }

    /// <summary>The two world axes spanning the plane with axis normal <paramref name="n"/>.</summary>
    public static (Vector3 U, Vector3 V) BaseAxes(Vector3 n)
    {
        n = TextureMapping.DominantAxis(n);
        if (n.X != 0) return (Vector3.UnitZ, Vector3.UnitY);
        if (n.Y != 0) return (Vector3.UnitX, Vector3.UnitZ);
        return (Vector3.UnitX, Vector3.UnitY);
    }

    /// <summary>Stair count for a rise of <paramref name="height"/> with steps close to <paramref name="stepHeight"/>.</summary>
    public static int StepsFor(float height, float stepHeight) =>
        stepHeight <= 1e-4f ? 1 : Math.Max(1, (int)MathF.Round(MathF.Abs(height) / stepHeight));

    /// <summary>
    /// True when the polygon (projected along <paramref name="normal"/>) has no crossing or touching non-adjacent edges.
    /// </summary>
    public static bool IsSimple(IReadOnlyList<Vector3> loop, Vector3 normal)
    {
        var pts = Dedupe(loop);
        int n = pts.Count;
        if (n < 3) return false;
        var p2 = Project(pts, normal);
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                if (j == i + 1 || (i == 0 && j == n - 1)) continue; // adjacent edges share a corner
                if (SegmentsTouch(p2[i], p2[(i + 1) % n], p2[j], p2[(j + 1) % n])) return false;
            }
        return Math.Abs(SignedArea(p2)) > 1e-8f;
    }

    /// <summary>Signed volume enclosed by a closed mesh (positive when faces wind outward).</summary>
    public static float SignedVolume(PolyMesh m)
    {
        double v = 0;
        foreach (var f in m.Faces)
        {
            var p0 = m.Positions[f.Verts[0]];
            for (int i = 1; i + 1 < f.Verts.Length; i++)
                v += Vector3.Dot(p0, Vector3.Cross(m.Positions[f.Verts[i]], m.Positions[f.Verts[i + 1]]));
        }
        return (float)(v / 6);
    }

    // ---- shared helpers ----

    /// <summary>Winds every face outward, applies the material and world-aligned mapping.</summary>
    static PolyMesh Finish(PolyMesh m, string material, TextureMapping scaleFrom)
    {
        if (SignedVolume(m) < 0)
            foreach (var f in m.Faces) Array.Reverse(f.Verts);
        foreach (var f in m.Faces) f.Material = material;
        MeshOps.AlignToGrid(m, Enumerable.Range(0, m.Faces.Count), scaleFrom);
        return m;
    }

    /// <summary>
    /// Appends a closed prism. Face order: bottom, top, then one side per loop edge (side i joins loop[i] and loop[i+1]
    /// of the bottom loop as stored, which is <paramref name="loop"/> or its reverse).
    /// </summary>
    static void AppendPrism(PolyMesh m, IReadOnlyList<Vector3> loop, Vector3 extrude)
    {
        int n = loop.Count, b = m.Positions.Count;
        var bottom = loop.ToList();
        if (Vector3.Dot(Newell(bottom), extrude) > 0) bottom.Reverse(); // bottom faces away from the sweep
        m.Positions.AddRange(bottom);
        m.Positions.AddRange(bottom.Select(p => p + extrude));
        m.Faces.Add(new Face(Enumerable.Range(b, n).ToArray(), "", default));
        m.Faces.Add(new Face(Enumerable.Range(0, n).Select(i => b + n + (n - 1 - i)).ToArray(), "", default));
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            m.Faces.Add(new Face([b + j, b + i, b + n + i, b + n + j], "", default));
        }
    }

    /// <summary>Scales local positions so their bounds are exactly [0, size] on every axis with extent.</summary>
    static void FitTo(PolyMesh m, Vector3 size)
    {
        var (min, max) = m.Bounds();
        var ext = max - min;
        for (int i = 0; i < m.Positions.Count; i++)
        {
            var p = m.Positions[i] - min;
            m.Positions[i] = new Vector3(
                ext.X > 1e-7f ? p.X / ext.X * size.X : p.X,
                ext.Y > 1e-7f ? p.Y / ext.Y * size.Y : p.Y,
                ext.Z > 1e-7f ? p.Z / ext.Z * size.Z : p.Z);
        }
    }

    /// <summary>Marks edges shared by two candidate faces whose normals differ by less than <paramref name="maxAngle"/> degrees.</summary>
    static void Soften(PolyMesh m, IEnumerable<int> candidates, float maxAngle)
    {
        var set = candidates.ToHashSet();
        float cos = MathF.Cos(maxAngle * MathF.PI / 180f);
        foreach (var (e, faces) in Topology.EdgeFaces(m))
            if (faces.Count == 2 && set.Contains(faces[0]) && set.Contains(faces[1])
                && (maxAngle >= 180 || Vector3.Dot(m.FaceNormal(faces[0]), m.FaceNormal(faces[1])) > cos))
                m.SoftEdges.Add(e);
    }

    static List<Vector3> Dedupe(IReadOnlyList<Vector3> loop)
    {
        var pts = new List<Vector3>(loop.Count);
        foreach (var p in loop)
            if (pts.Count == 0 || Vector3.DistanceSquared(pts[^1], p) > 1e-10f) pts.Add(p);
        while (pts.Count > 1 && Vector3.DistanceSquared(pts[0], pts[^1]) <= 1e-10f) pts.RemoveAt(pts.Count - 1);
        return pts;
    }

    static Vector3 Newell(IReadOnlyList<Vector3> pts)
    {
        var n = Vector3.Zero;
        for (int i = 0; i < pts.Count; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            n.X += (a.Y - b.Y) * (a.Z + b.Z);
            n.Y += (a.Z - b.Z) * (a.X + b.X);
            n.Z += (a.X - b.X) * (a.Y + b.Y);
        }
        return n;
    }

    static Vector2[] Project(IReadOnlyList<Vector3> pts, Vector3 normal)
    {
        var n = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY;
        var t = Vector3.Normalize(Vector3.Cross(MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX, n));
        var b = Vector3.Cross(n, t);
        return pts.Select(p => new Vector2(Vector3.Dot(p, t), Vector3.Dot(p, b))).ToArray();
    }

    static float SignedArea(Vector2[] p)
    {
        float a = 0;
        for (int i = 0; i < p.Length; i++) a += p[i].X * p[(i + 1) % p.Length].Y - p[(i + 1) % p.Length].X * p[i].Y;
        return a * 0.5f;
    }

    static bool SegmentsTouch(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        static float Cross(Vector2 o, Vector2 p, Vector2 q) => (p.X - o.X) * (q.Y - o.Y) - (p.Y - o.Y) * (q.X - o.X);
        static bool On(Vector2 p, Vector2 q, Vector2 r) =>
            MathF.Min(p.X, q.X) - 1e-6f <= r.X && r.X <= MathF.Max(p.X, q.X) + 1e-6f &&
            MathF.Min(p.Y, q.Y) - 1e-6f <= r.Y && r.Y <= MathF.Max(p.Y, q.Y) + 1e-6f;
        const float eps = 1e-9f;
        float d1 = Cross(c, d, a), d2 = Cross(c, d, b), d3 = Cross(a, b, c), d4 = Cross(a, b, d);
        if (((d1 > eps && d2 < -eps) || (d1 < -eps && d2 > eps)) && ((d3 > eps && d4 < -eps) || (d3 < -eps && d4 > eps))) return true;
        return (MathF.Abs(d1) <= eps && On(c, d, a)) || (MathF.Abs(d2) <= eps && On(c, d, b))
            || (MathF.Abs(d3) <= eps && On(a, b, c)) || (MathF.Abs(d4) <= eps && On(a, b, d));
    }
}
