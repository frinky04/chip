using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Picking;
using Chip.Core.Scene;

namespace Chip.Editor.Viewport;

/// <summary>
/// Scythe drag alignment: the vertex, edge midpoint or face centre nearest the cursor, on the surface under it
/// (or any vertex within a few pixels of it, so silhouette corners work too).
/// </summary>
public static class DragAlign
{
    const float VertexRadiusPx = 14f;

    /// <param name="exclude">Vertices that are moving (per mesh). Faces touching them are ignored so the selection can't snap onto itself.</param>
    public static Vector3? Find(Document doc, ViewInfo view, Vector2 mouse, IReadOnlyDictionary<PolyMesh, HashSet<int>>? exclude, float pixelScale = 1)
    {
        var ray = view.ScreenRay(mouse);
        bool Moving(PolyMesh m, int v) => exclude != null && exclude.TryGetValue(m, out var set) && set.Contains(v);

        // Nearest surface that isn't part of the moving selection.
        PolyMesh? hitMesh = null;
        int hitFace = -1;
        float best = float.MaxValue;
        var tris = new List<int>();
        foreach (var mesh in doc.Meshes)
        {
            if (mesh.Hidden) continue;
            for (int f = 0; f < mesh.Faces.Count; f++)
            {
                var verts = mesh.Faces[f].Verts;
                if (verts.Any(v => Moving(mesh, v))) continue;
                tris.Clear();
                Triangulator.Triangulate(mesh, f, tris);
                for (int i = 0; i < tris.Count; i += 3)
                    if (Picker.RayTriangle(ray, mesh.Positions[verts[tris[i]]], mesh.Positions[verts[tris[i + 1]]], mesh.Positions[verts[tris[i + 2]]], out float t) && t < best)
                    {
                        best = t;
                        hitMesh = mesh;
                        hitFace = f;
                    }
            }
        }

        Vector3? result = null;
        float bestPx = float.MaxValue;
        void Consider(Vector3 p, float limitPx)
        {
            if (!view.Project(p, out var s)) return;
            float d = Vector2.Distance(s, mouse);
            if (d < limitPx && d < bestPx) { bestPx = d; result = p; }
        }

        if (hitMesh != null) foreach (var p in Candidates(hitMesh, hitFace)) Consider(p, float.MaxValue);

        // Loose vertices near the cursor (corners seen edge-on, open meshes) win if they are closer on screen.
        float radius = VertexRadiusPx * pixelScale;
        float depthLimit = hitMesh != null ? best * 1.02f + 0.05f : float.MaxValue;
        foreach (var mesh in doc.Meshes)
        {
            if (mesh.Hidden) continue;
            for (int v = 0; v < mesh.Positions.Count; v++)
            {
                if (Moving(mesh, v)) continue;
                var p = mesh.Positions[v];
                if (Vector3.Dot(p - ray.Origin, ray.Dir) > depthLimit) continue; // behind the surface under the cursor
                Consider(p, radius);
            }
        }
        return result;
    }

    /// <summary>A face's snap points: its corners, edge midpoints and centre.</summary>
    public static IEnumerable<Vector3> Candidates(PolyMesh mesh, int face)
    {
        var verts = mesh.Faces[face].Verts;
        for (int i = 0; i < verts.Length; i++)
        {
            var a = mesh.Positions[verts[i]];
            var b = mesh.Positions[verts[(i + 1) % verts.Length]];
            yield return a;
            yield return (a + b) * 0.5f;
        }
        yield return mesh.FaceCenter(face);
    }
}
