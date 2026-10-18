using System.Numerics;

namespace Chip.Core.Geometry;

/// <summary>Ear-clipping triangulation of a face loop. Output preserves the face winding.</summary>
public static class Triangulator
{
    /// <summary>Appends triangles as indices into the face's loop (0..n-1).</summary>
    public static void Triangulate(PolyMesh mesh, int face, List<int> loopIndicesOut)
    {
        var verts = mesh.Faces[face].Verts;
        int n = verts.Length;
        if (n < 3) return;
        if (n == 3) { loopIndicesOut.AddRange([0, 1, 2]); return; }

        var normal = mesh.FaceNormal(face);
        var t = Vector3.Normalize(Vector3.Cross(MathF.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX, normal));
        var b = Vector3.Cross(normal, t);
        var pts = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            var p = mesh.Positions[verts[i]];
            pts[i] = new Vector2(Vector3.Dot(p, t), Vector3.Dot(p, b));
        }

        var remaining = Enumerable.Range(0, n).ToList();
        int guard = 0;
        while (remaining.Count > 3 && guard++ < n * n)
        {
            bool clipped = false;
            for (int i = 0; i < remaining.Count; i++)
            {
                int ia = remaining[(i + remaining.Count - 1) % remaining.Count];
                int ib = remaining[i];
                int ic = remaining[(i + 1) % remaining.Count];
                if (Cross(pts[ia], pts[ib], pts[ic]) <= 1e-9f) continue;
                bool contains = false;
                foreach (var j in remaining)
                {
                    if (j == ia || j == ib || j == ic) continue;
                    if (InTriangle(pts[j], pts[ia], pts[ib], pts[ic])) { contains = true; break; }
                }
                if (contains) continue;
                loopIndicesOut.AddRange([ia, ib, ic]);
                remaining.RemoveAt(i);
                clipped = true;
                break;
            }
            if (!clipped) break; // degenerate loop: fan the rest
        }
        for (int i = 1; i + 1 < remaining.Count; i++)
            loopIndicesOut.AddRange([remaining[0], remaining[i], remaining[i + 1]]);
    }

    static float Cross(Vector2 a, Vector2 b, Vector2 c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c) =>
        Cross(a, b, p) >= 0 && Cross(b, c, p) >= 0 && Cross(c, a, p) >= 0;
}
