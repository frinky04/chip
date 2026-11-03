using System.Numerics;
using Chip.Core.Geometry;

namespace Chip.Core.IO;

/// <summary>
/// Groups one glTF mesh's drawn faces into lightmap charts and lays each chart out flat, in the units of the written
/// positions: tightly rotated, unmirrored, with its minimum at exactly (0, 0). Packing and density are the game's. The
/// contract is in <c>skills/chip/reference/export.md</c>.
/// </summary>
public sealed class LightmapCharts
{
    /// <summary>Largest departure from a plane that still counts as on it, in document units (1 mm).</summary>
    const double PlaneTolerance = 0.001;
    const double NormalTolerance = 0.9999;
    /// <summary>An edge shorter than this share of its chart once projected has no direction worth trusting.</summary>
    const double ShortEdge = 1e-6;
    /// <summary>Rectangles closer in area than this share are a tie: float noise shouldn't outrank face order.</summary>
    const double AreaTie = 1e-5;

    readonly Dictionary<int, (int Chart, Vector2[] Uvs)> _faces = [];

    LightmapCharts() { }

    /// <summary>The chart ID of a face, and its position in the chart at corner <paramref name="corner"/>.</summary>
    public (int Chart, Vector2 Uv) At(int face, int corner)
    {
        var (chart, uvs) = _faces[face];
        return (chart, uvs[corner]);
    }

    /// <summary>
    /// Charts for <paramref name="faces"/> of <paramref name="mesh"/>, one glTF mesh's worth. A resampled mesh passes the
    /// mesh it came from and each output face's source face: its charts are decided on those original faces, because a
    /// sliver's normal says little, and laid out from the resampled corners.
    /// </summary>
    public static LightmapCharts Build(PolyMesh mesh, IReadOnlyList<int> faces, Matrix4x4 place, float scale, PolyMesh? source = null, int[]? sourceFaces = null)
    {
        var grouped = source ?? mesh;
        var groupedFaces = sourceFaces == null ? faces : faces.Select(f => sourceFaces[f]).Distinct().ToList();
        var (groupOf, seeds) = Group(grouped, groupedFaces);

        // Numbered as faces come, so IDs have no gaps even if resampling wrote nothing for a source face.
        var members = new List<List<int>>();
        var chartOfGroup = new Dictionary<int, int>();
        foreach (int f in faces)
        {
            int group = groupOf[sourceFaces?[f] ?? f];
            if (!chartOfGroup.TryGetValue(group, out int chart))
            {
                chartOfGroup[group] = chart = members.Count;
                members.Add([]);
            }
            members[chart].Add(f);
        }

        var result = new LightmapCharts();
        foreach (var (group, chart) in chartOfGroup)
        {
            var normal = Vector3.TransformNormal(grouped.FaceNormal(seeds[group]), place);
            result.LayOut(mesh, members[chart], chart, normal, place, scale);
        }
        return result;
    }

    /// <summary>Flood-fills across shared edges. Returns each face's group and each group's first face, whose plane is the chart's.</summary>
    static (Dictionary<int, int> GroupOf, List<int> Seeds) Group(PolyMesh mesh, IReadOnlyList<int> faces)
    {
        var onEdge = new Dictionary<Edge, List<int>>();
        var planes = new Dictionary<int, (Vector3 Normal, Vector3D Centre)>();
        foreach (int f in faces)
        {
            var verts = mesh.Faces[f].Verts;
            var centre = default(Vector3D);
            for (int i = 0; i < verts.Length; i++)
            {
                centre += new Vector3D(mesh.Positions[verts[i]]);
                var edge = new Edge(verts[i], verts[(i + 1) % verts.Length]);
                if (!onEdge.TryGetValue(edge, out var list)) onEdge[edge] = list = [];
                list.Add(f);
            }
            planes[f] = (mesh.FaceNormal(f), centre / Math.Max(1, verts.Length));
        }

        bool WithinPlaneOf(int f, int of)
        {
            var (normal, centre) = planes[of];
            foreach (int v in mesh.Faces[f].Verts)
                if (Math.Abs((new Vector3D(mesh.Positions[v]) - centre).Dot(normal)) > PlaneTolerance) return false;
            return true;
        }
        bool Joins(int a, int b) => Vector3.Dot(planes[a].Normal, planes[b].Normal) >= NormalTolerance && WithinPlaneOf(a, b) && WithinPlaneOf(b, a);

        var groupOf = new Dictionary<int, int>();
        var seeds = new List<int>();
        var queue = new Queue<int>();
        foreach (int seed in faces)
        {
            if (groupOf.ContainsKey(seed)) continue;
            groupOf[seed] = seeds.Count;
            queue.Enqueue(seed);
            while (queue.TryDequeue(out int f))
            {
                var verts = mesh.Faces[f].Verts;
                for (int i = 0; i < verts.Length; i++)
                    foreach (int other in onEdge[new Edge(verts[i], verts[(i + 1) % verts.Length])])
                    {
                        // The seed test stops a gentle curve, whose neighbours each pass, from chaining into one "flat" chart.
                        if (groupOf.ContainsKey(other) || !Joins(f, other) || !Joins(seed, other)) continue;
                        groupOf[other] = seeds.Count;
                        queue.Enqueue(other);
                    }
            }
            seeds.Add(seed);
        }
        return (groupOf, seeds);
    }

    void LayOut(PolyMesh mesh, List<int> faces, int chart, Vector3 normal, Matrix4x4 place, float scale)
    {
        // Any frame in the plane will do to flatten the corners; the tight axes are then found within it.
        var n = new Vector3D(Vector3.Normalize(normal));
        var along = (Math.Abs(n.X) <= Math.Abs(n.Y) && Math.Abs(n.X) <= Math.Abs(n.Z) ? new Vector3D(Vector3.UnitX)
            : Math.Abs(n.Y) <= Math.Abs(n.Z) ? new Vector3D(Vector3.UnitY) : new Vector3D(Vector3.UnitZ)).Cross(n).Normalized();
        var across = n.Cross(along); // so (along, across, n) is right-handed: turning a direction by 90° in here is n × it

        var flat = new List<(double X, double Y)[]>(faces.Count);
        foreach (int f in faces)
        {
            var verts = mesh.Faces[f].Verts;
            var corners = new (double, double)[verts.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                var p = new Vector3D(Vector3.Transform(mesh.Positions[verts[i]], place) * scale);
                corners[i] = (p.Dot(along), p.Dot(across));
            }
            flat.Add(corners);
        }

        var (ux, uy) = TightestAxis(flat);
        double minU = double.MaxValue, minV = double.MaxValue;
        foreach (var corners in flat)
            foreach (var (x, y) in corners)
            {
                minU = Math.Min(minU, x * ux + y * uy);
                minV = Math.Min(minV, -x * uy + y * ux);
            }
        for (int k = 0; k < faces.Count; k++)
            _faces[faces[k]] = (chart, flat[k].Select(c => new Vector2((float)(c.X * ux + c.Y * uy - minU), (float)(-c.X * uy + c.Y * ux - minV))).ToArray());
    }

    /// <summary>The unit edge direction giving the smallest bounding rectangle; the first in face order wins a tie.</summary>
    static (double X, double Y) TightestAxis(List<(double X, double Y)[]> faces)
    {
        // A rectangle only touches the convex hull, which keeps this fast on a wall cut into thousands of cells.
        var hull = ConvexHull(faces.SelectMany(c => c));
        double minX = hull.Min(p => p.X), maxX = hull.Max(p => p.X), minY = hull.Min(p => p.Y), maxY = hull.Max(p => p.Y);
        double shortest = Math.Max(maxX - minX, maxY - minY) * ShortEdge;

        (double, double) best = (1, 0);
        double bestArea = double.MaxValue;
        foreach (var corners in faces)
            for (int i = 0; i < corners.Length; i++)
            {
                var (ax, ay) = corners[i];
                var (bx, by) = corners[(i + 1) % corners.Length];
                double dx = bx - ax, dy = by - ay, length = Math.Sqrt(dx * dx + dy * dy);
                if (length <= shortest || length == 0) continue;
                dx /= length;
                dy /= length;
                double loU = double.MaxValue, hiU = double.MinValue, loV = double.MaxValue, hiV = double.MinValue;
                foreach (var (x, y) in hull)
                {
                    double u = x * dx + y * dy, v = -x * dy + y * dx;
                    loU = Math.Min(loU, u); hiU = Math.Max(hiU, u);
                    loV = Math.Min(loV, v); hiV = Math.Max(hiV, v);
                }
                double area = (hiU - loU) * (hiV - loV);
                if (area < bestArea * (1 - AreaTie))
                {
                    bestArea = area;
                    best = (dx, dy);
                }
            }
        return best;
    }

    /// <summary>Andrew's monotone chain. The hull holds every extreme point; a chart with no area keeps its two ends.</summary>
    static List<(double X, double Y)> ConvexHull(IEnumerable<(double X, double Y)> points)
    {
        var sorted = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (sorted.Count < 3) return sorted;
        var hull = new List<(double X, double Y)>();
        void Add((double X, double Y) p, int floor)
        {
            while (hull.Count >= floor + 2)
            {
                var (ax, ay) = hull[^2];
                var (bx, by) = hull[^1];
                if ((bx - ax) * (p.Y - ay) - (by - ay) * (p.X - ax) > 0) break;
                hull.RemoveAt(hull.Count - 1);
            }
            hull.Add(p);
        }
        foreach (var p in sorted) Add(p, 0);
        int lower = hull.Count - 1;
        for (int i = sorted.Count - 2; i >= 0; i--) Add(sorted[i], lower);
        hull.RemoveAt(hull.Count - 1);
        return hull;
    }

    /// <summary>Doubles, so a chart far from the origin keeps its size to the float's last bit.</summary>
    readonly record struct Vector3D(double X, double Y, double Z)
    {
        public Vector3D(Vector3 v) : this(v.X, v.Y, v.Z) { }
        public static Vector3D operator +(Vector3D a, Vector3D b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vector3D operator -(Vector3D a, Vector3D b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vector3D operator /(Vector3D a, double k) => new(a.X / k, a.Y / k, a.Z / k);
        public double Dot(Vector3 b) => X * b.X + Y * b.Y + Z * b.Z;
        public double Dot(Vector3D b) => X * b.X + Y * b.Y + Z * b.Z;
        public Vector3D Cross(Vector3D b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
        public Vector3D Normalized() => this / Math.Sqrt(Dot(this));
    }
}
