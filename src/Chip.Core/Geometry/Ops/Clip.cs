using System.Numerics;

namespace Chip.Core.Geometry;

/// <summary>Which side of a clip plane survives. Front is the side the plane normal points to.</summary>
public enum ClipKeep { Front, Back, Both }

public static partial class MeshOps
{
    const float PlaneEpsilon = 1e-5f;

    /// <summary>
    /// Splits a mesh with a plane (Hammer clipping tool). Returns the part in front of the plane (the side
    /// <paramref name="normal"/> points to) and the part behind; either is null when empty. If the plane misses the
    /// mesh, the whole mesh comes back as one side. Both parts copy the source's id, name and flags; the caller decides
    /// which keeps the id. Cut faces keep material, mapping and explicit UVs (interpolated). Each closed part gets
    /// world-aligned cap faces over the cut, using <paramref name="capMaterial"/> or the material of the cut face.
    /// Holes in the cross-section (a clipped tube) are bridged so every cap is a simple polygon.
    /// </summary>
    public static (PolyMesh? Front, PolyMesh? Back) Split(PolyMesh m, Vector3 point, Vector3 normal, string? capMaterial = null)
    {
        var n = Vector3.Normalize(normal);
        int count = m.Positions.Count;
        var d = new float[count];
        var side = new int[count];
        bool anyFront = false, anyBack = false;
        for (int i = 0; i < count; i++)
        {
            d[i] = Vector3.Dot(m.Positions[i] - point, n);
            side[i] = d[i] > PlaneEpsilon ? 1 : d[i] < -PlaneEpsilon ? -1 : 0;
            anyFront |= side[i] > 0;
            anyBack |= side[i] < 0;
        }
        if (!anyBack) return (m.Clone(), null);
        if (!anyFront) return (null, m.Clone());

        var positions = new List<Vector3>(m.Positions);
        var cuts = new Dictionary<Edge, int>();
        int Cut(int a, int b)
        {
            var e = new Edge(a, b);
            if (!cuts.TryGetValue(e, out var v))
            {
                v = positions.Count;
                positions.Add(Vector3.Lerp(positions[e.A], positions[e.B], d[e.A] / (d[e.A] - d[e.B])));
                cuts[e] = v;
            }
            return v;
        }

        var front = new List<Face>();
        var back = new List<Face>();
        for (int f = 0; f < m.Faces.Count; f++)
        {
            var face = m.Faces[f];
            var v = face.Verts;
            bool hasFront = v.Any(i => side[i] > 0), hasBack = v.Any(i => side[i] < 0);
            if (!hasFront && !hasBack)
            {
                // Lies on the plane: it bounds whichever solid it faces away from.
                (Vector3.Dot(m.FaceNormal(f), n) < 0 ? front : back).Add(face.Clone());
                continue;
            }
            if (!hasBack) { front.Add(face.Clone()); continue; }
            if (!hasFront) { back.Add(face.Clone()); continue; }

            var fv = new List<int>(); var bv = new List<int>();
            var fuv = new List<Vector2>(); var buv = new List<Vector2>();
            var uvs = face.UVs;
            for (int i = 0; i < v.Length; i++)
            {
                int a = v[i], b = v[(i + 1) % v.Length];
                var uvA = uvs != null ? uvs[i] : default;
                if (side[a] >= 0) { fv.Add(a); fuv.Add(uvA); }
                if (side[a] <= 0) { bv.Add(a); buv.Add(uvA); }
                if (side[a] * side[b] >= 0) continue;
                int x = Cut(a, b);
                var uvX = uvs != null ? Vector2.Lerp(uvA, uvs[(i + 1) % v.Length], d[a] / (d[a] - d[b])) : default;
                fv.Add(x); fuv.Add(uvX);
                bv.Add(x); buv.Add(uvX);
            }
            if (fv.Count >= 3) front.Add(new Face(fv.ToArray(), face.Material, face.Map) { UVs = uvs != null ? fuv.ToArray() : null, Dominant = face.Dominant });
            if (bv.Count >= 3) back.Add(new Face(bv.ToArray(), face.Material, face.Map) { UVs = uvs != null ? buv.ToArray() : null, Dominant = face.Dominant });
        }

        // Soft edges that were cut continue on both halves of the edge.
        var soft = new HashSet<Edge>();
        foreach (var e in m.SoftEdges)
        {
            if (cuts.TryGetValue(e, out var x)) { soft.Add(new Edge(e.A, x)); soft.Add(new Edge(x, e.B)); }
            else soft.Add(e);
        }

        bool OnPlane(int v) => v >= count || side[v] == 0;
        PolyMesh? Half(List<Face> faces, Vector3 capNormal)
        {
            if (faces.Count == 0) return null;
            var h = new PolyMesh
            {
                Id = m.Id, Name = m.Name, Hidden = m.Hidden, Locked = m.Locked, Collision = m.Collision,
                Positions = new List<Vector3>(positions), Faces = faces, SoftEdges = new HashSet<Edge>(soft),
            };
            CapPlanarHoles(h, OnPlane, capNormal, capMaterial);
            RemoveUnusedVertices(h);
            return h;
        }
        return (Half(front, -n), Half(back, n));
    }

    /// <summary>
    /// Closes open borders whose vertices all satisfy <paramref name="onPlane"/> with world-aligned cap faces facing
    /// <paramref name="capNormal"/>. Inner loops (holes) are bridged into the loop around them. Returns the new face indices.
    /// </summary>
    static List<int> CapPlanarHoles(PolyMesh m, Func<int, bool> onPlane, Vector3 capNormal, string? capMaterial)
    {
        var directed = new Dictionary<(int, int), int>();
        for (int f = 0; f < m.Faces.Count; f++)
        {
            var v = m.Faces[f].Verts;
            for (int i = 0; i < v.Length; i++) directed.TryAdd((v[i], v[(i + 1) % v.Length]), f);
        }
        // A cap runs each open border edge a→b backwards (b→a), so the cap winds consistently with its neighbours.
        var next = new Dictionary<int, int>();
        var source = new Dictionary<int, int>();
        foreach (var ((a, b), f) in directed)
        {
            if (directed.ContainsKey((b, a)) || !onPlane(a) || !onPlane(b)) continue;
            next[b] = a;
            source[b] = f;
        }

        var loops = ChainLoops(next);
        if (loops.Count == 0) return new List<int>();

        var basisU = Vector3.Normalize(Vector3.Cross(capNormal, MathF.Abs(capNormal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        var basisV = Vector3.Cross(capNormal, basisU);
        Vector2 P2(int v) => new(Vector3.Dot(m.Positions[v], basisU), Vector3.Dot(m.Positions[v], basisV));
        float SignedArea(List<int> loop)
        {
            float a = 0;
            for (int i = 0; i < loop.Count; i++)
            {
                var p = P2(loop[i]); var q = P2(loop[(i + 1) % loop.Count]);
                a += p.X * q.Y - q.X * p.Y;
            }
            return a * 0.5f;
        }

        var outers = loops.Where(l => SignedArea(l) > 0).ToList();
        var holes = loops.Where(l => SignedArea(l) < 0).OrderBy(SignedArea).ToList();
        foreach (var hole in holes)
        {
            var probe = P2(hole[0]);
            int host = outers.FindIndex(o => PointInPolygon(probe, o.Select(P2).ToList()));
            if (host < 0) continue; // not enclosed: leave the hole open rather than cap it inside out
            var (a, b) = BridgeHole(outers[host], hole, v => m.Positions[v]);
            outers[host] = a;
            outers.Add(b);
        }

        var added = new List<int>();
        foreach (var loop in outers)
        {
            int src = source.TryGetValue(loop[0], out var s) ? s : 0;
            var srcFace = m.Faces[src];
            added.Add(m.Faces.Count);
            m.Faces.Add(new Face(loop.ToArray(), capMaterial ?? srcFace.Material, srcFace.Map));
            AlignToGrid(m, [m.Faces.Count - 1], srcFace.Map);
        }
        return added;
    }

    /// <summary>Follows a successor map into closed loops of three or more vertices; open chains are dropped.</summary>
    static List<List<int>> ChainLoops(Dictionary<int, int> next)
    {
        var loops = new List<List<int>>();
        var used = new HashSet<int>();
        foreach (var start in next.Keys)
        {
            if (used.Contains(start)) continue;
            var loop = new List<int> { start };
            int cur = next[start];
            while (cur != start && next.ContainsKey(cur) && !used.Contains(cur) && loop.Count <= next.Count)
            {
                loop.Add(cur);
                cur = next[cur];
            }
            used.UnionWith(loop);
            if (cur == start && loop.Count >= 3) loops.Add(loop);
        }
        return loops;
    }

    /// <summary>
    /// Joins a hole into the polygon around it with two bridges, giving two simple polygons that share the bridges.
    /// The outer loop winds counter-clockwise and the hole clockwise (seen from the cap's front).
    /// </summary>
    static (List<int> A, List<int> B) BridgeHole(List<int> outer, List<int> hole, Func<int, Vector3> pos)
    {
        int h0 = 0, h1 = hole.Count / 2;
        int Nearest(int h, int exclude) => Enumerable.Range(0, outer.Count).Where(i => i != exclude)
            .MinBy(i => Vector3.DistanceSquared(pos(outer[i]), pos(hole[h])));
        int o0 = Nearest(h0, -1), o1 = Nearest(h1, o0);
        var a = Cycle(outer, o0, o1).Concat(Cycle(hole, h1, h0)).ToList();
        var b = Cycle(outer, o1, o0).Concat(Cycle(hole, h0, h1)).ToList();
        return (a, b);
    }

    static bool PointInPolygon(Vector2 p, IReadOnlyList<Vector2> poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var a = poly[i]; var b = poly[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    /// <summary>Signed volume enclosed by a closed mesh (positive when normals point outward).</summary>
    public static float Volume(PolyMesh m)
    {
        double vol = 0;
        var tris = new List<int>();
        for (int f = 0; f < m.Faces.Count; f++)
        {
            tris.Clear();
            Triangulator.Triangulate(m, f, tris);
            var v = m.Faces[f].Verts;
            for (int i = 0; i < tris.Count; i += 3)
            {
                var a = m.Positions[v[tris[i]]]; var b = m.Positions[v[tris[i + 1]]]; var c = m.Positions[v[tris[i + 2]]];
                vol += Vector3.Dot(a, Vector3.Cross(b, c));
            }
        }
        return (float)(vol / 6);
    }

    /// <summary>
    /// UV at <paramref name="p"/> (a point in the face's plane) by affine interpolation of the face's corners,
    /// using the widest corner triangle. Exact for planar projections; used to give new corners explicit UVs.
    /// </summary>
    internal static Vector2 InterpolateUV(IReadOnlyList<Vector3> corners, IReadOnlyList<Vector2> uvs, Vector3 p)
    {
        int n = corners.Count;
        int i0 = 0, i1 = 1, i2 = 2;
        float best = -1;
        int far = Enumerable.Range(1, n - 1).MaxBy(i => Vector3.DistanceSquared(corners[i], corners[0]));
        for (int k = 1; k < n; k++)
        {
            if (k == far) continue;
            float area = Vector3.Cross(corners[far] - corners[0], corners[k] - corners[0]).LengthSquared();
            if (area > best) { best = area; i1 = far; i2 = k; }
        }
        if (best < 1e-12f) return uvs[0];
        var a = corners[i0]; var e1 = corners[i1] - a; var e2 = corners[i2] - a; var w = p - a;
        float d11 = Vector3.Dot(e1, e1), d12 = Vector3.Dot(e1, e2), d22 = Vector3.Dot(e2, e2);
        float dw1 = Vector3.Dot(w, e1), dw2 = Vector3.Dot(w, e2);
        float den = d11 * d22 - d12 * d12;
        float s = (d22 * dw1 - d12 * dw2) / den, t = (d11 * dw2 - d12 * dw1) / den;
        return uvs[i0] + (uvs[i1] - uvs[i0]) * s + (uvs[i2] - uvs[i0]) * t;
    }
}
