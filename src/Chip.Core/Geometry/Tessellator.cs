using System.Numerics;
using Chip.Core.Texturing;

namespace Chip.Core.Geometry;

/// <summary>
/// Export tessellation settings. <paramref name="Spacing"/> is the target triangle size in metres (edge splits and the
/// interior lattice). <paramref name="MaxCellsPerFace"/> caps the interior lattice points of one face; past it the
/// face's lattice coarsens to a whole multiple of the spacing (its edge splits never change).
/// </summary>
public sealed record TessellateOptions(float Spacing = 1f, int MaxCellsPerFace = 1024);

/// <summary>A triangle mesh plus per-corner normals, ready for the GLB exporter (faces are all triangles).</summary>
public sealed record TessellatedMesh(PolyMesh Mesh, Vector3[][] CornerNormals)
{
    /// <summary>Source face index of every output triangle (parallel to <c>Mesh.Faces</c>).</summary>
    public int[] SourceFaces { get; init; } = [];

    public int TriangleCount => Mesh.Faces.Count;

    /// <summary>
    /// Faces whose loop was too degenerate (folded or self-touching in its own plane) to resample inside: they keep
    /// their edge splits, so the mesh stays crack-free, but get no interior points.
    /// </summary>
    public int CoarseFaces { get; init; }

    /// <summary>Interior lattice points the triangulation rejected (duplicates or failed point location); 0 normally.</summary>
    public int SkippedPoints { get; init; }
}

/// <summary>
/// Export-only resampling of render faces into small triangles, for per-vertex lighting. The surface doesn't change:
/// every output triangle lies inside its source polygon and plane, and materials, mappings, explicit UVs and corner
/// normals are interpolated from the source face.
/// <para>
/// Each source edge is split into <c>max(1, ceil(length / Spacing))</c> equal parts computed from the edge alone (from
/// its lexicographically lower end), and those points are shared by both faces on the edge, so a closed mesh stays
/// closed with no T-junctions. Inside each face, points of a world-aligned lattice (the two world axes across the face's
/// dominant axis) are added, skipping any closer than 0.3 cells to the boundary, and the lot is triangulated by a
/// constrained Delaunay triangulation.
/// </para>
/// </summary>
public static class Tessellator
{
    /// <summary>Distance from the boundary below which a lattice point is dropped, in lattice cells.</summary>
    public const float BoundaryMargin = 0.3f;

    /// <param name="source">Mesh to resample; it isn't modified.</param>
    /// <param name="faces">Which source faces to include (e.g. the render faces of one material group, or all).</param>
    /// <param name="sourceCornerNormals">Per source face, per corner (<see cref="Topology.CornerNormals"/> or flat
    /// normals). Interpolated across each face. A missing or mismatched entry falls back to the flat face normal.</param>
    /// <param name="options">Spacing and per-face cap.</param>
    public static TessellatedMesh Tessellate(PolyMesh source, IReadOnlyList<int> faces, Vector3[][] sourceCornerNormals, TessellateOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(faces);
        ArgumentNullException.ThrowIfNull(options);
        if (!(options.Spacing > 0) || !float.IsFinite(options.Spacing))
            throw new ArgumentOutOfRangeException(nameof(options), options.Spacing, "Spacing must be a positive number of metres.");

        var builder = new Builder(source, sourceCornerNormals, options);
        foreach (int f in faces) builder.AddFace(f);
        return builder.Finish();
    }

    /// <summary>Tessellates every face, with smoothed (<see cref="Topology.CornerNormals"/>) or flat normals.</summary>
    public static TessellatedMesh Tessellate(PolyMesh source, TessellateOptions options, bool smoothing = true)
    {
        var normals = smoothing
            ? Topology.CornerNormals(source)
            : Enumerable.Range(0, source.Faces.Count).Select(f => Enumerable.Repeat(source.FaceNormal(f), source.Faces[f].Verts.Length).ToArray()).ToArray();
        return Tessellate(source, Enumerable.Range(0, source.Faces.Count).ToArray(), normals, options);
    }

    /// <summary>Accumulates output for one mesh: shared vertices, edge splits, triangles and their attributes.</summary>
    sealed class Builder
    {
        const int MaxEdgeSegments = 1 << 16;

        readonly PolyMesh _src;
        readonly Vector3[][]? _normals;
        readonly float _spacing;
        readonly int _maxCells;
        readonly PolyMesh _out;
        readonly List<Vector3[]> _outNormals = new();
        readonly List<int> _outSource = new();
        readonly int[] _vertexMap;
        readonly Dictionary<Edge, (int Start, int Count, int First)> _edges = new();
        readonly FaceTriangulation _w = new();
        readonly List<int> _srcTris = new();
        readonly List<int> _corner = new();
        readonly List<double> _crossings = new();
        readonly List<SourceTri> _srcTriData = new();
        readonly List<Vector3> _cornerNormal = new();
        Vector3 _tAxis, _bAxis, _origin; // the current face's 2D frame
        int _coarseFaces, _skippedPoints;

        public Builder(PolyMesh src, Vector3[][]? normals, TessellateOptions options)
        {
            _src = src;
            _normals = normals;
            _spacing = options.Spacing;
            _maxCells = options.MaxCellsPerFace;
            _out = new PolyMesh { Id = src.Id, Name = src.Name, Hidden = src.Hidden, Locked = src.Locked, Collision = src.Collision };
            _vertexMap = new int[src.Positions.Count];
            Array.Fill(_vertexMap, -1);
        }

        public TessellatedMesh Finish() => new(_out, _outNormals.ToArray())
        {
            SourceFaces = _outSource.ToArray(), CoarseFaces = _coarseFaces, SkippedPoints = _skippedPoints,
        };

        int OutVertex(int v)
        {
            if (_vertexMap[v] < 0)
            {
                _vertexMap[v] = _out.Positions.Count;
                _out.Positions.Add(_src.Positions[v]);
            }
            return _vertexMap[v];
        }

        /// <summary>
        /// Interior split points of a source edge, created once and shared. Points run from the edge's lexicographically
        /// lower end (by position, then index), so any face, or any mesh, holding the same edge gets bit-identical points.
        /// </summary>
        (int Start, int Count, int First) Splits(int va, int vb)
        {
            var key = new Edge(va, vb);
            if (_edges.TryGetValue(key, out var known)) return known;
            Vector3 pa = _src.Positions[va], pb = _src.Positions[vb];
            int order = Compare(pa, pb);
            bool aFirst = order < 0 || (order == 0 && va <= vb);
            Vector3 p0 = aFirst ? pa : pb, p1 = aFirst ? pb : pa;
            float steps = Vector3.Distance(p0, p1) / _spacing;
            int count = steps >= MaxEdgeSegments ? MaxEdgeSegments : Math.Max(1, (int)MathF.Ceiling(steps - 1e-3f));
            int start = _out.Positions.Count;
            var d = p1 - p0;
            for (int k = 1; k < count; k++) _out.Positions.Add(p0 + d * (k / (float)count));
            return _edges[key] = (start, count, aFirst ? va : vb);
        }

        static int Compare(Vector3 a, Vector3 b) =>
            a.X != b.X ? a.X.CompareTo(b.X) : a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.Z.CompareTo(b.Z);

        public void AddFace(int f)
        {
            if ((uint)f >= (uint)_src.Faces.Count) return;
            var face = _src.Faces[f];
            var verts = face.Verts;
            int n = verts.Length;
            if (n < 3) return;
            var P = _src.Positions;

            var normal = _src.FaceNormal(f);
            _tAxis = Vector3.Normalize(Vector3.Cross(MathF.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX, normal));
            _bAxis = Vector3.Cross(normal, _tAxis); // (t, b, normal) is right-handed: CCW about the normal is CCW in 2D
            _origin = P[verts[0]];
            var given = _normals is { } all && f < all.Length && all[f] is { } cn && cn.Length == n ? cn : null;
            _cornerNormal.Clear();
            for (int i = 0; i < n; i++) _cornerNormal.Add(given?[i] ?? normal);
            var uvs = face.UVs is { } u && u.Length == n ? u : null;
            var w = _w;

            // Boundary: corners and edge splits in face order.
            w.Clear();
            _corner.Clear();
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                _corner.Add(w.Count);
                AddPoint(P[verts[i]], OutVertex(verts[i]), _cornerNormal[i], uvs?[i] ?? default);
                var (start, count, first) = Splits(verts[i], verts[j]);
                bool forward = first == verts[i];
                for (int m = 1; m < count; m++)
                {
                    int o = start + (forward ? m : count - m) - 1;
                    float t = m / (float)count;
                    AddPoint(_out.Positions[o], o, Vector3.Lerp(_cornerNormal[i], _cornerNormal[j], t),
                        uvs is null ? default : Vector2.Lerp(uvs[i], uvs[j], t));
                }
            }
            int boundary = w.Count;
            w.SetScale();

            // Start from today's triangulation of the source face, then split its edges in place.
            _srcTris.Clear();
            Triangulator.Triangulate(_src, f, _srcTris);
            bool clean = _srcTris.Count >= 3;
            for (int i = 0; clean && i + 2 < _srcTris.Count; i += 3)
                if (w.Orient(_corner[_srcTris[i]], _corner[_srcTris[i + 1]], _corner[_srcTris[i + 2]]) <= w.AreaEps) clean = false;

            bool built = _srcTris.Count >= 3 && w.Build(_srcTris, _corner);
            for (int i = 0; built && i < n; i++)
            {
                int a = _corner[i], b = _corner[(i + 1) % n];
                int end = i + 1 < n ? b : boundary;
                built = w.SplitBoundary(a, b, a + 1, end - a - 1);
            }
            if (!built)
            {
                // The source loop is too broken for an edge-respecting triangulation: fan the boundary so edges still match.
                w.Fan(boundary);
                clean = false;
            }

            if (clean)
            {
                w.MakeDelaunay();
                if (_maxCells > 0) AddLattice(f, normal, uvs);
                for (int p = boundary; p < w.Count; p++)
                    if (!w.Insert(p)) _skippedPoints++;
            }
            else _coarseFaces++;

            Emit(f, face, normal, uvs is not null);

            void AddPoint(Vector3 pos, int outIndex, Vector3 nrm, Vector2 uv)
            {
                var (x, y) = To2D(pos);
                w.Add(x, y, outIndex, pos, nrm, uv);
            }
        }

        (double X, double Y) To2D(Vector3 pos)
        {
            var r = pos - _origin;
            return (Vector3.Dot(r, _tAxis), Vector3.Dot(r, _bAxis));
        }

        readonly record struct SourceTri(int A, int B, int C, double U0, double V0, double E1U, double E1V, double E2U, double E2V, double InvDet);

        /// <summary>World-aligned lattice points inside the face, on its dominant-axis projection, capped per face.</summary>
        void AddLattice(int f, Vector3 normal, Vector2[]? uvs)
        {
            var verts = _src.Faces[f].Verts;
            int n = verts.Length;
            var P = _src.Positions;
            var dom = TextureMapping.DominantAxis(normal);
            int wi = dom.X != 0 ? 0 : dom.Y != 0 ? 1 : 2;
            int ui = wi == 0 ? 2 : 0, vi = wi == 1 ? 2 : 1;

            double area = 0, vMin = double.MaxValue, vMax = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                var a = P[verts[i]];
                var b = P[verts[(i + 1) % n]];
                area += (double)Axis(a, ui) * Axis(b, vi) - (double)Axis(b, ui) * Axis(a, vi);
                vMin = Math.Min(vMin, Axis(a, vi));
                vMax = Math.Max(vMax, Axis(a, vi));
            }
            area = Math.Abs(area) * 0.5;
            double s = _spacing;
            if (area < s * s * 0.25) return; // smaller than a cell: boundary splits only

            _srcTriData.Clear();
            for (int i = 0; i + 2 < _srcTris.Count; i += 3)
            {
                int a = _srcTris[i], b = _srcTris[i + 1], c = _srcTris[i + 2];
                Vector3 pa = P[verts[a]], pb = P[verts[b]], pc = P[verts[c]];
                double e1u = Axis(pb, ui) - Axis(pa, ui), e1v = Axis(pb, vi) - Axis(pa, vi);
                double e2u = Axis(pc, ui) - Axis(pa, ui), e2v = Axis(pc, vi) - Axis(pa, vi);
                double det = e1u * e2v - e1v * e2u;
                if (Math.Abs(det) < 1e-14) continue;
                _srcTriData.Add(new SourceTri(a, b, c, Axis(pa, ui), Axis(pa, vi), e1u, e1v, e2u, e2v, 1 / det));
            }
            if (_srcTriData.Count == 0) return;

            var w = _w;
            int boundary = w.Count;
            int k = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(area / ((double)_maxCells * s * s))));
            while (true)
            {
                w.Truncate(boundary);
                double cell = k * s;
                if (Generate(cell)) return;
                k++;
            }

            bool Generate(double cell)
            {
                double margin2 = BoundaryMargin * cell * BoundaryMargin * cell;
                int hint = 0;
                for (long row = (long)Math.Ceiling(vMin / cell); row * cell <= vMax; row++)
                {
                    double v = row * cell;
                    _crossings.Clear();
                    for (int i = 0; i < n; i++)
                    {
                        var a = P[verts[i]];
                        var b = P[verts[(i + 1) % n]];
                        double av = Axis(a, vi), bv = Axis(b, vi);
                        if ((av <= v && v < bv) || (bv <= v && v < av))
                            _crossings.Add(Axis(a, ui) + (v - av) / (bv - av) * (Axis(b, ui) - Axis(a, ui)));
                    }
                    _crossings.Sort();
                    for (int c = 0; c + 1 < _crossings.Count; c += 2)
                    {
                        for (long col = (long)Math.Ceiling(_crossings[c] / cell); col * cell <= _crossings[c + 1]; col++)
                        {
                            if (!TryAdd((float)(col * cell), (float)v, margin2, ref hint)) continue;
                            if (w.Count - boundary > _maxCells) return false;
                        }
                    }
                }
                return true;
            }

            bool TryAdd(float u, float v, double margin2, ref int hint)
            {
                // Locate the source triangle (in the projection) for the height and the attributes.
                int best = -1;
                double bestMin = double.MinValue, l0 = 0, l1 = 0, l2 = 0;
                for (int q = 0; q < _srcTriData.Count; q++)
                {
                    var t = _srcTriData[(q + hint) % _srcTriData.Count];
                    double du = u - t.U0, dv = v - t.V0;
                    double b1 = (du * t.E2V - dv * t.E2U) * t.InvDet;
                    double b2 = (t.E1U * dv - t.E1V * du) * t.InvDet;
                    double b0 = 1 - b1 - b2;
                    double min = Math.Min(b0, Math.Min(b1, b2));
                    if (min > bestMin) { bestMin = min; best = (q + hint) % _srcTriData.Count; l0 = b0; l1 = b1; l2 = b2; }
                    if (min >= 0) break;
                }
                if (best < 0 || bestMin < -1e-6) return false;
                hint = best;
                var st = _srcTriData[best];
                Vector3 pa = P[verts[st.A]], pb = P[verts[st.B]], pc = P[verts[st.C]];
                float height = (float)(l0 * Axis(pa, wi) + l1 * Axis(pb, wi) + l2 * Axis(pc, wi));
                var pos = Compose(ui, u, vi, v, wi, height);

                var (x, y) = To2D(pos);
                for (int i = 0; i < n; i++)
                    if (w.SegmentDistance2(x, y, _corner[i], _corner[(i + 1) % n]) < margin2) return false;

                var nrm = (float)l0 * _cornerNormal[st.A] + (float)l1 * _cornerNormal[st.B] + (float)l2 * _cornerNormal[st.C];
                var uv = uvs is null ? default : (float)l0 * uvs[st.A] + (float)l1 * uvs[st.B] + (float)l2 * uvs[st.C];
                w.Add(x, y, -1, pos, nrm, uv);
                return true;
            }
        }

        static float Axis(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

        static Vector3 Compose(int ai, float a, int bi, float b, int ci, float c)
        {
            Span<float> xyz = stackalloc float[3];
            xyz[ai] = a; xyz[bi] = b; xyz[ci] = c;
            return new Vector3(xyz[0], xyz[1], xyz[2]);
        }

        void Emit(int f, Face face, Vector3 normal, bool hasUVs)
        {
            var w = _w;
            for (int t = 0; t < w.TriCount; t++)
            {
                var vs = new int[3];
                var ns = new Vector3[3];
                var uv = hasUVs ? new Vector2[3] : null;
                for (int c = 0; c < 3; c++)
                {
                    int p = w.V[t * 3 + c];
                    if (w.Out[p] < 0)
                    {
                        w.Out[p] = _out.Positions.Count;
                        _out.Positions.Add(w.Pos[p]);
                    }
                    vs[c] = w.Out[p];
                    var nr = w.Nrm[p];
                    float len = nr.Length();
                    ns[c] = len > 1e-12f ? nr / len : normal;
                    if (uv is not null) uv[c] = w.Uv[p];
                }
                _out.Faces.Add(new Face(vs, face.Material, face.Map) { UVs = uv, Dominant = face.Dominant });
                _outNormals.Add(ns);
                _outSource.Add(f);
            }
        }
    }

    /// <summary>
    /// A 2D triangulation of one face, as triangle vertex and neighbour arrays. Edge k of triangle t runs from
    /// V[3t+k] to V[3t+(k+1)%3]; N[3t+k] is the triangle across it, or -1 on the face boundary (the only constrained
    /// edges, so they are never flipped).
    /// </summary>
    sealed class FaceTriangulation
    {
        public readonly List<double> X = new(), Y = new();
        public readonly List<int> Out = new();
        public readonly List<Vector3> Pos = new(), Nrm = new();
        public readonly List<Vector2> Uv = new();
        public readonly List<int> V = new(), N = new();
        readonly Stack<int> _stack = new();
        readonly Dictionary<long, int> _directed = new();
        int _hint;
        double _lengthEps;
        public double AreaEps;

        public int Count => X.Count;
        public int TriCount => V.Count / 3;

        public void Clear()
        {
            X.Clear(); Y.Clear(); Out.Clear(); Pos.Clear(); Nrm.Clear(); Uv.Clear();
            V.Clear(); N.Clear();
            _hint = 0;
        }

        public void Truncate(int count)
        {
            int extra = Count - count;
            if (extra <= 0) return;
            X.RemoveRange(count, extra); Y.RemoveRange(count, extra); Out.RemoveRange(count, extra);
            Pos.RemoveRange(count, extra); Nrm.RemoveRange(count, extra); Uv.RemoveRange(count, extra);
        }

        public void Add(double x, double y, int outIndex, Vector3 pos, Vector3 nrm, Vector2 uv)
        {
            X.Add(x); Y.Add(y); Out.Add(outIndex); Pos.Add(pos); Nrm.Add(nrm); Uv.Add(uv);
        }

        /// <summary>Tolerances from the boundary's extent.</summary>
        public void SetScale()
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            for (int i = 0; i < Count; i++)
            {
                minX = Math.Min(minX, X[i]); maxX = Math.Max(maxX, X[i]);
                minY = Math.Min(minY, Y[i]); maxY = Math.Max(maxY, Y[i]);
            }
            double size = Math.Max(Math.Max(maxX - minX, maxY - minY), 1e-6);
            _lengthEps = size * 1e-9;
            AreaEps = size * size * 1e-14;
        }

        public double Orient(int a, int b, int c) => (X[b] - X[a]) * (Y[c] - Y[a]) - (Y[b] - Y[a]) * (X[c] - X[a]);

        double Orient(int a, int b, double px, double py) => (X[b] - X[a]) * (py - Y[a]) - (Y[b] - Y[a]) * (px - X[a]);

        public double SegmentDistance2(double px, double py, int a, int b)
        {
            double ex = X[b] - X[a], ey = Y[b] - Y[a];
            double len2 = ex * ex + ey * ey;
            double t = len2 > 0 ? Math.Clamp(((px - X[a]) * ex + (py - Y[a]) * ey) / len2, 0, 1) : 0;
            double dx = X[a] + ex * t - px, dy = Y[a] + ey * t - py;
            return dx * dx + dy * dy;
        }

        int NewTri()
        {
            V.Add(-1); V.Add(-1); V.Add(-1);
            N.Add(-1); N.Add(-1); N.Add(-1);
            return TriCount - 1;
        }

        void Set(int t, int a, int b, int c, int nab, int nbc, int nca)
        {
            int i = t * 3;
            V[i] = a; V[i + 1] = b; V[i + 2] = c;
            N[i] = nab; N[i + 1] = nbc; N[i + 2] = nca;
        }

        int SlotOf(int t, int vertex)
        {
            int i = t * 3;
            return V[i] == vertex ? 0 : V[i + 1] == vertex ? 1 : V[i + 2] == vertex ? 2 : -1;
        }

        /// <summary>Points the triangle across edge k of t back at t.</summary>
        void Link(int t, int k)
        {
            int u = N[t * 3 + k];
            if (u < 0) return;
            int s = SlotOf(u, V[t * 3 + (k + 1) % 3]);
            if (s >= 0) N[u * 3 + s] = t;
        }

        /// <summary>Loads a triangulation given as loop-index triples mapped through <paramref name="corner"/>.</summary>
        public bool Build(List<int> loopTris, List<int> corner)
        {
            _directed.Clear();
            for (int i = 0; i + 2 < loopTris.Count; i += 3)
            {
                int t = NewTri();
                Set(t, corner[loopTris[i]], corner[loopTris[i + 1]], corner[loopTris[i + 2]], -1, -1, -1);
                for (int k = 0; k < 3; k++)
                {
                    int a = V[t * 3 + k], b = V[t * 3 + (k + 1) % 3];
                    if (a == b || !_directed.TryAdd(Key(a, b), t * 3 + k)) return false;
                }
            }
            foreach (var (key, e) in _directed)
            {
                int a = (int)(key >> 32), b = (int)(key & 0xffffffff);
                if (_directed.TryGetValue(Key(b, a), out int back)) N[e] = back / 3;
            }
            return true;
        }

        static long Key(int a, int b) => ((long)a << 32) | (uint)b;

        /// <summary>Splits boundary edge a→b at points first..first+count-1 (in order from a), keeping it constrained.</summary>
        public bool SplitBoundary(int a, int b, int first, int count)
        {
            int t = -1, k = -1;
            for (int i = 0; i < V.Count; i++)
                if (V[i] == a && V[i - i % 3 + (i % 3 + 1) % 3] == b) { t = i / 3; k = i % 3; break; }
            if (t < 0 || N[t * 3 + k] >= 0) return false;
            if (count == 0) return true;

            int c = V[t * 3 + (k + 2) % 3];
            int nbc = N[t * 3 + (k + 1) % 3], nca = N[t * 3 + (k + 2) % 3];
            for (int p = first; p < first + count; p++)
            {
                int t2 = NewTri();
                Set(t, a, p, c, -1, t2, nca);
                Set(t2, p, b, c, -1, nbc, t);
                Link(t, 2);
                Link(t2, 1);
                nca = t;
                t = t2;
                a = p;
            }
            return true;
        }

        /// <summary>Fallback: a fan over the boundary loop (no interior points).</summary>
        public void Fan(int boundary)
        {
            V.Clear(); N.Clear();
            for (int i = 1; i + 1 < boundary; i++)
            {
                int t = NewTri();
                Set(t, 0, i, i + 1, -1, -1, -1);
            }
        }

        /// <summary>Lawson flips over every interior edge until the triangulation is constrained Delaunay.</summary>
        public void MakeDelaunay()
        {
            for (int e = 0; e < N.Count; e++)
                if (N[e] >= 0) _stack.Push(e);
            Legalize();
        }

        /// <summary>Inserts point p (inside the face) and restores the Delaunay property. Returns false if it was skipped.</summary>
        public bool Insert(int p)
        {
            int t = Locate(X[p], Y[p]);
            if (t < 0) return false;
            int bestK = -1;
            double bestDist = double.MaxValue;
            for (int k = 0; k < 3; k++)
            {
                int a = V[t * 3 + k], b = V[t * 3 + (k + 1) % 3];
                double dx = X[p] - X[a], dy = Y[p] - Y[a];
                if (dx * dx + dy * dy <= _lengthEps * _lengthEps) return false; // duplicate of a vertex
                double len = Math.Sqrt((X[b] - X[a]) * (X[b] - X[a]) + (Y[b] - Y[a]) * (Y[b] - Y[a]));
                double dist = Orient(a, b, X[p], Y[p]) / Math.Max(len, 1e-300);
                if (dist < bestDist) { bestDist = dist; bestK = k; }
            }
            if (bestDist <= _lengthEps)
            {
                // On the boundary it would add a vertex the neighbouring face doesn't have.
                if (N[t * 3 + bestK] < 0 || !SplitEdge(t, bestK, p)) return false;
            }
            else SplitTriangle(t, p);
            Legalize();
            return true;
        }

        int Locate(double px, double py)
        {
            int t = _hint < TriCount ? _hint : 0;
            int limit = TriCount + 16;
            for (int step = 0; step < limit; step++)
            {
                int next = -1;
                bool blocked = false;
                for (int kk = 0; kk < 3; kk++)
                {
                    int k = (kk + step) % 3;
                    if (Orient(V[t * 3 + k], V[t * 3 + (k + 1) % 3], px, py) < 0)
                    {
                        next = N[t * 3 + k];
                        blocked = next < 0;
                        break;
                    }
                }
                if (next < 0 && !blocked) return _hint = t;
                if (blocked) break;
                t = next;
            }
            // Walk left the (concave) domain or cycled: scan.
            for (t = 0; t < TriCount; t++)
            {
                int i = t * 3;
                if (Orient(V[i], V[i + 1], px, py) >= -AreaEps && Orient(V[i + 1], V[i + 2], px, py) >= -AreaEps
                    && Orient(V[i + 2], V[i], px, py) >= -AreaEps)
                    return _hint = t;
            }
            return -1;
        }

        void SplitTriangle(int t, int p)
        {
            int i = t * 3;
            int a = V[i], b = V[i + 1], c = V[i + 2], nab = N[i], nbc = N[i + 1], nca = N[i + 2];
            int t1 = NewTri(), t2 = NewTri();
            Set(t, a, b, p, nab, t1, t2);
            Set(t1, b, c, p, nbc, t2, t);
            Set(t2, c, a, p, nca, t, t1);
            Link(t, 0); Link(t1, 0); Link(t2, 0);
            _stack.Push(t * 3); _stack.Push(t1 * 3); _stack.Push(t2 * 3);
        }

        bool SplitEdge(int t, int k, int p)
        {
            int a = V[t * 3 + k], b = V[t * 3 + (k + 1) % 3], c = V[t * 3 + (k + 2) % 3];
            int nbc = N[t * 3 + (k + 1) % 3], nca = N[t * 3 + (k + 2) % 3];
            int u = N[t * 3 + k];
            int j = SlotOf(u, b);
            if (j < 0 || V[u * 3 + (j + 1) % 3] != a) return false;
            int d = V[u * 3 + (j + 2) % 3];
            int nad = N[u * 3 + (j + 1) % 3], ndb = N[u * 3 + (j + 2) % 3];
            int t1 = NewTri(), u1 = NewTri();
            Set(t, a, p, c, u1, t1, nca);
            Set(t1, p, b, c, u, nbc, t);
            Set(u, b, p, d, t1, u1, ndb);
            Set(u1, p, a, d, t, nad, u);
            Link(t, 2); Link(t1, 1); Link(u, 2); Link(u1, 1);
            _stack.Push(t * 3 + 2); _stack.Push(t1 * 3 + 1); _stack.Push(u * 3 + 2); _stack.Push(u1 * 3 + 1);
            return true;
        }

        void Legalize()
        {
            long guard = 0, limit = 64L * TriCount + 1024;
            while (_stack.Count > 0 && guard++ < limit)
            {
                int e = _stack.Pop();
                int t = e / 3, k = e % 3;
                int u = N[e];
                if (u < 0) continue;
                int a = V[t * 3 + k], b = V[t * 3 + (k + 1) % 3], c = V[t * 3 + (k + 2) % 3];
                int j = SlotOf(u, b);
                if (j < 0 || V[u * 3 + (j + 1) % 3] != a) continue;
                int d = V[u * 3 + (j + 2) % 3];
                if (!InCircle(a, b, c, d)) continue;
                if (Orient(c, a, d) <= AreaEps || Orient(d, b, c) <= AreaEps) continue; // flip would fold
                int nbc = N[t * 3 + (k + 1) % 3], nca = N[t * 3 + (k + 2) % 3];
                int nad = N[u * 3 + (j + 1) % 3], ndb = N[u * 3 + (j + 2) % 3];
                Set(t, c, a, d, nca, nad, u);
                Set(u, d, b, c, ndb, nbc, t);
                Link(t, 0); Link(t, 1); Link(u, 0); Link(u, 1);
                _stack.Push(t * 3); _stack.Push(t * 3 + 1); _stack.Push(u * 3); _stack.Push(u * 3 + 1);
            }
            _stack.Clear();
        }

        /// <summary>True when d is strictly inside the circumcircle of CCW triangle (a, b, c), beyond rounding noise.</summary>
        bool InCircle(int a, int b, int c, int d)
        {
            double adx = X[a] - X[d], ady = Y[a] - Y[d];
            double bdx = X[b] - X[d], bdy = Y[b] - Y[d];
            double cdx = X[c] - X[d], cdy = Y[c] - Y[d];
            double al = adx * adx + ady * ady, bl = bdx * bdx + bdy * bdy, cl = cdx * cdx + cdy * cdy;
            double det = al * (bdx * cdy - cdx * bdy) + bl * (cdx * ady - adx * cdy) + cl * (adx * bdy - bdx * ady);
            double perm = al * (Math.Abs(bdx * cdy) + Math.Abs(cdx * bdy)) + bl * (Math.Abs(cdx * ady) + Math.Abs(adx * cdy))
                + cl * (Math.Abs(adx * bdy) + Math.Abs(bdx * ady));
            return det > perm * 1e-10;
        }
    }
}
