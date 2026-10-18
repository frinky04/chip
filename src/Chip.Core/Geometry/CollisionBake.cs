using System.Numerics;

namespace Chip.Core.Geometry;

/// <summary>One mesh's contribution: the faces that collide (already filtered by the exporter: tool rules, collision modes).</summary>
public sealed record CollisionSource(PolyMesh Mesh, IReadOnlyList<int> Faces, bool AllowConvex = true, bool ForceConvex = false);

public sealed record CollisionBakeOptions(float ChunkSize = 32f, float WeldDistance = 1e-3f, float PlaneEpsilon = 1e-4f);

/// <summary>A closed convex piece (for Godot ConvexPolygonShape3D via the -convcolonly suffix): hull vertices + triangles.</summary>
public sealed record ConvexPiece(int SourceMeshId, Vector3[] Points, int[] Triangles);

/// <summary>One spatial chunk of the merged world trimesh (Godot ConcavePolygonShape3D via -colonly).</summary>
public sealed record TriChunk(Vector3[] Positions, int[] Triangles);

/// <summary>
/// Bake counters. <see cref="InputTriangles"/> is every colliding source face triangulated as the render export does
/// (n - 2 per face); <see cref="OutputTriangles"/> is every triangle written, trimesh chunks and convex pieces together.
/// <see cref="MergedFaces"/> counts source faces absorbed into a merged coplanar polygon.
/// </summary>
public sealed record CollisionBakeStats(int InputTriangles, int OutputTriangles, int ConvexPieces, int HiddenFacesRemoved, int MergedFaces, int Chunks);

public sealed record CollisionBakeResult(List<ConvexPiece> Convex, List<TriChunk> Chunks, CollisionBakeStats Stats);

/// <summary>
/// Export-time world physics, compiled from the original (untessellated) polygons the way Source 2 Hammer does it:
/// convex meshes become convex pieces; everything else is welded into one world trimesh with hidden faces removed and
/// coplanar neighbours merged, so characters don't snag on the internal edges between separate meshes.
/// Positions are metres, unscaled; triangles wind counter-clockwise seen from outside, like the source faces.
/// </summary>
public static class CollisionBake
{
    /// <summary>Triangles smaller than this (m²) are dropped as slivers.</summary>
    const double MinTriangleArea = 1e-8;
    /// <summary>Two normals count as parallel (or opposite) within about half a degree.</summary>
    const double ParallelCos = 0.99995;
    /// <summary>Hidden-face lookups bucket polygons on a 3D grid of this size (m); larger polygons go to a shared list.</summary>
    const float HiddenCell = 4f;
    const int MaxHiddenCellsPerPolygon = 512;

    public static CollisionBakeResult Bake(IEnumerable<CollisionSource> sources, CollisionBakeOptions options)
    {
        var convex = new List<ConvexPiece>();
        var welder = new Welder(options.WeldDistance);
        var polys = new List<Poly>();
        int inputTris = 0, convexTris = 0;

        foreach (var src in sources)
        {
            if (src?.Mesh is null || src.Faces is null) continue;
            var mesh = src.Mesh;
            var faces = ValidFaces(mesh, src.Faces);
            foreach (int f in faces) inputTris += mesh.Faces[f].Verts.Length - 2;
            if (faces.Count == 0) continue;

            ConvexPiece? piece = null;
            if (src.ForceConvex) piece = HullPiece(mesh, faces, options);
            else if (src.AllowConvex && faces.Count == mesh.Faces.Count && IsClosed(mesh) && IsConvex(mesh, options.PlaneEpsilon))
                piece = FastPiece(mesh);
            if (piece is not null)
            {
                convex.Add(piece);
                convexTris += piece.Triangles.Length / 3;
                continue;
            }

            // Trimesh: weld each mesh vertex once, then keep the face loops in welded indices.
            var welded = new int[mesh.Positions.Count];
            Array.Fill(welded, -1);
            foreach (int f in faces)
            {
                var verts = mesh.Faces[f].Verts;
                var loop = new List<int>(verts.Length);
                foreach (int v in verts)
                {
                    if (welded[v] < 0) welded[v] = welder.Add(mesh.Positions[v]);
                    int w = welded[v];
                    if (loop.Count == 0 || loop[^1] != w) loop.Add(w);
                }
                while (loop.Count > 1 && loop[0] == loop[^1]) loop.RemoveAt(loop.Count - 1);
                if (loop.Count < 3) continue;
                var poly = Poly.Create(loop.ToArray(), welder.Points);
                if (poly.Area >= MinTriangleArea) polys.Add(poly);
            }
        }

        var pts = welder.Points;
        int hidden = RemoveHidden(polys, pts, options);
        var tris = new List<int>();
        int merged = MergeAndTriangulate(polys, pts, options, tris);
        var chunks = BuildChunks(tris, pts, options.ChunkSize);

        int outTris = convexTris + chunks.Sum(c => c.Triangles.Length / 3);
        return new CollisionBakeResult(convex, chunks, new CollisionBakeStats(inputTris, outTris, convex.Count, hidden, merged, chunks.Count));
    }

    // ---- sources -------------------------------------------------------------------------------------------------

    /// <summary>The requested faces that exist and have at least three finite corners, deduplicated, in request order.</summary>
    static List<int> ValidFaces(PolyMesh mesh, IReadOnlyList<int> requested)
    {
        var result = new List<int>(requested.Count);
        var seen = new HashSet<int>();
        foreach (int f in requested)
        {
            if (f < 0 || f >= mesh.Faces.Count || !seen.Add(f)) continue;
            var verts = mesh.Faces[f].Verts;
            if (verts is null || verts.Length < 3) continue;
            bool ok = true;
            foreach (int v in verts)
                if (v < 0 || v >= mesh.Positions.Count || !float.IsFinite(mesh.Positions[v].X) || !float.IsFinite(mesh.Positions[v].Y) || !float.IsFinite(mesh.Positions[v].Z))
                { ok = false; break; }
            if (ok) result.Add(f);
        }
        return result;
    }

    static bool IsClosed(PolyMesh mesh)
    {
        foreach (var faces in Topology.EdgeFaces(mesh).Values)
            if (faces.Count != 2) return false;
        return mesh.Faces.Count >= 4;
    }

    /// <summary>Every vertex is on or behind every face plane, and the mesh encloses some volume (rules out flat shells).</summary>
    static bool IsConvex(PolyMesh mesh, float eps)
    {
        double volume = 0;
        for (int f = 0; f < mesh.Faces.Count; f++)
        {
            var verts = mesh.Faces[f].Verts;
            var (n, area) = Newell(verts, mesh.Positions);
            if (area < MinTriangleArea) continue; // degenerate faces have no plane to test
            var c = Centroid(verts, mesh.Positions);
            double d = Dot(n, c);
            foreach (var p in mesh.Positions)
                if (Dot(n, p) - d > eps) return false;
            volume += Dot(n, c) * area / 3;
        }
        return volume > MinTriangleArea;
    }

    /// <summary>Fast path: the mesh's own vertices and faces already form a closed convex hull.</summary>
    static ConvexPiece FastPiece(PolyMesh mesh)
    {
        var index = new Dictionary<Vector3, int>();
        var points = new List<Vector3>();
        var map = new int[mesh.Positions.Count];
        Array.Fill(map, -1);
        var tris = new List<int>();
        var loop = new List<int>();
        for (int f = 0; f < mesh.Faces.Count; f++)
        {
            var verts = mesh.Faces[f].Verts;
            foreach (int v in verts)
            {
                if (map[v] >= 0) continue;
                if (!index.TryGetValue(mesh.Positions[v], out int i)) { i = points.Count; points.Add(mesh.Positions[v]); index[mesh.Positions[v]] = i; }
                map[v] = i;
            }
            loop.Clear();
            Triangulator.Triangulate(mesh, f, loop);
            for (int t = 0; t + 2 < loop.Count; t += 3)
            {
                int a = map[verts[loop[t]]], b = map[verts[loop[t + 1]]], c = map[verts[loop[t + 2]]];
                if (a != b && b != c && c != a) tris.AddRange([a, b, c]);
            }
        }
        return new ConvexPiece(mesh.Id, points.ToArray(), tris.ToArray());
    }

    /// <summary>"Convex only": the convex hull of the colliding faces' corners. Flat or degenerate input yields null.</summary>
    static ConvexPiece? HullPiece(PolyMesh mesh, List<int> faces, CollisionBakeOptions options)
    {
        var used = new List<Vector3>();
        var seen = new HashSet<int>();
        foreach (int f in faces)
            foreach (int v in mesh.Faces[f].Verts)
                if (seen.Add(v)) used.Add(mesh.Positions[v]);
        var hull = ConvexHull.Build(used, Math.Max(options.PlaneEpsilon, 1e-7f), options.WeldDistance);
        return hull is { } h ? new ConvexPiece(mesh.Id, h.Points, h.Triangles) : null;
    }

    // ---- hidden faces --------------------------------------------------------------------------------------------

    /// <summary>
    /// Marks every polygon that lies inside a coplanar, opposite-facing polygon (back-to-back faces, a wall bottom on a
    /// floor). Decisions are made against the original set, so identical back-to-back faces both go.
    /// </summary>
    static int RemoveHidden(List<Poly> polys, List<Vector3> pts, CollisionBakeOptions options)
    {
        if (polys.Count < 2) return 0;
        float tol = Math.Max(options.PlaneEpsilon, options.WeldDistance);
        var grid = new Dictionary<(int, int, int), List<int>>();
        var big = new List<int>();
        for (int i = 0; i < polys.Count; i++)
        {
            var (lo, hi) = (Cell3(polys[i].Min, HiddenCell), Cell3(polys[i].Max, HiddenCell));
            long cells = (long)(hi.Item1 - lo.Item1 + 1) * (hi.Item2 - lo.Item2 + 1) * (hi.Item3 - lo.Item3 + 1);
            if (cells > MaxHiddenCellsPerPolygon) { big.Add(i); continue; }
            for (int x = lo.Item1; x <= hi.Item1; x++)
                for (int y = lo.Item2; y <= hi.Item2; y++)
                    for (int z = lo.Item3; z <= hi.Item3; z++)
                    {
                        if (!grid.TryGetValue((x, y, z), out var list)) grid[(x, y, z)] = list = new List<int>();
                        list.Add(i);
                    }
        }

        var hiddenFlags = new bool[polys.Count];
        var empty = new List<int>();
        for (int a = 0; a < polys.Count; a++)
        {
            var A = polys[a];
            // Whatever contains A covers A's centre, so its cell (or the big list) is the only place to look.
            var cell = grid.GetValueOrDefault(Cell3((A.Min + A.Max) * 0.5f, HiddenCell), empty);
            foreach (var list in (ReadOnlySpan<List<int>>)[cell, big])
                foreach (int b in list)
                {
                    if (b == a) continue;
                    var B = polys[b];
                    if (A.Min.X < B.Min.X - tol || A.Min.Y < B.Min.Y - tol || A.Min.Z < B.Min.Z - tol ||
                        A.Max.X > B.Max.X + tol || A.Max.Y > B.Max.Y + tol || A.Max.Z > B.Max.Z + tol) continue;
                    if (Vector3.Dot(A.Normal, B.Normal) > -ParallelCos) continue;
                    if (!OnPlane(A, B, pts, options.PlaneEpsilon)) continue;
                    if (!Inside(A, B, pts, tol)) continue;
                    hiddenFlags[a] = true;
                    goto next;
                }
            next:;
        }

        int removed = 0;
        for (int i = polys.Count - 1; i >= 0; i--)
            if (hiddenFlags[i]) { polys.RemoveAt(i); removed++; }
        return removed;
    }

    /// <summary>All of <paramref name="a"/>'s corners are within <paramref name="eps"/> of <paramref name="b"/>'s plane.</summary>
    static bool OnPlane(Poly a, Poly b, List<Vector3> pts, float eps)
    {
        foreach (int v in a.Verts)
            if (Math.Abs(Dot(b.Normal, pts[v]) - b.D) > eps) return false;
        return true;
    }

    /// <summary>
    /// Polygon <paramref name="a"/> lies inside (or on the boundary of) polygon <paramref name="b"/>, tested in
    /// <paramref name="b"/>'s plane. Each edge of a is split where it meets b's boundary; every corner and every
    /// sub-segment midpoint must be inside or on b, which handles concave b and edges grazing b's corners.
    /// </summary>
    static bool Inside(Poly a, Poly b, List<Vector3> pts, double tol)
    {
        var basis = new Basis(b.Normal, pts[b.Verts[0]]);
        var pb = basis.Project(b.Verts, pts);
        var pa = basis.Project(a.Verts, pts);
        foreach (var p in pa)
            if (!InOrOn(p, pb, tol)) return false;
        var ts = new List<double>();
        for (int i = 0; i < pa.Length; i++)
        {
            P2 p = pa[i], q = pa[(i + 1) % pa.Length], d = q - p;
            double len2 = d.Dot(d);
            if (len2 < 1e-20) continue;
            ts.Clear();
            ts.Add(0); ts.Add(1);
            for (int j = 0; j < pb.Length; j++)
            {
                P2 r = pb[j], s = pb[(j + 1) % pb.Length];
                double t = (r - p).Dot(d) / len2;
                if (t > 0 && t < 1 && SegmentDistance(r, p, q) <= tol) ts.Add(t);
                var e = s - r;
                double den = d.Cross(e);
                if (Math.Abs(den) < 1e-20) continue;
                double tp = (r - p).Cross(e) / den, ts2 = (r - p).Cross(d) / den;
                if (tp > 0 && tp < 1 && ts2 >= 0 && ts2 <= 1) ts.Add(tp);
            }
            ts.Sort();
            for (int k = 0; k + 1 < ts.Count; k++)
            {
                if (ts[k + 1] - ts[k] < 1e-9) continue;
                double tm = (ts[k] + ts[k + 1]) * 0.5;
                if (!InOrOn(p + d * tm, pb, tol)) return false;
            }
        }
        return true;
    }

    static bool InOrOn(P2 p, P2[] poly, double tol)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            P2 a = poly[i], b = poly[j];
            if (SegmentDistance(p, a, b) <= tol) return true;
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    static double SegmentDistance(P2 p, P2 a, P2 b)
    {
        var ab = b - a;
        double len2 = ab.Dot(ab);
        double t = len2 < 1e-20 ? 0 : Math.Clamp((p - a).Dot(ab) / len2, 0, 1);
        var c = a + ab * t - p;
        return Math.Sqrt(c.Dot(c));
    }

    // ---- coplanar merge + triangulation ----------------------------------------------------------------------------

    /// <summary>
    /// Joins polygons that share a welded edge (used by exactly those two, wound oppositely) and lie in the same plane
    /// facing the same way. A group whose outline is one simple loop is re-triangulated as a single polygon without
    /// collinear corners; anything else (holes, pinches, failed checks) keeps its original faces. Returns the number of
    /// source faces absorbed into merged polygons.
    /// </summary>
    static int MergeAndTriangulate(List<Poly> polys, List<Vector3> pts, CollisionBakeOptions options, List<int> tris)
    {
        int n = polys.Count;
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }

        var directed = new Dictionary<(int, int), int>();
        var undirected = new Dictionary<(int, int), int>();
        for (int i = 0; i < n; i++)
        {
            var v = polys[i].Verts;
            for (int k = 0; k < v.Length; k++)
            {
                int a = v[k], b = v[(k + 1) % v.Length];
                directed[(a, b)] = directed.ContainsKey((a, b)) ? -1 : i; // -1: the same directed edge twice (overlap)
                var key = a < b ? (a, b) : (b, a);
                undirected[key] = undirected.GetValueOrDefault(key) + 1;
            }
        }
        for (int i = 0; i < n; i++)
        {
            var v = polys[i].Verts;
            for (int k = 0; k < v.Length; k++)
            {
                int a = v[k], b = v[(k + 1) % v.Length];
                if (a > b) continue; // visit each shared edge once, from its lower end
                if (undirected[(a, b)] != 2 || directed[(a, b)] != i || !directed.TryGetValue((b, a), out int j) || j < 0 || j == i) continue;
                if (Vector3.Dot(polys[i].Normal, polys[j].Normal) < ParallelCos) continue;
                if (!OnPlane(polys[j], polys[i], pts, options.PlaneEpsilon)) continue;
                int ri = Find(i), rj = Find(j);
                if (ri != rj) parent[Math.Max(ri, rj)] = Math.Min(ri, rj);
            }
        }

        var groups = new Dictionary<int, List<int>>();
        for (int i = 0; i < n; i++)
        {
            int r = Find(i);
            if (!groups.TryGetValue(r, out var g)) groups[r] = g = new List<int>();
            g.Add(i);
        }

        int mergedFaces = 0;
        var loopTris = new List<int>();
        for (int i = 0; i < n; i++)
        {
            int r = Find(i);
            if (r != i) continue; // a group is emitted at its first (lowest) polygon, which is its root
            var group = groups[r];
            if (group.Count > 1 && MergeGroup(group, polys, pts, options, tris))
            {
                mergedFaces += group.Count;
                continue;
            }
            foreach (int p in group)
            {
                var poly = polys[p];
                var basis = new Basis(poly.Normal, pts[poly.Verts[0]]);
                loopTris.Clear();
                EarClip(basis.Project(poly.Verts, pts), poly.Verts, loopTris, fanFallback: true);
                foreach (int t in loopTris) tris.Add(poly.Verts[t]);
            }
        }
        return mergedFaces;
    }

    static bool MergeGroup(List<int> group, List<Poly> polys, List<Vector3> pts, CollisionBakeOptions options, List<int> tris)
    {
        // Outline: directed edges whose reverse isn't in the group. One outgoing edge per vertex, one loop, or give up.
        var edges = new HashSet<(int, int)>();
        foreach (int p in group)
        {
            var v = polys[p].Verts;
            for (int k = 0; k < v.Length; k++)
                if (!edges.Add((v[k], v[(k + 1) % v.Length]))) return false;
        }
        var next = new Dictionary<int, int>();
        int start = -1, boundary = 0;
        foreach (int p in group)
        {
            var v = polys[p].Verts;
            for (int k = 0; k < v.Length; k++)
            {
                int a = v[k], b = v[(k + 1) % v.Length];
                if (edges.Contains((b, a))) continue;
                if (!next.TryAdd(a, b)) return false; // pinched outline
                if (start < 0) start = a;
                boundary++;
            }
        }
        if (start < 0) return false;
        var loop = new List<int>(boundary);
        for (int at = start; ;)
        {
            loop.Add(at);
            if (loop.Count > boundary || !next.TryGetValue(at, out at)) return false;
            if (at == start) break;
        }
        if (loop.Count != boundary) return false; // holes or several islands

        // Plane: the largest face's, and every corner of the group must sit on it.
        var root = polys[group[0]];
        double total = 0;
        foreach (int p in group)
        {
            total += polys[p].Area;
            if (polys[p].Area > root.Area) root = polys[p];
        }
        foreach (int p in group)
            if (!OnPlane(polys[p], root, pts, options.PlaneEpsilon)) return false;

        var basis = new Basis(root.Normal, pts[loop[0]]);
        var ids = loop.ToArray();
        var p2 = basis.Project(ids, pts);
        DropCollinear(ref ids, ref p2, options.PlaneEpsilon);
        if (ids.Length < 3) return false;

        var loopTris = new List<int>();
        if (!EarClip(p2, ids, loopTris, fanFallback: false)) return false;
        double area = 0;
        for (int t = 0; t < loopTris.Count; t += 3)
            area += (p2[loopTris[t + 1]] - p2[loopTris[t]]).Cross(p2[loopTris[t + 2]] - p2[loopTris[t]]) * 0.5;
        if (Math.Abs(area - total) > 1e-4 * total + 1e-6) return false; // self-overlapping outline: keep the faces
        foreach (int t in loopTris) tris.Add(ids[t]);
        return true;
    }

    /// <summary>Removes corners that lie on the straight line between their neighbours (and repeated corners).</summary>
    static void DropCollinear(ref int[] ids, ref P2[] p, double tol)
    {
        var li = ids.ToList();
        var lp = p.ToList();
        bool changed = true;
        while (changed && li.Count >= 3)
        {
            changed = false;
            for (int k = 0; k < li.Count && li.Count >= 3; k++)
            {
                P2 a = lp[(k + lp.Count - 1) % lp.Count], b = lp[k], c = lp[(k + 1) % lp.Count];
                var ac = c - a;
                double t = (b - a).Dot(ac), len2 = ac.Dot(ac);
                bool repeat = (b - a).Dot(b - a) < 1e-20;
                if (repeat || (t > 0 && t < len2 && SegmentDistance(b, a, c) <= tol))
                {
                    li.RemoveAt(k);
                    lp.RemoveAt(k);
                    changed = true;
                    k--;
                }
            }
        }
        ids = li.ToArray();
        p = lp.ToArray();
    }

    /// <summary>
    /// Ear clipping in 2D for a simple loop of either orientation; triangles are loop indices in loop order, so they
    /// keep the loop's winding. Returns false when no ear can be found; with <paramref name="fanFallback"/> the rest is
    /// fanned (what the render triangulator does) instead.
    /// </summary>
    static bool EarClip(P2[] p, int[] ids, List<int> output, bool fanFallback)
    {
        int n = p.Length;
        if (n < 3) return false;
        double signed = 0;
        for (int i = 0; i < n; i++) signed += p[i].Cross(p[(i + 1) % n]);
        double sign = signed < 0 ? -1 : 1;

        var rem = new List<int>(n);
        for (int i = 0; i < n; i++) rem.Add(i);
        while (rem.Count > 3)
        {
            bool clipped = false;
            for (int i = 0; i < rem.Count; i++)
            {
                int a = rem[(i + rem.Count - 1) % rem.Count], b = rem[i], c = rem[(i + 1) % rem.Count];
                if ((p[b] - p[a]).Cross(p[c] - p[a]) * sign <= 1e-14) continue;
                bool blocked = false;
                foreach (int j in rem)
                {
                    if (j == a || j == b || j == c || ids[j] == ids[a] || ids[j] == ids[b] || ids[j] == ids[c]) continue;
                    if (InTriangle(p[j], p[a], p[b], p[c], sign)) { blocked = true; break; }
                }
                if (blocked) continue;
                output.AddRange([a, b, c]);
                rem.RemoveAt(i);
                clipped = true;
                break;
            }
            if (clipped) continue;
            if (!fanFallback) return false;
            for (int i = 1; i + 1 < rem.Count; i++) output.AddRange([rem[0], rem[i], rem[i + 1]]);
            return true;
        }
        output.AddRange([rem[0], rem[1], rem[2]]);
        return true;
    }

    static bool InTriangle(P2 p, P2 a, P2 b, P2 c, double sign) =>
        (b - a).Cross(p - a) * sign >= 0 && (c - b).Cross(p - b) * sign >= 0 && (a - c).Cross(p - c) * sign >= 0;

    // ---- chunks --------------------------------------------------------------------------------------------------

    static List<TriChunk> BuildChunks(List<int> tris, List<Vector3> pts, float chunkSize)
    {
        var byCell = new Dictionary<(int, int), List<int>>();
        for (int t = 0; t + 2 < tris.Count; t += 3)
        {
            int a = tris[t], b = tris[t + 1], c = tris[t + 2];
            if (a == b || b == c || c == a) continue;
            var cross = Vector3.Cross(pts[b] - pts[a], pts[c] - pts[a]);
            if (cross.Length() * 0.5 < MinTriangleArea) continue;
            var centroid = (pts[a] + pts[b] + pts[c]) / 3;
            var cell = chunkSize > 0 && float.IsFinite(chunkSize) ? (FloorCell(centroid.X, chunkSize), FloorCell(centroid.Z, chunkSize)) : (0, 0);
            if (!byCell.TryGetValue(cell, out var list)) byCell[cell] = list = new List<int>();
            list.AddRange([a, b, c]);
        }

        var chunks = new List<TriChunk>(byCell.Count);
        foreach (var cell in byCell.Keys.OrderBy(k => k.Item1).ThenBy(k => k.Item2))
        {
            var list = byCell[cell];
            var local = new Dictionary<int, int>();
            var positions = new List<Vector3>();
            var indices = new int[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                if (!local.TryGetValue(list[i], out int li)) { li = positions.Count; positions.Add(pts[list[i]]); local[list[i]] = li; }
                indices[i] = li;
            }
            chunks.Add(new TriChunk(positions.ToArray(), indices));
        }
        return chunks;
    }

    // ---- small helpers -------------------------------------------------------------------------------------------

    static int FloorCell(float x, float size) => (int)Math.Clamp(Math.Floor(x / (double)size), int.MinValue / 2, int.MaxValue / 2);

    static (int, int, int) Cell3(Vector3 p, float size) => (FloorCell(p.X, size), FloorCell(p.Y, size), FloorCell(p.Z, size));

    static double Dot(Vector3 n, Vector3 p) => (double)n.X * p.X + (double)n.Y * p.Y + (double)n.Z * p.Z;

    static Vector3 Centroid(IReadOnlyList<int> verts, IReadOnlyList<Vector3> pts)
    {
        var c = Vector3.Zero;
        foreach (int v in verts) c += pts[v];
        return c / verts.Count;
    }

    /// <summary>Unit normal and area by Newell's method, in double precision relative to the first corner.</summary>
    static (Vector3 Normal, double Area) Newell(IReadOnlyList<int> verts, IReadOnlyList<Vector3> pts)
    {
        double x = 0, y = 0, z = 0;
        var o = pts[verts[0]];
        for (int i = 0; i < verts.Count; i++)
        {
            var a = pts[verts[i]] - o;
            var b = pts[verts[(i + 1) % verts.Count]] - o;
            x += ((double)a.Y - b.Y) * ((double)a.Z + b.Z);
            y += ((double)a.Z - b.Z) * ((double)a.X + b.X);
            z += ((double)a.X - b.X) * ((double)a.Y + b.Y);
        }
        double len = Math.Sqrt(x * x + y * y + z * z);
        if (len < 1e-30) return (Vector3.UnitY, 0);
        return (new Vector3((float)(x / len), (float)(y / len), (float)(z / len)), len * 0.5);
    }

    /// <summary>A trimesh polygon: welded corner loop with its plane and bounds.</summary>
    sealed class Poly
    {
        public required int[] Verts;
        public Vector3 Normal;
        public double D;
        public double Area;
        public Vector3 Min, Max;

        public static Poly Create(int[] verts, List<Vector3> pts)
        {
            var (n, area) = Newell(verts, pts);
            var c = Centroid(verts, pts);
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (int v in verts) { min = Vector3.Min(min, pts[v]); max = Vector3.Max(max, pts[v]); }
            return new Poly { Verts = verts, Normal = n, D = Dot(n, c), Area = area, Min = min, Max = max };
        }
    }

    /// <summary>Right-handed plane frame: loops counter-clockwise about the normal project counter-clockwise.</summary>
    readonly struct Basis
    {
        readonly Vector3 _origin, _t, _b;

        public Basis(Vector3 normal, Vector3 origin)
        {
            _origin = origin;
            _t = Vector3.Normalize(Vector3.Cross(MathF.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX, normal));
            _b = Vector3.Cross(normal, _t);
        }

        public P2[] Project(int[] verts, List<Vector3> pts)
        {
            var r = new P2[verts.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                var d = pts[verts[i]] - _origin;
                r[i] = new P2((double)d.X * _t.X + (double)d.Y * _t.Y + (double)d.Z * _t.Z, (double)d.X * _b.X + (double)d.Y * _b.Y + (double)d.Z * _b.Z);
            }
            return r;
        }
    }

    readonly record struct P2(double X, double Y)
    {
        public static P2 operator -(P2 a, P2 b) => new(a.X - b.X, a.Y - b.Y);
        public static P2 operator +(P2 a, P2 b) => new(a.X + b.X, a.Y + b.Y);
        public static P2 operator *(P2 a, double s) => new(a.X * s, a.Y * s);
        public double Dot(P2 o) => X * o.X + Y * o.Y;
        public double Cross(P2 o) => X * o.Y - Y * o.X;
    }

    /// <summary>Spatial-hash welder: a point within the weld distance of an earlier point reuses it (lowest index wins).</summary>
    sealed class Welder(float distance)
    {
        public readonly List<Vector3> Points = new();
        readonly Dictionary<(long, long, long), List<int>> _cells = new();
        readonly Dictionary<Vector3, int> _exact = new();

        public int Add(Vector3 p)
        {
            if (!(distance > 0))
            {
                if (_exact.TryGetValue(p, out int e)) return e;
                _exact[p] = Points.Count;
                Points.Add(p);
                return Points.Count - 1;
            }
            long cx = (long)Math.Floor(p.X / (double)distance), cy = (long)Math.Floor(p.Y / (double)distance), cz = (long)Math.Floor(p.Z / (double)distance);
            float d2 = distance * distance;
            int best = -1;
            for (long x = cx - 1; x <= cx + 1; x++)
                for (long y = cy - 1; y <= cy + 1; y++)
                    for (long z = cz - 1; z <= cz + 1; z++)
                        if (_cells.TryGetValue((x, y, z), out var list))
                            foreach (int i in list)
                                if (Vector3.DistanceSquared(Points[i], p) <= d2 && (best < 0 || i < best)) best = i;
            if (best >= 0) return best;
            if (!_cells.TryGetValue((cx, cy, cz), out var cell)) _cells[(cx, cy, cz)] = cell = new List<int>(1);
            cell.Add(Points.Count);
            Points.Add(p);
            return Points.Count - 1;
        }
    }
}

/// <summary>Incremental 3D convex hull for small point sets (convex-only colliders).</summary>
public static class ConvexHull
{
    /// <summary>
    /// Hull of <paramref name="input"/> as compact points and outward counter-clockwise triangles, or null when the points
    /// don't span a volume (fewer than four, collinear or coplanar within <paramref name="eps"/>).
    /// </summary>
    public static (Vector3[] Points, int[] Triangles)? Build(IReadOnlyList<Vector3> input, float eps, float weld = 0) =>
        Build(input, eps, weld, corners: false);

    static (Vector3[] Points, int[] Triangles)? Build(IReadOnlyList<Vector3> input, float eps, float weld, bool corners)
    {
        var pts = new List<Vector3>();
        foreach (var p in input)
        {
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) continue;
            bool dup = false;
            foreach (var q in pts) if (Vector3.DistanceSquared(p, q) <= weld * weld) { dup = true; break; }
            if (!dup) pts.Add(p);
        }
        if (pts.Count < 4) return null;

        var P = pts.Select(p => new V3(p.X, p.Y, p.Z)).ToArray();
        int i0 = 0;
        for (int i = 1; i < P.Length; i++) if (P[i].X < P[i0].X) i0 = i;
        int i1 = Farthest(P, i => (P[i] - P[i0]).Length());
        if ((P[i1] - P[i0]).Length() <= eps) return null;
        var axis = (P[i1] - P[i0]).Normalized();
        int i2 = Farthest(P, i => { var d = P[i] - P[i0]; return (d - axis * d.Dot(axis)).Length(); });
        var n012 = (P[i1] - P[i0]).Cross(P[i2] - P[i0]);
        if (n012.Length() <= eps * (P[i1] - P[i0]).Length()) return null;
        n012 = n012.Normalized();
        int i3 = Farthest(P, i => Math.Abs((P[i] - P[i0]).Dot(n012)));
        if (Math.Abs((P[i3] - P[i0]).Dot(n012)) <= eps) return null;

        var faces = new List<HFace>();
        var centre = (P[i0] + P[i1] + P[i2] + P[i3]) * 0.25;
        void AddFace(int a, int b, int c)
        {
            var f = HFace.Make(a, b, c, P);
            if ((centre - P[a]).Dot(f.N) > 0) f = HFace.Make(a, c, b, P);
            faces.Add(f);
        }
        AddFace(i0, i1, i2); AddFace(i0, i1, i3); AddFace(i0, i2, i3); AddFace(i1, i2, i3);

        var visibleEdges = new HashSet<(int, int)>();
        var horizon = new List<(int, int)>();
        for (int p = 0; p < P.Length; p++)
        {
            if (p == i0 || p == i1 || p == i2 || p == i3) continue;
            visibleEdges.Clear();
            horizon.Clear();
            bool any = false;
            for (int f = 0; f < faces.Count; f++)
            {
                if (!faces[f].Alive || faces[f].Distance(P[p]) <= eps) continue;
                any = true;
                faces[f] = faces[f] with { Alive = false };
                visibleEdges.Add((faces[f].A, faces[f].B)); visibleEdges.Add((faces[f].B, faces[f].C)); visibleEdges.Add((faces[f].C, faces[f].A));
            }
            if (!any) continue;
            for (int f = 0; f < faces.Count; f++)
            {
                if (faces[f].Alive || faces[f].Retired) continue;
                foreach (var e in (ReadOnlySpan<(int, int)>)[(faces[f].A, faces[f].B), (faces[f].B, faces[f].C), (faces[f].C, faces[f].A)])
                    if (!visibleEdges.Contains((e.Item2, e.Item1))) horizon.Add(e);
                faces[f] = faces[f] with { Retired = true };
            }
            foreach (var (a, b) in horizon) faces.Add(HFace.Make(a, b, p, P));
        }

        // Points added early can end up inside a final face or edge (coplanar within eps). Only corners, where at least
        // three distinct face directions meet, are kept; one rebuild from the corners gives the minimal hull.
        if (!corners)
        {
            var normals = new Dictionary<int, List<V3>>();
            foreach (var f in faces)
            {
                if (!f.Alive) continue;
                foreach (int v in (ReadOnlySpan<int>)[f.A, f.B, f.C])
                {
                    if (!normals.TryGetValue(v, out var list)) normals[v] = list = new List<V3>(4);
                    if (!list.Exists(n => n.Dot(f.N) > 0.99999)) list.Add(f.N);
                }
            }
            var keep = normals.Keys.Where(v => normals[v].Count >= 3).Order().ToList();
            if (keep.Count < normals.Count && keep.Count >= 4)
                return Build(keep.Select(v => pts[v]).ToList(), eps, weld, corners: true);
        }

        var map = new Dictionary<int, int>();
        var outPts = new List<Vector3>();
        var tris = new List<int>();
        foreach (var f in faces)
        {
            if (!f.Alive) continue;
            foreach (int v in (ReadOnlySpan<int>)[f.A, f.B, f.C])
            {
                if (!map.TryGetValue(v, out int m)) { m = outPts.Count; outPts.Add(pts[v]); map[v] = m; }
                tris.Add(m);
            }
        }
        return (outPts.ToArray(), tris.ToArray());
    }

    static int Farthest(V3[] p, Func<int, double> metric)
    {
        int best = 0;
        double bestValue = double.MinValue;
        for (int i = 0; i < p.Length; i++)
        {
            double m = metric(i);
            if (m > bestValue) { bestValue = m; best = i; }
        }
        return best;
    }

    readonly record struct HFace(int A, int B, int C, V3 N, double D, bool Alive, bool Retired)
    {
        public static HFace Make(int a, int b, int c, V3[] p)
        {
            var n = (p[b] - p[a]).Cross(p[c] - p[a]);
            double len = n.Length();
            n = len > 1e-30 ? n * (1 / len) : default;
            return new HFace(a, b, c, n, n.Dot(p[a]), true, false);
        }

        public double Distance(V3 p) => N.Dot(p) - D;
    }

    readonly record struct V3(double X, double Y, double Z)
    {
        public static V3 operator -(V3 a, V3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static V3 operator +(V3 a, V3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static V3 operator *(V3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
        public double Dot(V3 o) => X * o.X + Y * o.Y + Z * o.Z;
        public V3 Cross(V3 o) => new(Y * o.Z - Z * o.Y, Z * o.X - X * o.Z, X * o.Y - Y * o.X);
        public double Length() => Math.Sqrt(Dot(this));
        public V3 Normalized() => this * (1 / Length());
    }
}
