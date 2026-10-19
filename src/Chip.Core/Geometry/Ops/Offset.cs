using System.Numerics;

namespace Chip.Core.Geometry;

/// <summary>
/// A prepared offset: the selected faces sit on duplicated vertices joined to the old outline by zero-area side quads.
/// <see cref="Apply"/> sets the distance, so an interactive tool can call it every frame without rebuilding anything.
/// </summary>
public sealed class OffsetResult
{
    internal readonly List<(int Vert, Vector3 Base, Vector3 Dir)> Verts = new();

    /// <summary>The new side faces (one quad per region outline edge), world-aligned by <see cref="Apply"/>.</summary>
    public List<int> SideFaces { get; } = new();

    /// <summary>Reversed copies left at the start of an open patch (shells to solids), so a lone plane offsets into a slab.</summary>
    public List<int> CapFaces { get; } = new();

    /// <summary>Every face of the capped slabs (patch, sides and caps), turned round together for negative distances.</summary>
    internal readonly List<int> SlabFaces = new();
    bool _reversed;

    /// <summary>
    /// Moves every face plane of the region <paramref name="distance"/> along its own normal (negative goes inward).
    /// Corners are mitred, so a strip of wall faces round a room becomes a baseboard of even thickness.
    /// </summary>
    public void Apply(PolyMesh m, float distance)
    {
        foreach (var (v, start, dir) in Verts) m.Positions[v] = start + dir * distance;
        // A capped slab offset backwards would be inside out: turn its faces round while the distance is negative.
        if (CapFaces.Count > 0 && distance < 0 != _reversed)
        {
            _reversed = !_reversed;
            foreach (var f in SlabFaces)
            {
                var face = m.Faces[f];
                Array.Reverse(face.Verts);
                if (face.UVs != null) Array.Reverse(face.UVs);
            }
            MeshOps.AlignToGrid(m, CapFaces);
        }
        if (MathF.Abs(distance) > 1e-6f) MeshOps.AlignToGrid(m, SideFaces); // flat sides have no normal to align to
    }
}

public static partial class MeshOps
{
    /// <summary>Longest a mitred vertex may travel per unit of offset, so near-folded corners don't spike.</summary>
    const float OffsetMaxMitre = 4f;

    /// <summary>
    /// Offset topology for a face selection: each edge-connected region is extruded (see <see cref="ExtrudeFaces"/>) and
    /// every region vertex gets the direction that moves each face around it by one unit along that face's normal.
    /// Face indices stay; side faces (and caps for open patches) are appended. Call <see cref="OffsetResult.Apply"/> to
    /// set the distance. Matches Unreal's PolyGroup Edit offset in "Selected Triangle Normals Even" mode with shells to solids.
    /// </summary>
    public static OffsetResult Offset(PolyMesh m, IReadOnlyCollection<int> faces)
    {
        var result = new OffsetResult();
        var region = faces.Where(f => f >= 0 && f < m.Faces.Count).ToHashSet();
        if (region.Count == 0) return result;

        // Distinct face planes around each region vertex, read before the extrude moves the faces onto new vertices.
        var normals = new Dictionary<int, List<Vector3>>();
        foreach (var f in region)
        {
            var n = m.FaceNormal(f);
            if (n.LengthSquared() < 1e-10f) continue;
            foreach (var v in m.Faces[f].Verts)
            {
                if (!normals.TryGetValue(v, out var list)) normals[v] = list = new List<Vector3>();
                if (!list.Any(o => Vector3.Dot(o, n) > 0.9999f)) list.Add(n);
            }
        }

        var before = region.ToDictionary(f => f, f => m.Faces[f].Verts);
        // A closed region (a whole box) has no outline to bridge: it inflates in place instead of orphaning vertices.
        var directed = new HashSet<(int, int)>();
        foreach (var v in before.Values)
            for (int i = 0; i < v.Length; i++) directed.Add((v[i], v[(i + 1) % v.Length]));
        // Shells to solids (Unreal's offset): a region whose whole outline is open mesh border, like a lone rectangle,
        // keeps a reversed copy of itself where it started, so it becomes a closed slab rather than a floating shell.
        var meshEdges = new HashSet<(int, int)>();
        foreach (var face in m.Faces)
            for (int i = 0; i < face.Verts.Length; i++) meshEdges.Add((face.Verts[i], face.Verts[(i + 1) % face.Verts.Length]));
        var caps = new List<Face>();
        var cappedOutline = new HashSet<(int, int)>();
        foreach (var part in FaceRegions(m, region))
        {
            var outline = new List<(int A, int B)>();
            foreach (var f in part)
            {
                var v = m.Faces[f].Verts;
                for (int i = 0; i < v.Length; i++)
                    if (!directed.Contains((v[(i + 1) % v.Length], v[i]))) outline.Add((v[i], v[(i + 1) % v.Length]));
            }
            if (outline.Count == 0 || outline.Any(e => meshEdges.Contains((e.B, e.A)))) continue;
            cappedOutline.UnionWith(outline);
            result.SlabFaces.AddRange(part);
            foreach (var f in part)
            {
                var src = m.Faces[f];
                var cap = src.Derive(src.Verts.Reverse().ToArray());
                cap.UVs = src.UVs?.Reverse().ToArray();
                cap.Dominant = src.Dominant;
                caps.Add(cap);
            }
        }
        if (directed.Any(e => !directed.Contains((e.Item2, e.Item1))))
            result.SideFaces.AddRange(ExtrudeFaces(m, region).SideFaces);
        // Caps keep the original vertices, which ExtrudeFaces left in place for the side faces.
        foreach (var cap in caps)
        {
            result.CapFaces.Add(m.Faces.Count);
            m.Faces.Add(cap);
        }
        AlignToGrid(m, result.CapFaces);
        result.SlabFaces.AddRange(result.CapFaces);
        result.SlabFaces.AddRange(result.SideFaces.Where(s => cappedOutline.Contains((m.Faces[s].Verts[0], m.Faces[s].Verts[1]))));
        // ExtrudeFaces keeps corner order, so each face's old and new corners pair up one to one.
        var moved = new HashSet<int>();
        foreach (var (f, old) in before)
        {
            var now = m.Faces[f].Verts;
            for (int i = 0; i < now.Length; i++)
            {
                if (!moved.Add(now[i])) continue;
                var dir = normals.TryGetValue(old[i], out var ns) ? OffsetMitre(ns) : Vector3.Zero;
                result.Verts.Add((now[i], m.Positions[now[i]], dir));
            }
        }
        return result;
    }

    /// <summary>
    /// The direction d with d·n = 1 for every plane normal n (least squares when no exact answer exists), so offsetting
    /// by t moves each plane by exactly t. Lightly regularised so parallel or coplanar normals stay well defined.
    /// </summary>
    internal static Vector3 OffsetMitre(IReadOnlyList<Vector3> normals)
    {
        if (normals.Count == 0) return Vector3.Zero;
        if (normals.Count == 1) return normals[0];
        float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
        var rhs = Vector3.Zero;
        foreach (var n in normals)
        {
            xx += n.X * n.X; xy += n.X * n.Y; xz += n.X * n.Z;
            yy += n.Y * n.Y; yz += n.Y * n.Z; zz += n.Z * n.Z;
            rhs += n;
        }
        const float eps = 1e-5f;
        var mtx = new Matrix4x4(xx + eps, xy, xz, 0, xy, yy + eps, yz, 0, xz, yz, zz + eps, 0, 0, 0, 0, 1);
        if (!Matrix4x4.Invert(mtx, out var inv)) return SafeNormalize(rhs);
        var d = Vector3.Transform(rhs, inv);
        // Iterative refinement takes the regularisation error (about eps) down to rounding.
        var exact = new Matrix4x4(xx, xy, xz, 0, xy, yy, yz, 0, xz, yz, zz, 0, 0, 0, 0, 1);
        for (int i = 0; i < 3; i++) d += Vector3.Transform(rhs - Vector3.Transform(d, exact), inv);
        if (!float.IsFinite(d.X + d.Y + d.Z) || d.LengthSquared() < 1e-10f) return SafeNormalize(rhs);
        float len = d.Length();
        return len > OffsetMaxMitre ? d * (OffsetMaxMitre / len) : d;
    }
}
