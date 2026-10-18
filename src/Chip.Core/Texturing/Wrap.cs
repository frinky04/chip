using System.Numerics;
using Chip.Core.Geometry;

namespace Chip.Core.Texturing;

/// <summary>
/// Scythe's Wrap Texture: continue one face's texture onto its neighbours as if the surface were folded flat along the
/// shared edge. A projected source gives a projected result (the projection hinged onto the neighbour's plane), so it
/// stays world-anchored; an explicit source gives explicit UVs. Either way shared corners get identical UVs and the
/// material is copied. Flow continues quads along a strip (Follow Quads from an existing face).
/// </summary>
public static class Wrap
{
    /// <summary>Wraps <paramref name="src"/>'s texture onto <paramref name="dst"/>. False when they don't share an edge.</summary>
    public static bool Across(PolyMesh mesh, int src, int dst, TextureSize size)
    {
        if (src == dst || UvSpace.SharedEdge(mesh, src, dst) is not { } edge) return false;
        var s = mesh.Faces[src];
        var d = mesh.Faces[dst];
        var hinge = Hinge(mesh, src, dst, edge.A, edge.B);
        d.Material = s.Material;

        if (!UvSpace.IsExplicit(s))
        {
            d.Map = s.Map.MovedRigidly(hinge);
            d.UVs = null;
            return true;
        }

        // Explicit: fold the neighbour flat into the source's plane, then extend the source's plane-to-UV map onto it.
        Matrix4x4.Invert(hinge, out var unfold);
        var (u, v) = UvSpace.PlaneFrame(mesh.FaceNormal(src));
        var srcPlane = UvSpace.PlaneCoords(mesh, src, u, v);
        var affine = FitAffine(srcPlane, s.UVs!);
        var uvs = new Vector2[d.Verts.Length];
        for (int i = 0; i < uvs.Length; i++)
        {
            var p = Vector3.Transform(mesh.Positions[d.Verts[i]], unfold);
            uvs[i] = affine(new Vector2(Vector3.Dot(p, u), Vector3.Dot(p, v)));
            int sc = UvSpace.Corner(s, d.Verts[i]);
            if (sc >= 0 && (d.Verts[i] == edge.A || d.Verts[i] == edge.B)) uvs[i] = s.UVs![sc]; // exactly seamless
        }
        d.UVs = uvs;
        return true;
    }

    /// <summary>
    /// Wraps from <paramref name="source"/> across every face in <paramref name="targets"/> reachable through shared
    /// edges (breadth first, each face from the neighbour that reached it). Returns the faces wrapped, in order.
    /// </summary>
    public static List<int> ToSelection(PolyMesh mesh, int source, IEnumerable<int> targets, TextureSize size)
    {
        var allowed = targets.ToHashSet();
        allowed.Remove(source);
        var edgeFaces = Topology.EdgeFaces(mesh);
        var done = new List<int>();
        var seen = new HashSet<int> { source };
        var queue = new Queue<int>([source]);
        while (queue.Count > 0)
        {
            int p = queue.Dequeue();
            foreach (var e in mesh.Faces[p].Edges())
                foreach (var n in edgeFaces[e])
                {
                    if (!allowed.Contains(n) || !seen.Add(n)) continue;
                    if (!Across(mesh, p, n, size)) continue;
                    done.Add(n);
                    queue.Enqueue(n);
                }
        }
        return done;
    }

    /// <summary>
    /// Flow: starting at quad <paramref name="src"/> (its current UVs, made explicit) and stepping into quad
    /// <paramref name="next"/>, walks the quad strip straight on until it ends, branches, loops or hits a non-quad, laying
    /// each quad out Follow-Quads style. Returns the strip faces after <paramref name="src"/>, or an empty list.
    /// </summary>
    public static List<int> Flow(PolyMesh mesh, int src, int next, TextureSize size, QuadSpacing spacing = QuadSpacing.Length)
    {
        var strip = Strip(mesh, src, next);
        if (strip.Count == 0) return [];
        UvSpace.Bake(new FaceRef(mesh, src), size);
        var rings = spacing == QuadSpacing.LengthAverage ? Unwrap.EdgeRings(mesh, strip.Select(s => s.Face).Prepend(src)) : null;
        var material = mesh.Faces[src].Material;
        foreach (var (face, entry, parent) in strip)
        {
            float fac = Unwrap.Factor(mesh, parent, face, entry, spacing, rings);
            mesh.Faces[face].UVs = Unwrap.ExtendQuad(mesh, parent, mesh.Faces[parent].UVs!, face, entry, fac);
            mesh.Faces[face].Material = material;
        }
        return strip.Select(s => s.Face).ToList();
    }

    /// <summary>The quad strip entered from <paramref name="src"/> into <paramref name="next"/>, each with its entry edge and predecessor.</summary>
    public static List<(int Face, Edge Entry, int Parent)> Strip(PolyMesh mesh, int src, int next)
    {
        var result = new List<(int, Edge, int)>();
        if (mesh.Faces[src].Verts.Length != 4 || UvSpace.SharedEdge(mesh, src, next) is not { } shared) return result;
        var edgeFaces = Topology.EdgeFaces(mesh);
        var visited = new HashSet<int> { src };
        int parent = src, cur = next;
        var entry = new Edge(shared.A, shared.B);
        while (mesh.Faces[cur].Verts.Length == 4 && visited.Add(cur))
        {
            result.Add((cur, entry, parent));
            var f = mesh.Faces[cur];
            int ca = UvSpace.Corner(f, entry.A), cb = UvSpace.Corner(f, entry.B);
            var opposite = new Edge(f.Verts[Unwrap.FarNeighbour(f, ca, entry.B)], f.Verts[Unwrap.FarNeighbour(f, cb, entry.A)]);
            var onward = edgeFaces[opposite].Where(x => x != cur).ToList();
            if (onward.Count != 1) break;
            parent = cur;
            cur = onward[0];
            entry = opposite;
        }
        return result;
    }

    /// <summary>Rigid rotation about the shared edge that carries <paramref name="src"/>'s plane onto <paramref name="dst"/>'s.</summary>
    public static Matrix4x4 Hinge(PolyMesh mesh, int src, int dst, int a, int b)
    {
        var pa = mesh.Positions[a];
        var axis = mesh.Positions[b] - pa;
        if (axis.LengthSquared() < 1e-12f) return Matrix4x4.Identity;
        axis = Vector3.Normalize(axis);
        var ns = Perp(mesh.FaceNormal(src), axis);
        var nd = Perp(mesh.FaceNormal(dst), axis);
        float angle = MathF.Atan2(Vector3.Dot(Vector3.Cross(ns, nd), axis), Vector3.Dot(ns, nd));
        return Matrix4x4.CreateTranslation(-pa) * Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromAxisAngle(axis, angle)) * Matrix4x4.CreateTranslation(pa);
    }

    static Vector3 Perp(Vector3 n, Vector3 axis)
    {
        var p = n - axis * Vector3.Dot(n, axis);
        return p.LengthSquared() < 1e-12f ? n : Vector3.Normalize(p);
    }

    /// <summary>Least-squares 2D affine map from <paramref name="from"/> to <paramref name="to"/> (exact for a face's own unwrap).</summary>
    public static Func<Vector2, Vector2> FitAffine(Vector2[] from, Vector2[] to)
    {
        // Centre the inputs for conditioning, then solve the 2x2 normal equations per output axis.
        var cf = Vector2.Zero;
        var ct = Vector2.Zero;
        for (int i = 0; i < from.Length; i++) { cf += from[i]; ct += to[i]; }
        cf /= from.Length;
        ct /= from.Length;
        float sxx = 0, sxy = 0, syy = 0;
        var sxt = Vector2.Zero;
        var syt = Vector2.Zero;
        for (int i = 0; i < from.Length; i++)
        {
            var p = from[i] - cf;
            var t = to[i] - ct;
            sxx += p.X * p.X; sxy += p.X * p.Y; syy += p.Y * p.Y;
            sxt += p.X * t; syt += p.Y * t;
        }
        float det = sxx * syy - sxy * sxy;
        if (MathF.Abs(det) < 1e-12f) return p => ct + (p - cf);
        // [sxx sxy; sxy syy] [cx; cy] = [sxt; syt], solved for both output components at once.
        var colX = (sxt * syy - syt * sxy) / det;
        var colY = (syt * sxx - sxt * sxy) / det;
        return p =>
        {
            var q = p - cf;
            return ct + colX * q.X + colY * q.Y;
        };
    }
}
