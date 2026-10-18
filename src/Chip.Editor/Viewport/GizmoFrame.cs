using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Scene;

namespace Chip.Editor.Viewport;

/// <summary>Computes the gizmo's axes for each <see cref="GizmoOrientation"/>.</summary>
public static class GizmoFrame
{
    public static (Vector3 X, Vector3 Y, Vector3 Z) For(GizmoOrientation o, Document doc, Selection sel, Camera cam) => o switch
    {
        GizmoOrientation.Local => Local(doc, sel) ?? World,
        GizmoOrientation.View => (cam.Right, cam.Up, -cam.Forward),
        _ => World,
    };

    public static (Vector3 X, Vector3 Y, Vector3 Z) World => (Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);

    /// <summary>
    /// The selection's dominant face: area-weighted average normal of the faces the selection touches, or the largest face
    /// when they cancel out (a closed box). That normal replaces the world axis it is closest to; the face's longest edge
    /// gives a second axis, so a rotated block gets a frame that is its own rotation.
    /// </summary>
    public static (Vector3 X, Vector3 Y, Vector3 Z)? Local(Document doc, Selection sel)
    {
        var faces = Faces(doc, sel);
        if (faces.Count == 0) return null;
        var sum = Vector3.Zero;
        float total = 0;
        (PolyMesh Mesh, int Face, float Area) largest = default;
        foreach (var (m, f) in faces)
        {
            float area = Topology.FaceArea(m, f);
            sum += m.FaceNormal(f) * area;
            total += area;
            if (area > largest.Area) largest = (m, f, area);
        }
        if (largest.Mesh == null) return null;
        var n = sum.Length() > total * 0.3f ? Vector3.Normalize(sum) : largest.Mesh.FaceNormal(largest.Face);

        // Longest edge of the face that best represents the normal.
        var (fm, ff, _) = faces.Select(x => (x.Mesh, x.Face, Score: Vector3.Dot(x.Mesh.FaceNormal(x.Face), n) * Topology.FaceArea(x.Mesh, x.Face)))
            .MaxBy(x => x.Score);
        var tangent = Vector3.Zero;
        float bestLen = 0;
        var verts = fm.Faces[ff].Verts;
        for (int i = 0; i < verts.Length; i++)
        {
            var e = fm.Positions[verts[(i + 1) % verts.Length]] - fm.Positions[verts[i]];
            e -= n * Vector3.Dot(e, n);
            if (e.Length() > bestLen) { bestLen = e.Length(); tangent = e; }
        }
        return FromNormal(n, bestLen > 1e-5f ? Vector3.Normalize(tangent) : null);
    }

    /// <summary>Builds a right-handed frame where <paramref name="n"/> takes the place of its closest world axis.</summary>
    public static (Vector3 X, Vector3 Y, Vector3 Z) FromNormal(Vector3 n, Vector3? tangent)
    {
        Vector3[] world = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
        int k = Closest(n, world, -1);
        if (Vector3.Dot(n, world[k]) < 0) n = -n;
        var t = tangent ?? Vector3.Normalize(Vector3.Cross(n, world[(k + 1) % 3]));
        int j = Closest(t, world, k);
        if (Vector3.Dot(t, world[j]) < 0) t = -t;
        var axes = new Vector3[3];
        axes[k] = n;
        axes[j] = Vector3.Normalize(t - n * Vector3.Dot(t, n));
        int third = 3 - k - j;
        // Cyclic order keeps the frame right-handed: X = Y×Z, Y = Z×X, Z = X×Y.
        axes[third] = Vector3.Normalize(Vector3.Cross(axes[(third + 1) % 3], axes[(third + 2) % 3]));
        return (axes[0], axes[1], axes[2]);
    }

    static int Closest(Vector3 v, Vector3[] axes, int skip)
    {
        int best = -1;
        float bestDot = -1;
        for (int i = 0; i < 3; i++)
        {
            if (i == skip) continue;
            float d = MathF.Abs(Vector3.Dot(v, axes[i]));
            if (d > bestDot) { bestDot = d; best = i; }
        }
        return best;
    }

    /// <summary>Faces the selection touches: selected faces, all faces of selected objects, or faces around selected vertices/edges.</summary>
    static List<(PolyMesh Mesh, int Face)> Faces(Document doc, Selection sel)
    {
        var result = new List<(PolyMesh, int)>();
        if (sel.Mode is SelectMode.Face or SelectMode.Object)
        {
            foreach (var (m, faces) in sel.FaceSets(doc))
                foreach (var f in faces) result.Add((m, f));
            return result;
        }
        foreach (var (m, verts) in sel.VertexSets(doc))
        {
            var touching = new List<int>();
            var full = new List<int>();
            for (int f = 0; f < m.Faces.Count; f++)
            {
                var fv = m.Faces[f].Verts;
                int count = fv.Count(verts.Contains);
                if (count == fv.Length) full.Add(f);
                else if (count > 0) touching.Add(f);
            }
            foreach (var f in full.Count > 0 ? full : touching) result.Add((m, f));
        }
        return result;
    }
}
