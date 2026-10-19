using System.Numerics;

namespace Chip.Core.Geometry;

/// <summary>
/// A prepared inset: the topology is in place (a ring of quads around each selected region) with zero width.
/// <see cref="Apply"/> sets the width, so an interactive tool can call it every frame without rebuilding anything.
/// </summary>
public sealed class InsetResult
{
    internal readonly List<(int Old, int New, Vector3 Base, Vector3 Dir)> Verts = new();

    /// <summary>The new ring faces (one quad per outline edge).</summary>
    public List<int> RingFaces { get; } = new();

    /// <summary>Largest inset width before the outline starts to cross itself (approximate for concave regions).</summary>
    public float MaxInset { get; internal set; } = float.MaxValue;

    /// <summary>
    /// Inset: the region's outline moves inward by <paramref name="amount"/> in the face plane. Outset: the region keeps
    /// its size and the old outline moves outward instead, pushing the surrounding faces back.
    /// </summary>
    public void Apply(PolyMesh m, float amount, bool outset)
    {
        foreach (var (old, nv, start, dir) in Verts)
        {
            m.Positions[nv] = outset ? start : start + dir * amount;
            m.Positions[old] = outset ? start - dir * amount : start;
        }
    }
}

public static partial class MeshOps
{
    /// <summary>
    /// Inset/outset topology for a face selection. Each edge-connected region is inset as a whole (shared inner edges
    /// stay), its outline duplicated onto new vertices and joined to the old outline by quads that copy the owning
    /// face's surface. Face indices stay; ring faces are appended. Call <see cref="InsetResult.Apply"/> to set the width.
    /// </summary>
    public static InsetResult Inset(PolyMesh m, IReadOnlyCollection<int> faces)
    {
        var result = new InsetResult();
        foreach (var region in FaceRegions(m, faces))
            InsetRegion(m, region, result);
        return result;
    }

    static void InsetRegion(PolyMesh m, List<int> region, InsetResult result)
    {
        var directed = new HashSet<(int, int)>();
        foreach (var f in region)
        {
            var v = m.Faces[f].Verts;
            for (int i = 0; i < v.Length; i++) directed.Add((v[i], v[(i + 1) % v.Length]));
        }
        var outline = new List<(int A, int B, int Face)>();
        foreach (var f in region)
        {
            var v = m.Faces[f].Verts;
            for (int i = 0; i < v.Length; i++)
            {
                int a = v[i], b = v[(i + 1) % v.Length];
                if (!directed.Contains((b, a))) outline.Add((a, b, f));
            }
        }
        if (outline.Count == 0) return; // a closed shell has no outline to inset

        var into = new Dictionary<int, int>();
        var outOf = new Dictionary<int, int>();
        foreach (var (a, b, _) in outline)
        {
            outOf.TryAdd(a, b);
            into.TryAdd(b, a);
        }
        var normals = new Dictionary<int, Vector3>();
        foreach (var f in region)
        {
            var n = m.FaceNormal(f);
            foreach (var v in m.Faces[f].Verts) normals[v] = normals.GetValueOrDefault(v) + n;
        }

        var dup = new Dictionary<int, int>();
        var centre = Vector3.Zero;
        foreach (var v in outOf.Keys)
        {
            var p = m.Positions[v];
            centre += p;
            var n = Vector3.Normalize(normals[v]);
            var l1 = SafeNormalize(Vector3.Cross(n, p - m.Positions[into.GetValueOrDefault(v, v)]));
            var l2 = SafeNormalize(Vector3.Cross(n, m.Positions[outOf[v]] - p));
            var miter = l1 + l2;
            Vector3 dir;
            if (miter.LengthSquared() < 1e-8f) dir = l1;
            else
            {
                miter = Vector3.Normalize(miter);
                dir = miter / MathF.Max(Vector3.Dot(miter, l1.LengthSquared() > 0 ? l1 : l2), 0.25f);
            }
            dup[v] = m.Positions.Count;
            m.Positions.Add(p);
            result.Verts.Add((v, dup[v], p, dir));
        }
        centre /= outOf.Count;
        foreach (var (a, b, _) in outline)
        {
            var pa = m.Positions[a];
            var d = m.Positions[b] - pa;
            var off = centre - pa;
            float dist = d.LengthSquared() < 1e-12f ? off.Length() : (off - d * (Vector3.Dot(off, d) / d.LengthSquared())).Length();
            result.MaxInset = MathF.Min(result.MaxInset, dist * 0.98f);
        }

        // Soft edges inside the region follow their vertices onto the duplicates.
        var inner = m.SoftEdges.Where(e => directed.Contains((e.A, e.B)) && directed.Contains((e.B, e.A))).ToList();
        foreach (var e in inner)
        {
            m.SoftEdges.Remove(e);
            m.SoftEdges.Add(new Edge(dup.GetValueOrDefault(e.A, e.A), dup.GetValueOrDefault(e.B, e.B)));
        }

        foreach (var f in region)
            m.Faces[f].Verts = m.Faces[f].Verts.Select(v => dup.GetValueOrDefault(v, v)).ToArray(); // corners keep their UVs
        foreach (var (a, b, f) in outline)
        {
            result.RingFaces.Add(m.Faces.Count);
            m.Faces.Add(m.Faces[f].Derive([a, b, dup[b], dup[a]]));
        }
    }

}
