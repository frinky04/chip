using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Core.IO;

/// <summary>What an import produced: new meshes and entities (ids unassigned) plus notes for the user.</summary>
public sealed record ImportResult(List<PolyMesh> Meshes, List<Entity> Entities, List<string> Warnings)
{
    /// <summary>
    /// The file's own base colour images by the material name its faces use (glTF: embedded, data-URI or beside the file).
    /// Only materials with a PNG or JPEG image are listed.
    /// </summary>
    public Dictionary<string, ImportedImage> Images { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>An image read from an imported file: its bytes and extension (<c>.png</c> or <c>.jpg</c>).</summary>
public sealed record ImportedImage(byte[] Data, string Extension);

/// <summary>
/// Turns triangle soups (or n-gons) from a file into an editable <see cref="PolyMesh"/>: welds coincident positions,
/// drops degenerate polygons, then merges edge-connected polygons that share a material, a plane and UVs at their
/// shared corners back into n-gons. A merge is only taken when the result stays a simple loop (no holes or pinches),
/// so a ring of quads keeps one seam. Vertices left on a straight border and used by no other face are dropped.
/// Explicit UVs become <see cref="Face.UVs"/>; corner normals that agree across a crease mark it as a soft edge.
/// </summary>
public sealed class ImportBuilder
{
    const float WeldQuantum = 1e-5f;
    const float NormalTolerance = 0.9999f;   // cos of ~0.8 degrees
    const float UvTolerance = 1e-4f;

    sealed class Poly
    {
        public int[] Verts = [];
        public string Material = "";
        public Vector2[]? UVs;
        public Vector3[]? Normals;
        public Vector3 Plane;
        public float Offset;
    }

    readonly List<Vector3> _positions = new();
    readonly Dictionary<(long, long, long), int> _weld = new();
    readonly List<Poly> _polys = new();

    public int PolygonCount => _polys.Count;

    /// <summary>Returns the index of a position, welded to any earlier one within ~10 micrometres.</summary>
    public int AddVertex(Vector3 p)
    {
        var key = ((long)MathF.Round(p.X / WeldQuantum), (long)MathF.Round(p.Y / WeldQuantum), (long)MathF.Round(p.Z / WeldQuantum));
        if (_weld.TryGetValue(key, out int i)) return i;
        _positions.Add(p);
        return _weld[key] = _positions.Count - 1;
    }

    /// <summary>Adds a counter-clockwise polygon. <paramref name="uvs"/> and <paramref name="normals"/> are per corner, or null.</summary>
    public void AddPolygon(int[] verts, string material, Vector2[]? uvs = null, Vector3[]? normals = null)
    {
        // Drop repeated corners (welding can collapse an edge), keeping the per-corner data aligned.
        var keep = new List<int>();
        for (int i = 0; i < verts.Length; i++)
            if (verts[i] != verts[(i + 1) % verts.Length]) keep.Add(i);
        if (keep.Count < 3 || keep.Select(i => verts[i]).Distinct().Count() != keep.Count) return;
        var poly = new Poly
        {
            Verts = keep.Select(i => verts[i]).ToArray(), Material = material,
            UVs = uvs == null ? null : keep.Select(i => uvs[i]).ToArray(),
            Normals = normals == null ? null : keep.Select(i => normals[i]).ToArray(),
        };
        var n = Newell(poly.Verts);
        if (n.LengthSquared() < 1e-14f) return; // zero area
        poly.Plane = Vector3.Normalize(n);
        poly.Offset = Vector3.Dot(poly.Plane, _positions[poly.Verts[0]]);
        _polys.Add(poly);
    }

    Vector3 Newell(int[] v)
    {
        var n = Vector3.Zero;
        for (int i = 0; i < v.Length; i++)
        {
            var a = _positions[v[i]];
            var b = _positions[v[(i + 1) % v.Length]];
            n.X += (a.Y - b.Y) * (a.Z + b.Z);
            n.Y += (a.Z - b.Z) * (a.X + b.X);
            n.Z += (a.X - b.X) * (a.Y + b.Y);
        }
        return n;
    }

    /// <summary>Builds the mesh. Material sizes come from <paramref name="materials"/> for the default projection.</summary>
    public PolyMesh Build(string name, MaterialLibrary materials, bool mergeCoplanar = true)
    {
        var faces = mergeCoplanar ? Merge() : _polys.Select(p => (p.Verts, p.Material, p.UVs, p.Normals)).ToList();
        faces = DropStraightCorners(faces);

        // Compact positions to those still used.
        var remap = new int[_positions.Count];
        Array.Fill(remap, -1);
        var mesh = new PolyMesh { Name = name };
        foreach (var f in faces)
            foreach (var v in f.Verts)
                if (remap[v] < 0) { remap[v] = mesh.Positions.Count; mesh.Positions.Add(_positions[v]); }
        foreach (var f in faces)
        {
            var info = materials.Get(f.Material);
            var density = TextureMapping.Default with { ScaleU = info.Scale, ScaleV = info.Scale };
            mesh.Faces.Add(new Face(f.Verts.Select(v => remap[v]).ToArray(), f.Material, density) { UVs = f.UVs });
            mesh.Faces[^1].Map = TextureMapping.AlignToGrid(mesh.FaceNormal(mesh.Faces.Count - 1), density);
        }
        MarkSoftEdges(mesh, faces.Select(f => f.Normals).ToList());
        return mesh;
    }

    List<(int[] Verts, string Material, Vector2[]? UVs, Vector3[]? Normals)> Merge()
    {
        // Directed edge -> polygon. Edges used twice in the same direction are non-manifold: never merge across them.
        var owner = new Dictionary<(int, int), int>();
        var bad = new HashSet<(int, int)>();
        for (int p = 0; p < _polys.Count; p++)
        {
            var v = _polys[p].Verts;
            for (int i = 0; i < v.Length; i++)
            {
                var e = (v[i], v[(i + 1) % v.Length]);
                if (!owner.TryAdd(e, p)) bad.Add(e);
            }
        }

        var done = new bool[_polys.Count];
        var result = new List<(int[], string, Vector2[]?, Vector3[]?)>();
        var next = new Dictionary<int, int>();      // boundary loop of the growing region: vertex -> next vertex
        var inRegion = new HashSet<int>();           // every vertex the region touches
        var uvAt = new Dictionary<int, Vector2>();
        var normalAt = new Dictionary<int, Vector3>();
        var queue = new Queue<int>();
        for (int seed = 0; seed < _polys.Count; seed++)
        {
            if (done[seed]) continue;
            var sp = _polys[seed];
            next.Clear(); inRegion.Clear(); uvAt.Clear(); normalAt.Clear(); queue.Clear();
            done[seed] = true;
            for (int i = 0; i < sp.Verts.Length; i++)
            {
                next[sp.Verts[i]] = sp.Verts[(i + 1) % sp.Verts.Length];
                inRegion.Add(sp.Verts[i]);
                if (sp.UVs != null) uvAt[sp.Verts[i]] = sp.UVs[i];
                if (sp.Normals != null) normalAt[sp.Verts[i]] = sp.Normals[i];
            }
            int boundaryLength = sp.Verts.Length;
            int anyBoundary = sp.Verts[0];
            Enqueue(sp);
            bool grew = true;
            var deferred = new List<int>();
            while (grew)
            {
                grew = false;
                while (queue.Count > 0)
                {
                    int c = queue.Dequeue();
                    if (done[c]) continue;
                    if (TryAdd(c)) { grew = true; done[c] = true; Enqueue(_polys[c]); }
                    else deferred.Add(c);
                }
                // A candidate rejected earlier (a notch) may fit now that its other side has joined.
                if (grew) { foreach (var d in deferred) queue.Enqueue(d); deferred.Clear(); }
            }

            var loop = new List<int>();
            int start = anyBoundary, cur = start, guard = 0;
            do { loop.Add(cur); cur = next[cur]; } while (cur != start && guard++ < next.Count + 1);
            result.Add((loop.ToArray(), sp.Material,
                sp.UVs != null ? loop.Select(v => uvAt[v]).ToArray() : null,
                sp.Normals != null ? loop.Select(v => normalAt[v]).ToArray() : null));

            void Enqueue(Poly p)
            {
                for (int i = 0; i < p.Verts.Length; i++)
                {
                    var e = (p.Verts[i], p.Verts[(i + 1) % p.Verts.Length]);
                    if (bad.Contains(e)) continue;
                    var twin = (e.Item2, e.Item1);
                    if (!bad.Contains(twin) && owner.TryGetValue(twin, out int o) && !done[o]) queue.Enqueue(o);
                }
            }

            bool TryAdd(int ci)
            {
                var c = _polys[ci];
                if (!string.Equals(c.Material, sp.Material, StringComparison.Ordinal)) return false;
                if ((c.UVs == null) != (sp.UVs == null) || (c.Normals == null) != (sp.Normals == null)) return false;
                if (Vector3.Dot(c.Plane, sp.Plane) < NormalTolerance) return false;
                float tol = PlaneTolerance(sp);
                foreach (var v in c.Verts)
                    if (MathF.Abs(Vector3.Dot(sp.Plane, _positions[v]) - sp.Offset) > tol) return false;

                // Which of c's edges run along the region's boundary (reversed)?
                int n = c.Verts.Length, k = 0, first = -1;
                var shared = new bool[n];
                for (int i = 0; i < n; i++)
                {
                    int a = c.Verts[i], b = c.Verts[(i + 1) % n];
                    shared[i] = next.TryGetValue(b, out int nb) && nb == a && !bad.Contains((a, b)) && !bad.Contains((b, a));
                    if (shared[i]) k++;
                }
                if (k == 0 || k == n || k >= boundaryLength || boundaryLength + n - 2 * k < 3) return false;
                // The shared edges must form one run: find its first edge.
                int runs = 0;
                for (int i = 0; i < n; i++)
                    if (shared[i] && !shared[(i + n - 1) % n]) { runs++; first = i; }
                if (runs != 1) return false;
                // Corners off the run must be new to the region, or the boundary would pinch.
                for (int j = k + 1; j < n; j++)
                    if (inRegion.Contains(c.Verts[(first + j) % n])) return false;
                // UVs must agree where the polygons meet, or merging would lose a seam.
                if (c.UVs != null)
                    for (int j = 0; j <= k; j++)
                    {
                        int idx = (first + j) % n;
                        if (Vector2.DistanceSquared(uvAt[c.Verts[idx]], c.UVs[idx]) > UvTolerance * UvTolerance) return false;
                    }

                // Splice: the region's boundary path end -> ... -> start becomes c's path end -> (off-run corners) -> start.
                int runStart = c.Verts[first], runEnd = c.Verts[(first + k) % n];
                for (int j = 1; j < k; j++) next.Remove(c.Verts[(first + j) % n]);
                int prev = runEnd;
                for (int j = k + 1; j <= n; j++)
                {
                    int idx = (first + j) % n, v = c.Verts[idx];
                    next[prev] = v;
                    prev = v;
                    if (j < n)
                    {
                        inRegion.Add(v);
                        if (c.UVs != null) uvAt[v] = c.UVs[idx];
                        if (c.Normals != null) normalAt[v] = c.Normals[idx];
                    }
                }
                boundaryLength += n - 2 * k;
                anyBoundary = runEnd;
                return true;
            }
        }
        return result;
    }

    float PlaneTolerance(Poly p)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var v in p.Verts) { min = Vector3.Min(min, _positions[v]); max = Vector3.Max(max, _positions[v]); }
        return MathF.Max(1e-4f, (max - min).Length() * 1e-4f);
    }

    /// <summary>Removes corners that sit on a straight border, are used by no other face, and interpolate their UVs linearly.</summary>
    List<(int[] Verts, string Material, Vector2[]? UVs, Vector3[]? Normals)> DropStraightCorners(
        List<(int[] Verts, string Material, Vector2[]? UVs, Vector3[]? Normals)> faces)
    {
        var uses = new Dictionary<int, int>();
        foreach (var f in faces) foreach (var v in f.Verts) uses[v] = uses.GetValueOrDefault(v) + 1;
        var output = new List<(int[], string, Vector2[]?, Vector3[]?)>(faces.Count);
        foreach (var f in faces)
        {
            var keep = Enumerable.Range(0, f.Verts.Length).ToList();
            bool changed = true;
            while (changed && keep.Count > 3)
            {
                changed = false;
                for (int j = 0; j < keep.Count && keep.Count > 3; j++)
                {
                    int ip = keep[(j + keep.Count - 1) % keep.Count], i = keep[j], inx = keep[(j + 1) % keep.Count];
                    if (uses[f.Verts[i]] != 1) continue;
                    Vector3 a = _positions[f.Verts[ip]], p = _positions[f.Verts[i]], b = _positions[f.Verts[inx]];
                    var ab = b - a;
                    float len2 = ab.LengthSquared();
                    if (len2 < 1e-12f) continue;
                    float t = Vector3.Dot(p - a, ab) / len2;
                    if (t <= 0 || t >= 1 || (a + ab * t - p).LengthSquared() > 1e-10f) continue;
                    if (f.UVs != null && Vector2.DistanceSquared(Vector2.Lerp(f.UVs[ip], f.UVs[inx], t), f.UVs[i]) > UvTolerance * UvTolerance) continue;
                    keep.RemoveAt(j);
                    changed = true;
                    j--;
                }
            }
            output.Add(keep.Count == f.Verts.Length ? f : (keep.Select(i => f.Verts[i]).ToArray(), f.Material,
                f.UVs == null ? null : keep.Select(i => f.UVs[i]).ToArray(),
                f.Normals == null ? null : keep.Select(i => f.Normals[i]).ToArray()));
        }
        return output;
    }

    /// <summary>An edge between two non-coplanar faces is soft when the file's corner normals agree on both sides at both ends.</summary>
    static void MarkSoftEdges(PolyMesh mesh, List<Vector3[]?> normals)
    {
        var corners = new Dictionary<Edge, List<(int Face, int A, int B)>>();
        for (int f = 0; f < mesh.Faces.Count; f++)
        {
            if (normals[f] == null) continue;
            var v = mesh.Faces[f].Verts;
            for (int i = 0; i < v.Length; i++)
            {
                int j = (i + 1) % v.Length;
                var e = new Edge(v[i], v[j]);
                if (!corners.TryGetValue(e, out var list)) corners[e] = list = new();
                list.Add((f, i, j));
            }
        }
        foreach (var (e, list) in corners)
        {
            if (list.Count != 2) continue;
            var (f0, a0, b0) = list[0];
            var (f1, a1, b1) = list[1];
            if (Vector3.Dot(mesh.FaceNormal(f0), mesh.FaceNormal(f1)) > NormalTolerance) continue;
            var n0 = normals[f0]!; var n1 = normals[f1]!;
            // Same vertex on both sides: f0's corner a0 is vertex v, find f1's corner holding v.
            int va = mesh.Faces[f0].Verts[a0], vb = mesh.Faces[f0].Verts[b0];
            int ca = mesh.Faces[f1].Verts[a1] == va ? a1 : b1, cb = ca == a1 ? b1 : a1;
            if (Vector3.Dot(n0[a0], n1[ca]) > 0.999f && Vector3.Dot(n0[b0], n1[cb]) > 0.999f && mesh.Faces[f1].Verts[cb] == vb)
                mesh.SoftEdges.Add(e);
        }
    }
}
