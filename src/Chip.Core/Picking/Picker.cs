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
    /// <summary>
    /// Hover radii in logical pixels. Generous on purpose: the nearest element that is actually visible wins, so a wide
    /// radius never reaches through a wall.
    /// </summary>
    public const float VertexRadius = 14f, EdgeRadius = 16f;

    /// <summary>Visibility rays per hover at most (nearest candidates first), so dense zoomed-out views stay cheap.</summary>
    public const int MaxVisibilityTests = 32;
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

        // Every vertex/edge within the radius, nearest first; the first one that is actually visible wins.
        float radius = (mode == SelectMode.Vertex ? VertexRadius : EdgeRadius) * pixelScale;
        var candidates = new List<ElementCandidate>();
        for (int mi = 0; mi < doc.Meshes.Count; mi++)
        {
            var mesh = doc.Meshes[mi];
            if (mesh.Hidden || mesh.Locked) continue;
            if (mode == SelectMode.Vertex)
            {
                for (int v = 0; v < mesh.Positions.Count; v++)
                    if (view.Project(mesh.Positions[v], out var s) && Vector2.Distance(s, mouse) is var d && d < radius)
                        candidates.Add(new(d, mi, v, mesh, mesh.Positions[v], v, -1));
            }
            else
            {
                int i = 0;
                foreach (var e in mesh.Edges())
                {
                    if (EdgeUnderMouse(view, mesh.Positions[e.A], mesh.Positions[e.B], mouse, out float d, out var q) && d < radius)
                        candidates.Add(new(d, mi, i, mesh, q, e.A, e.B));
                    i++;
                }
            }
        }
        candidates.Sort(ElementCandidate.Order);
        foreach (var c in candidates.Take(MaxVisibilityTests))
            if (Visible(view, c.Point, r => RaycastFace(doc, r), c.Mesh, c.A, c.B))
                return mode == SelectMode.Vertex ? ElementRef.Vertex(c.Mesh.Id, c.A) : ElementRef.EdgeOf(c.Mesh.Id, new Edge(c.A, c.B));
        return null;
    }

    /// <summary>A vertex or edge near the cursor: screen distance, document order (for ties), and the 3D point nearest the cursor.</summary>
    public readonly record struct ElementCandidate(float Px, int MeshIndex, int Element, PolyMesh Mesh, Vector3 Point, int A, int B)
    {
        public static readonly Comparison<ElementCandidate> Order = (x, y) =>
            x.Px != y.Px ? x.Px.CompareTo(y.Px) : x.MeshIndex != y.MeshIndex ? x.MeshIndex.CompareTo(y.MeshIndex) : x.Element.CompareTo(y.Element);
    }

    /// <summary>
    /// Screen distance from <paramref name="mouse"/> to edge a–b, and the point on the 3D edge under the nearest screen point
    /// (perspective-correct: halfway along on screen is not halfway along in 3D). Edges running behind the camera are clipped
    /// to the part in front of it instead of being skipped. False when none of the edge is in front.
    /// </summary>
    public static bool EdgeUnderMouse(ViewInfo view, Vector3 a, Vector3 b, Vector2 mouse, out float px, out Vector3 point)
    {
        const float MinW = 0.01f; // just in front of the near plane
        px = float.MaxValue;
        point = default;
        float wa = ClipW(view, a), wb = ClipW(view, b);
        if (wa <= MinW && wb <= MinW) return false;
        if (wa < MinW) { a = Vector3.Lerp(a, b, (MinW - wa) / (wb - wa)); wa = MinW; }
        else if (wb < MinW) { b = Vector3.Lerp(b, a, (MinW - wb) / (wa - wb)); wb = MinW; }
        if (!view.Project(a, out var sa) || !view.Project(b, out var sb)) return false;
        px = DistanceToSegment(mouse, sa, sb, out float s);
        float den = (1 - s) * wb + s * wa;
        point = Vector3.Lerp(a, b, den > 1e-12f ? s * wa / den : s);
        return true;
    }

    static float ClipW(ViewInfo view, Vector3 p) => Vector4.Transform(new Vector4(p, 1), view.ViewProj).W;

    /// <summary>
    /// Whether <paramref name="point"/> (on an element of <paramref name="mesh"/> with vertices <paramref name="a"/> and,
    /// for an edge, <paramref name="b"/>) can be seen: nothing in front of it along its own view ray, or what's in front is
    /// a face the element lies on (silhouettes and grazing views). Back faces aren't drawn, so they never hide anything.
    /// </summary>
    public static bool Visible(ViewInfo view, Vector3 point, Func<Ray, FaceHit?> raycast, PolyMesh mesh, int a, int b = -1)
    {
        if (!view.Project(point, out var s)) return false;
        var ray = view.ScreenRay(s);
        float depth = Vector3.Dot(point - ray.Origin, ray.Dir);
        if (raycast(ray) is not { } hit || hit.Distance >= depth - (0.005f + 0.002f * MathF.Abs(depth))) return true;
        if (!ReferenceEquals(hit.Mesh, mesh)) return false;
        var verts = hit.Mesh.Faces[hit.Face].Verts;
        return Array.IndexOf(verts, a) >= 0 && (b < 0 || Array.IndexOf(verts, b) >= 0);
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
