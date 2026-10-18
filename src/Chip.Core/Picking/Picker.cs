using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Scene;

namespace Chip.Core.Picking;

public readonly record struct Ray(Vector3 Origin, Vector3 Dir)
{
    public Vector3 At(float t) => Origin + Dir * t;
}

/// <summary>A camera as picking sees it: view-projection, viewport size in pixels (origin top-left), eye position.</summary>
public readonly record struct ViewInfo(Matrix4x4 ViewProj, Vector2 Size, Vector3 Eye)
{
    /// <summary>Parallel projection (2D views): clip w doesn't depend on position, so every pixel's ray shares one direction.</summary>
    public bool IsOrthographic => ViewProj.M14 == 0 && ViewProj.M24 == 0 && ViewProj.M34 == 0;

    public bool Project(Vector3 p, out Vector2 screen)
    {
        var c = Vector4.Transform(new Vector4(p, 1), ViewProj);
        screen = default;
        if (c.W <= 1e-5f) return false;
        screen = new Vector2((c.X / c.W * 0.5f + 0.5f) * Size.X, (1 - (c.Y / c.W * 0.5f + 0.5f)) * Size.Y);
        return true;
    }

    public Ray ScreenRay(Vector2 px)
    {
        Matrix4x4.Invert(ViewProj, out var inv);
        float x = px.X / Size.X * 2 - 1, y = 1 - px.Y / Size.Y * 2;
        // Orthographic: start on the near plane (GL clip z = -1) so geometry in front of the view centre is hit.
        var near = Vector4.Transform(new Vector4(x, y, IsOrthographic ? -1 : 0, 1), inv);
        var far = Vector4.Transform(new Vector4(x, y, 1, 1), inv);
        var a = new Vector3(near.X, near.Y, near.Z) / near.W;
        var b = new Vector3(far.X, far.Y, far.Z) / far.W;
        // Orthographic rays start at the pixel on the near plane; perspective rays all start at the eye.
        return new Ray(IsOrthographic ? a : Eye, Vector3.Normalize(b - a));
    }
}

public readonly record struct FaceHit(PolyMesh Mesh, int Face, float Distance, Vector3 Point, Vector3 Normal);

/// <summary>
/// Hit-testing for every selection mode. Hidden meshes are ignored, locked meshes can't be selected; back faces are ignored unless
/// <c>backfaces</c> is set (Scythe-style backface selection, e.g. picking a room's inside from outside).
/// In object mode, entities are picked by their box or glyph unless a surface is in front of them.
/// </summary>
public static class Picker
{
    const float VertexRadius = 10f, EdgeRadius = 7f;
    /// <summary>Radius of an entity's screen glyph (logical pixels), drawn at the centre of its box.</summary>
    public const float EntityGlyphRadius = 11f;

    /// <param name="selectable">Skip locked meshes (selection); placement and camera queries pass false.</param>
    public static FaceHit? RaycastFace(Document doc, Ray ray, bool backfaces = false, bool selectable = false)
    {
        FaceHit? best = null;
        var tris = new List<int>();
        foreach (var mesh in doc.Meshes)
        {
            if (mesh.Hidden || (selectable && mesh.Locked)) continue;
            for (int f = 0; f < mesh.Faces.Count; f++)
            {
                var n = mesh.FaceNormal(f);
                if (!backfaces && Vector3.Dot(n, ray.Dir) >= 0) continue;
                tris.Clear();
                Triangulator.Triangulate(mesh, f, tris);
                var verts = mesh.Faces[f].Verts;
                for (int i = 0; i < tris.Count; i += 3)
                {
                    if (!RayTriangle(ray, mesh.Positions[verts[tris[i]]], mesh.Positions[verts[tris[i + 1]]], mesh.Positions[verts[tris[i + 2]]], out float t)) continue;
                    if (best is { } b && b.Distance <= t) continue;
                    best = new FaceHit(mesh, f, t, ray.At(t), n);
                }
            }
        }
        return best;
    }

    /// <param name="pixelScale">Display scale; pick radii are in logical pixels.</param>
    public static ElementRef? Pick(Document doc, ViewInfo view, Vector2 mouse, SelectMode mode, float pixelScale = 1, bool backfaces = false)
    {
        var ray = view.ScreenRay(mouse);
        var hit = RaycastFace(doc, ray, backfaces, selectable: true);
        switch (mode)
        {
            case SelectMode.Object:
                if (PickEntity(doc, view, mouse, pixelScale, hit?.Distance) is { } ent) return ElementRef.Object(ent.Id);
                return hit is { } h ? ElementRef.Object(h.Mesh.Id) : null;
            case SelectMode.Face: return hit is { } hf ? ElementRef.Face(hf.Mesh.Id, hf.Face) : null;
        }

        // Elements at or in front of the first surface count; a small slack keeps silhouette elements pickable.
        float limit = hit is { } hh ? hh.Distance * 1.02f + 0.01f : float.MaxValue;
        ElementRef? best = null;
        float bestPx = float.MaxValue;
        foreach (var mesh in doc.Meshes)
        {
            if (mesh.Hidden || mesh.Locked) continue;
            if (mode == SelectMode.Vertex)
            {
                for (int v = 0; v < mesh.Positions.Count; v++)
                {
                    var p = mesh.Positions[v];
                    if (!view.Project(p, out var s) || Vector3.Distance(p, view.Eye) > limit) continue;
                    float d = Vector2.Distance(s, mouse);
                    if (d < VertexRadius * pixelScale && d < bestPx) { bestPx = d; best = ElementRef.Vertex(mesh.Id, v); }
                }
            }
            else
            {
                foreach (var e in mesh.Edges())
                {
                    var pa = mesh.Positions[e.A];
                    var pb = mesh.Positions[e.B];
                    if (!view.Project(pa, out var sa) || !view.Project(pb, out var sb)) continue;
                    float d = DistanceToSegment(mouse, sa, sb, out float t);
                    if (d >= EdgeRadius * pixelScale || d >= bestPx) continue;
                    if (Vector3.Distance(Vector3.Lerp(pa, pb, t), view.Eye) > limit) continue;
                    bestPx = d;
                    best = ElementRef.EdgeOf(mesh.Id, e);
                }
            }
        }
        return best;
    }

    /// <summary>Elements inside a screen rectangle. Vertices and edges select through; faces must face the camera unless <paramref name="backfaces"/>.</summary>
    public static List<ElementRef> Marquee(Document doc, ViewInfo view, Vector2 a, Vector2 b, SelectMode mode, bool backfaces = false)
    {
        var min = Vector2.Min(a, b);
        var max = Vector2.Max(a, b);
        bool Inside(Vector3 p) => view.Project(p, out var s) && s.X >= min.X && s.Y >= min.Y && s.X <= max.X && s.Y <= max.Y;
        bool FrontFacing(PolyMesh m, int f) => backfaces || Vector3.Dot(m.FaceNormal(f), m.FaceCenter(f) - view.Eye) < 0;

        var result = new List<ElementRef>();
        foreach (var mesh in doc.Meshes)
        {
            if (mesh.Hidden || mesh.Locked) continue;
            switch (mode)
            {
                case SelectMode.Vertex:
                    for (int v = 0; v < mesh.Positions.Count; v++)
                        if (Inside(mesh.Positions[v])) result.Add(ElementRef.Vertex(mesh.Id, v));
                    break;
                case SelectMode.Edge:
                    foreach (var e in mesh.Edges())
                        if (Inside(mesh.Positions[e.A]) && Inside(mesh.Positions[e.B])) result.Add(ElementRef.EdgeOf(mesh.Id, e));
                    break;
                case SelectMode.Face:
                    for (int f = 0; f < mesh.Faces.Count; f++)
                        if (FrontFacing(mesh, f) && Inside(mesh.FaceCenter(f))) result.Add(ElementRef.Face(mesh.Id, f));
                    break;
                case SelectMode.Object:
                    if (Enumerable.Range(0, mesh.Faces.Count).Any(f => Inside(mesh.FaceCenter(f)))) result.Add(ElementRef.Object(mesh.Id));
                    break;
            }
        }
        if (mode == SelectMode.Object)
            foreach (var e in doc.Entities)
                if (!e.Hidden && !e.Locked && Inside(e.Position)) result.Add(ElementRef.Object(e.Id));
        return result;
    }

    /// <summary>
    /// The selectable entity under the mouse: the ray hits its box, or the cursor is on its glyph. The nearest wins, and only
    /// when no surface closer than <paramref name="surfaceDistance"/> (along the ray) hides it.
    /// </summary>
    public static Entity? PickEntity(Document doc, ViewInfo view, Vector2 mouse, float pixelScale = 1, float? surfaceDistance = null)
    {
        var ray = view.ScreenRay(mouse);
        float limit = surfaceDistance is { } s ? s * 1.02f + 0.02f : float.MaxValue;
        Entity? best = null;
        float bestT = float.MaxValue;
        foreach (var e in doc.Entities)
        {
            if (e.Hidden || e.Locked) continue;
            float? t = EntityOps.RayHit(e, ray);
            var center = EntityOps.Center(e);
            if (t == null && view.Project(center, out var sc) && Vector2.Distance(sc, mouse) < EntityGlyphRadius * pixelScale)
                t = MathF.Max(0, Vector3.Dot(center - ray.Origin, ray.Dir));
            if (t is not { } d || d > limit || d >= bestT) continue;
            bestT = d;
            best = e;
        }
        return best;
    }

    public static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b, out float t)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        t = len2 < 1e-6f ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0, 1);
        return Vector2.Distance(p, a + ab * t);
    }

    /// <summary>Möller–Trumbore, single-sided culling handled by the caller.</summary>
    public static bool RayTriangle(Ray r, Vector3 a, Vector3 b, Vector3 c, out float t)
    {
        t = 0;
        var e1 = b - a;
        var e2 = c - a;
        var p = Vector3.Cross(r.Dir, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-9f) return false;
        float inv = 1 / det;
        var s = r.Origin - a;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0 || u > 1) return false;
        var q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(r.Dir, q) * inv;
        if (v < 0 || u + v > 1) return false;
        t = Vector3.Dot(e2, q) * inv;
        return t > 1e-4f;
    }

    public static bool RayPlane(Ray r, Vector3 point, Vector3 normal, out float t)
    {
        t = 0;
        float d = Vector3.Dot(normal, r.Dir);
        if (MathF.Abs(d) < 1e-6f) return false;
        t = Vector3.Dot(point - r.Origin, normal) / d;
        return t > 0;
    }
}
