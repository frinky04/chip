using System.Numerics;
using Chip.Core.Geometry;

namespace Chip.Core.Texturing;

/// <summary>Pixel size of a material's texture, by material name. UV tools need it to convert texels and metres to UVs.</summary>
public delegate (int Width, int Height) TextureSize(string material);

/// <summary>One face of one mesh: the unit the UV tools work on, so a "treat as one" edit can span meshes.</summary>
public readonly record struct FaceRef(PolyMesh Mesh, int Index)
{
    public Face Face => Mesh.Faces[Index];
}

/// <summary>
/// Shared UV maths. A face's UVs are either <b>projected</b> (<see cref="Face.Map"/>, derived from world position) or
/// <b>explicit</b> (<see cref="Face.UVs"/>, one per corner). Explicit UVs win everywhere they are present. Edits here
/// keep a face's representation where they can: an axis-aligned scale/offset or a rotation is exactly expressible as a
/// projection change, so projected faces stay projected (and world-anchored); only unwraps, wraps from explicit faces and
/// free-form UV editing write explicit UVs.
/// </summary>
public static class UvSpace
{
    /// <summary>UVs per corner as currently displayed: explicit where present, projected otherwise.</summary>
    public static Vector2[] Get(PolyMesh m, int face, int width, int height)
    {
        var f = m.Faces[face];
        var result = new Vector2[f.Verts.Length];
        for (int i = 0; i < result.Length; i++) result[i] = f.UV(i, m.Positions[f.Verts[i]], width, height);
        return result;
    }

    public static Vector2[] Get(FaceRef f, TextureSize size)
    {
        var (w, h) = size(f.Face.Material);
        return Get(f.Mesh, f.Index, w, h);
    }

    public static bool IsExplicit(Face f) => f.UVs != null && f.UVs.Length == f.Verts.Length;

    /// <summary>Freezes the face's current UVs into explicit per-corner UVs (no visual change).</summary>
    public static Vector2[] Bake(FaceRef f, TextureSize size)
    {
        if (IsExplicit(f.Face)) return f.Face.UVs!;
        return f.Face.UVs = Get(f, size);
    }

    /// <summary>Texture-space units per metre at the given density (1 = one tile).</summary>
    public static Vector2 PerMetre(int width, int height, float texelsPerMeter = TextureMapping.TexelsPerMeter) =>
        new(texelsPerMeter / width, texelsPerMeter / height);

    public static (Vector2 Min, Vector2 Max) Bounds(IEnumerable<Vector2> uvs)
    {
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        foreach (var uv in uvs) { min = Vector2.Min(min, uv); max = Vector2.Max(max, uv); }
        return min.X > max.X ? (Vector2.Zero, Vector2.Zero) : (min, max);
    }

    public static (Vector2 Min, Vector2 Max) Bounds(IEnumerable<FaceRef> faces, TextureSize size) =>
        Bounds(faces.SelectMany(f => Get(f, size)));

    /// <summary>Orthonormal frame in a plane: U right, V "down" (world down projected), as in Align to Face.</summary>
    public static (Vector3 U, Vector3 V) PlaneFrame(Vector3 normal)
    {
        var m = TextureMapping.AlignToFace(normal, TextureMapping.Default);
        return (m.UAxis, m.VAxis);
    }

    /// <summary>
    /// Applies <c>uv' = a * uv + b</c> per axis. Projected faces stay projected: scale and shift absorb it exactly.
    /// </summary>
    public static void ApplyAffine(FaceRef f, Vector2 a, Vector2 b, TextureSize size)
    {
        var face = f.Face;
        if (IsExplicit(face))
        {
            var uvs = face.UVs!;
            for (int i = 0; i < uvs.Length; i++) uvs[i] = a * uvs[i] + b;
            return;
        }
        var (w, h) = size(face.Material);
        var m = face.Map;
        // u = (d / s + shift) / w  =>  a u + b = (d / (s / a) + a shift + b w) / w
        m.ScaleU = NonZero(m.ScaleU) / NonZero(a.X);
        m.ScaleV = NonZero(m.ScaleV) / NonZero(a.Y);
        m.ShiftU = a.X * m.ShiftU + b.X * w;
        m.ShiftV = a.Y * m.ShiftV + b.Y * h;
        face.Map = m;
    }

    /// <summary>
    /// Rotates the face's texture by <paramref name="degrees"/> about <paramref name="pivot"/> (UV), measured in texel
    /// space so non-square textures don't shear. Positive turns the image clockwise on screen, matching
    /// <see cref="TextureMapping.Rotation"/>. Projected and explicit faces agree exactly when U and V scales have the
    /// same magnitude; a stretched projection rotates along its own axes instead.
    /// </summary>
    public static void Rotate(FaceRef f, float degrees, Vector2 pivot, TextureSize size)
    {
        var face = f.Face;
        var (w, h) = size(face.Material);
        var tex = new Vector2(w, h);
        float r = degrees * MathF.PI / 180f, c = MathF.Cos(r), s = MathF.Sin(r);
        if (IsExplicit(face))
        {
            var uvs = face.UVs!;
            for (int i = 0; i < uvs.Length; i++)
            {
                var t = (uvs[i] - pivot) * tex;
                uvs[i] = pivot + new Vector2(t.X * c + t.Y * s, -t.X * s + t.Y * c) / tex;
            }
            return;
        }
        // Projection: rotate the axes, then shift so the world point that showed the pivot still does.
        var m = face.Map;
        var p0 = PointWithUv(f, pivot, w, h);
        // A mirrored projection (scales of opposite sign) turns the other way in texel space, so flip the angle to match.
        m.Rotation = NormalizeDegrees(m.Rotation + (MathF.Sign(m.ScaleU) == MathF.Sign(m.ScaleV) ? degrees : -degrees));
        var after = m.Project(p0, w, h);
        m.ShiftU += (pivot.X - after.X) * w;
        m.ShiftV += (pivot.Y - after.Y) * h;
        face.Map = m;
    }

    /// <summary>A world point whose projected UV is <paramref name="uv"/> (projection axes are orthonormal).</summary>
    static Vector3 PointWithUv(FaceRef f, Vector2 uv, int w, int h)
    {
        var m = f.Face.Map;
        var c = f.Mesh.FaceCenter(f.Index);
        var at = m.Project(c, w, h);
        var (u, v) = m.EffectiveAxes();
        return c + u * ((uv.X - at.X) * w * m.ScaleU) + v * ((uv.Y - at.Y) * h * m.ScaleV);
    }

    /// <summary>Corner positions of a face in its own plane frame, in metres (world-anchored, so neighbours line up).</summary>
    public static Vector2[] PlaneCoords(PolyMesh m, int face, Vector3 u, Vector3 v) =>
        m.Faces[face].Verts.Select(i => new Vector2(Vector3.Dot(m.Positions[i], u), Vector3.Dot(m.Positions[i], v))).ToArray();

    /// <summary>Converts a layout in metres to UVs at the given density.</summary>
    public static Vector2[] MetresToUv(Vector2[] metres, int width, int height, float texelsPerMeter = TextureMapping.TexelsPerMeter)
    {
        var k = PerMetre(width, height, texelsPerMeter);
        return metres.Select(p => p * k).ToArray();
    }

    /// <summary>Index of <paramref name="vertex"/> in the face loop, or -1.</summary>
    public static int Corner(Face f, int vertex) => Array.IndexOf(f.Verts, vertex);

    /// <summary>
    /// The edge two faces share, as vertex ids (A, B) in <paramref name="src"/>'s winding, or null when they don't touch.
    /// </summary>
    public static (int A, int B)? SharedEdge(PolyMesh m, int src, int dst)
    {
        var s = m.Faces[src].Verts;
        var d = new HashSet<Edge>(m.Faces[dst].Edges());
        for (int i = 0; i < s.Length; i++)
        {
            int a = s[i], b = s[(i + 1) % s.Length];
            if (a != b && d.Contains(new Edge(a, b))) return (a, b);
        }
        return null;
    }

    static float NonZero(float x) => MathF.Abs(x) < 1e-8f ? (x < 0 ? -1e-8f : 1e-8f) : x;

    static float NormalizeDegrees(float d)
    {
        d %= 360f;
        if (d > 180f) d -= 360f;
        if (d <= -180f) d += 360f;
        return d;
    }
}
