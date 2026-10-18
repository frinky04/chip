using System.Numerics;
using Chip.Core.Geometry;

namespace Chip.Core.Texturing;

/// <summary>Scythe's Fast Texture Tool unwraps (keys 1–5 in the tool).</summary>
public enum UnwrapMode { UseExisting, Conforming, Square, FollowQuads, Planar }

/// <summary>How Follow Quads sizes each new row: its own length, the reference quad's size, or the average of its edge ring.</summary>
public enum QuadSpacing { Length, Even, LengthAverage }

/// <summary>
/// Unwraps write explicit per-corner UVs at the standard texel density (<see cref="TextureMapping.TexelsPerMeter"/>),
/// so one metre of surface covers 512 texels whatever the texture size. Layouts are built in metres, anchored to world
/// position where possible (a flat floor unwraps to exactly its Align to Face projection), then converted per face.
/// </summary>
public static class Unwrap
{
    /// <summary>Unwraps the given faces of one mesh. <paramref name="reference"/> is the face Follow Quads starts from.</summary>
    public static void Apply(PolyMesh mesh, IReadOnlyList<int> faces, UnwrapMode mode, TextureSize size,
        QuadSpacing spacing = QuadSpacing.Length, int reference = -1, float texelsPerMeter = TextureMapping.TexelsPerMeter)
    {
        if (faces.Count == 0) return;
        if (mode == UnwrapMode.UseExisting)
        {
            foreach (var f in faces) UvSpace.Bake(new FaceRef(mesh, f), size);
            return;
        }
        var layout = mode switch
        {
            UnwrapMode.Planar => Planar(mesh, faces),
            UnwrapMode.Conforming => Conforming(mesh, faces, reference),
            UnwrapMode.Square => FollowQuads(mesh, faces, QuadSpacing.Even, reference, square: true),
            _ => FollowQuads(mesh, faces, spacing, reference, square: false),
        };
        foreach (var (f, metres) in layout)
        {
            var (w, h) = size(mesh.Faces[f].Material);
            mesh.Faces[f].UVs = UvSpace.MetresToUv(metres, w, h, texelsPerMeter);
        }
    }

    /// <summary>All faces projected onto one plane (area-weighted average normal), world-anchored.</summary>
    public static Dictionary<int, Vector2[]> Planar(PolyMesh mesh, IReadOnlyList<int> faces)
    {
        var n = Vector3.Zero;
        foreach (var f in faces) n += mesh.FaceNormal(f) * MathF.Max(Topology.FaceArea(mesh, f), 1e-6f);
        if (n.LengthSquared() < 1e-12f) n = mesh.FaceNormal(faces[0]);
        var (u, v) = UvSpace.PlaneFrame(Vector3.Normalize(n));
        return faces.ToDictionary(f => f, f => UvSpace.PlaneCoords(mesh, f, u, v));
    }

    /// <summary>
    /// Angle-preserving unfold: each connected region is flattened face by face across shared edges, so every face keeps
    /// its true shape and size and shared corners land on the same UV. The first face of a region uses its Align to Face
    /// frame. Closed shapes unfold along a spanning tree (seams fall where the tree doesn't cross).
    /// </summary>
    public static Dictionary<int, Vector2[]> Conforming(PolyMesh mesh, IReadOnlyList<int> faces, int reference = -1)
    {
        var result = new Dictionary<int, Vector2[]>();
        var set = faces.ToHashSet();
        var edgeFaces = Topology.EdgeFaces(mesh);
        foreach (var start in Ordered(faces, reference))
        {
            if (result.ContainsKey(start)) continue;
            var (u0, v0) = UvSpace.PlaneFrame(mesh.FaceNormal(start));
            result[start] = UvSpace.PlaneCoords(mesh, start, u0, v0);
            var queue = new Queue<int>([start]);
            while (queue.Count > 0)
            {
                int p = queue.Dequeue();
                foreach (var e in mesh.Faces[p].Edges())
                    foreach (var n in edgeFaces[e])
                    {
                        if (!set.Contains(n) || result.ContainsKey(n)) continue;
                        result[n] = UnfoldOnto(mesh, p, result[p], n, e);
                        queue.Enqueue(n);
                    }
            }
        }
        return result;
    }

    /// <summary>Lays face <paramref name="n"/> flat in its own plane, then places it so edge <paramref name="e"/> matches the parent.</summary>
    static Vector2[] UnfoldOnto(PolyMesh mesh, int parent, Vector2[] parentLayout, int n, Edge e)
    {
        var (u, v) = UvSpace.PlaneFrame(mesh.FaceNormal(n));
        var local = UvSpace.PlaneCoords(mesh, n, u, v);
        var pf = mesh.Faces[parent];
        var nf = mesh.Faces[n];
        var xa = parentLayout[UvSpace.Corner(pf, e.A)];
        var xb = parentLayout[UvSpace.Corner(pf, e.B)];
        var la = local[UvSpace.Corner(nf, e.A)];
        var lb = local[UvSpace.Corner(nf, e.B)];
        // Similarity (rotation + uniform scale, exactly 1 for planar faces) mapping la->xa, lb->xb, as complex numbers.
        var z = Div(xb - xa, lb - la);
        return local.Select(l => xa + Mul(z, l - la)).ToArray();
    }

    /// <summary>
    /// Follow Quads: the reference quad becomes a rectangle aligned with its edges, then every connected quad is laid out
    /// by extending the shared edge straight on, sized by <paramref name="spacing"/>. Square mode makes every quad a
    /// square of the region's mean edge length. Faces that aren't quads unfold conformingly on their own.
    /// </summary>
    public static Dictionary<int, Vector2[]> FollowQuads(PolyMesh mesh, IReadOnlyList<int> faces, QuadSpacing spacing, int reference = -1, bool square = false)
    {
        var result = new Dictionary<int, Vector2[]>();
        var quads = faces.Where(f => mesh.Faces[f].Verts.Length == 4).ToHashSet();
        var others = faces.Where(f => !quads.Contains(f)).ToList();
        if (others.Count > 0)
            foreach (var (f, uv) in Conforming(mesh, others)) result[f] = uv;
        if (quads.Count == 0) return result;

        var edgeFaces = Topology.EdgeFaces(mesh);
        var rings = spacing == QuadSpacing.LengthAverage ? EdgeRings(mesh, quads) : null;
        foreach (var start in Ordered(faces, reference))
        {
            if (!quads.Contains(start) || result.ContainsKey(start)) continue;
            var region = Region(mesh, start, quads, edgeFaces);
            float side = square ? region.SelectMany(f => mesh.Faces[f].Edges()).Average(e => Length(mesh, e)) : 0;
            result[start] = Rectangle(mesh, start, side, rings);
            var queue = new Queue<int>([start]);
            while (queue.Count > 0)
            {
                int p = queue.Dequeue();
                foreach (var e in mesh.Faces[p].Edges())
                    foreach (var n in edgeFaces[e])
                    {
                        if (!quads.Contains(n) || result.ContainsKey(n)) continue;
                        float fac = square ? 1 : Factor(mesh, p, n, e, spacing, rings);
                        result[n] = ExtendQuad(mesh, p, result[p], n, e, fac);
                        queue.Enqueue(n);
                    }
            }
        }
        return result;
    }

    /// <summary>
    /// UVs for quad <paramref name="child"/> continuing quad <paramref name="parent"/> across shared edge
    /// <paramref name="e"/>: the shared corners keep the parent's UVs and the far corners extend straight on by
    /// <paramref name="factor"/> times the parent's depth. Works in any 2D space (metres or UVs).
    /// </summary>
    public static Vector2[] ExtendQuad(PolyMesh mesh, int parent, Vector2[] parentUvs, int child, Edge e, float factor)
    {
        var pf = mesh.Faces[parent];
        var cf = mesh.Faces[child];
        int pa = UvSpace.Corner(pf, e.A), pb = UvSpace.Corner(pf, e.B);
        var ua = parentUvs[pa];
        var ub = parentUvs[pb];
        var ua0 = parentUvs[FarNeighbour(pf, pa, e.B)];
        var ub0 = parentUvs[FarNeighbour(pf, pb, e.A)];
        int ca = UvSpace.Corner(cf, e.A), cb = UvSpace.Corner(cf, e.B);
        var uvs = new Vector2[4];
        uvs[ca] = ua;
        uvs[cb] = ub;
        uvs[FarNeighbour(cf, ca, e.B)] = ua + (ua - ua0) * factor;
        uvs[FarNeighbour(cf, cb, e.A)] = ub + (ub - ub0) * factor;
        return uvs;
    }

    /// <summary>Depth of the child row relative to its parent's, per spacing mode.</summary>
    public static float Factor(PolyMesh mesh, int parent, int child, Edge e, QuadSpacing spacing, Dictionary<Edge, float>? rings = null)
    {
        if (spacing == QuadSpacing.Even) return 1;
        var (p1, p2) = SideEdges(mesh, parent, e);
        var (c1, c2) = SideEdges(mesh, child, e);
        float parentDepth, childDepth;
        if (spacing == QuadSpacing.LengthAverage && rings != null)
        {
            parentDepth = rings.GetValueOrDefault(p1, Length(mesh, p1));
            childDepth = rings.GetValueOrDefault(c1, Length(mesh, c1));
        }
        else
        {
            parentDepth = (Length(mesh, p1) + Length(mesh, p2)) * 0.5f;
            childDepth = (Length(mesh, c1) + Length(mesh, c2)) * 0.5f;
        }
        return parentDepth > 1e-6f ? childDepth / parentDepth : 1;
    }

    /// <summary>For every quad edge, the mean length of its edge ring (edges linked through opposite sides of quads).</summary>
    public static Dictionary<Edge, float> EdgeRings(PolyMesh mesh, IEnumerable<int> quads)
    {
        var parent = new Dictionary<Edge, Edge>();
        Edge Find(Edge x)
        {
            while (parent[x] != x) x = parent[x] = parent[parent[x]];
            return x;
        }
        void Union(Edge a, Edge b)
        {
            parent.TryAdd(a, a);
            parent.TryAdd(b, b);
            parent[Find(a)] = Find(b);
        }
        foreach (var q in quads)
        {
            var e = mesh.Faces[q].Edges().ToArray();
            Union(e[0], e[2]);
            Union(e[1], e[3]);
        }
        var sums = new Dictionary<Edge, (float Sum, int Count)>();
        foreach (var e in parent.Keys)
        {
            var r = Find(e);
            var s = sums.GetValueOrDefault(r);
            sums[r] = (s.Sum + Length(mesh, e), s.Count + 1);
        }
        return parent.Keys.ToDictionary(e => e, e => sums[Find(e)] is var s ? s.Sum / s.Count : 0);
    }

    /// <summary>
    /// The reference quad as a rectangle: width/height are the means of opposite edges, their ring averages when given
    /// (so Length Average rows match the reference), or <paramref name="side"/> for squares.
    /// </summary>
    static Vector2[] Rectangle(PolyMesh mesh, int face, float side, Dictionary<Edge, float>? rings)
    {
        var verts = mesh.Faces[face].Verts;
        var (u, v) = UvSpace.PlaneFrame(mesh.FaceNormal(face));
        var pts = verts.Select(i => mesh.Positions[i]).ToArray();
        // Start at the edge that runs most along the texture's U axis.
        int k = Enumerable.Range(0, 4).MaxBy(i =>
        {
            var d = pts[(i + 1) % 4] - pts[i];
            return MathF.Abs(Vector3.Dot(d, u)) / MathF.Max(d.Length(), 1e-9f);
        });
        var e0 = pts[(k + 1) % 4] - pts[k];
        var e1 = pts[(k + 2) % 4] - pts[(k + 1) % 4];
        float w = (e0.Length() + (pts[(k + 3) % 4] - pts[(k + 2) % 4]).Length()) * 0.5f;
        float h = (e1.Length() + (pts[k] - pts[(k + 3) % 4]).Length()) * 0.5f;
        if (rings != null)
        {
            w = rings.GetValueOrDefault(new Edge(verts[k], verts[(k + 1) % 4]), w);
            h = rings.GetValueOrDefault(new Edge(verts[(k + 1) % 4], verts[(k + 2) % 4]), h);
        }
        if (side > 0) w = h = side;
        float su = Vector3.Dot(e0, u) >= 0 ? 1 : -1;
        float sv = Vector3.Dot(e1, v) >= 0 ? 1 : -1;
        var origin = new Vector2(Vector3.Dot(pts[k], u), Vector3.Dot(pts[k], v));
        var result = new Vector2[4];
        result[k] = origin;
        result[(k + 1) % 4] = origin + new Vector2(su * w, 0);
        result[(k + 2) % 4] = origin + new Vector2(su * w, sv * h);
        result[(k + 3) % 4] = origin + new Vector2(0, sv * h);
        return result;
    }

    /// <summary>The two edges of a quad that touch shared edge <paramref name="e"/> (its "sides" along a strip).</summary>
    public static (Edge, Edge) SideEdges(PolyMesh mesh, int quad, Edge e)
    {
        var f = mesh.Faces[quad];
        int ca = UvSpace.Corner(f, e.A), cb = UvSpace.Corner(f, e.B);
        return (new Edge(e.A, f.Verts[FarNeighbour(f, ca, e.B)]), new Edge(e.B, f.Verts[FarNeighbour(f, cb, e.A)]));
    }

    /// <summary>Corner index next to <paramref name="corner"/> in the loop that isn't vertex <paramref name="notVertex"/>.</summary>
    internal static int FarNeighbour(Face f, int corner, int notVertex)
    {
        int n = f.Verts.Length;
        int next = (corner + 1) % n, prev = (corner + n - 1) % n;
        return f.Verts[next] == notVertex ? prev : next;
    }

    static HashSet<int> Region(PolyMesh mesh, int start, HashSet<int> allowed, Dictionary<Edge, List<int>> edgeFaces)
    {
        var region = new HashSet<int> { start };
        var stack = new Stack<int>([start]);
        while (stack.Count > 0)
            foreach (var e in mesh.Faces[stack.Pop()].Edges())
                foreach (var n in edgeFaces[e])
                    if (allowed.Contains(n) && region.Add(n)) stack.Push(n);
        return region;
    }

    static IEnumerable<int> Ordered(IReadOnlyList<int> faces, int reference) =>
        faces.Contains(reference) ? faces.Where(f => f != reference).Prepend(reference) : faces;

    static float Length(PolyMesh m, Edge e) => Vector3.Distance(m.Positions[e.A], m.Positions[e.B]);

    static Vector2 Mul(Vector2 a, Vector2 b) => new(a.X * b.X - a.Y * b.Y, a.X * b.Y + a.Y * b.X);

    static Vector2 Div(Vector2 a, Vector2 b)
    {
        float d = b.LengthSquared();
        return d < 1e-12f ? Vector2.UnitX : new Vector2(a.X * b.X + a.Y * b.Y, a.Y * b.X - a.X * b.Y) / d;
    }
}
