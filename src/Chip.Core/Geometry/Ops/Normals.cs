namespace Chip.Core.Geometry;

/// <summary>
/// Smoothing (SPEC §6 soft / hard / dominant normals). Shading is derived, never stored: these ops only edit
/// <see cref="PolyMesh.SoftEdges"/> and <see cref="Face.Dominant"/>, and <see cref="Topology.CornerNormals"/> turns them into
/// per-corner normals for the viewport and the glTF export. No op here changes topology or face indices.
/// </summary>
public static partial class MeshOps
{
    /// <summary>Default auto-smooth threshold in degrees: smooths cylinders and spheres with more than 8 sides, keeps 90° corners hard.</summary>
    public const float DefaultSmoothAngle = 45f;

    /// <summary>Every edge of the given faces: the edges inside the region and its outline.</summary>
    public static HashSet<Edge> EdgesOfFaces(PolyMesh m, IEnumerable<int> faces)
    {
        var edges = new HashSet<Edge>();
        foreach (var f in faces)
            if (f >= 0 && f < m.Faces.Count) edges.UnionWith(m.Faces[f].Edges());
        return edges;
    }

    /// <summary>
    /// Marks <paramref name="edges"/> soft (smooth shading across them) or hard (a crease). Only edges shared by two or more
    /// faces can be soft; open edges and edges not in the mesh are ignored. Returns how many edges changed.
    /// </summary>
    public static int SetEdgeSmoothing(PolyMesh m, IEnumerable<Edge> edges, bool soft)
    {
        int changed = 0;
        if (!soft)
        {
            foreach (var e in edges) if (m.SoftEdges.Remove(e)) changed++;
            return changed;
        }
        var edgeFaces = Topology.EdgeFaces(m);
        foreach (var e in edges)
            if (edgeFaces.TryGetValue(e, out var faces) && faces.Count >= 2 && m.SoftEdges.Add(e)) changed++;
        return changed;
    }

    /// <summary>
    /// Dominant faces soften their edges to neighbours that meet them at less than this many degrees: bevels and chamfers
    /// (45° or less on a right-angle corner) bend to match, while walls at right angles stay separate surfaces.
    /// </summary>
    public const float DominantAngle = 60f;

    /// <summary>
    /// Scythe dominant normals: the faces become <see cref="Face.Dominant"/>, and their edges to neighbours meeting them at
    /// less than <paramref name="maxAngle"/> degrees become soft, so those neighbours take the dominant faces' normal at
    /// shared corners while the dominant faces stay flat (a large face next to a chamfer shades the chamfer like a round
    /// instead of being bent by it). Edges already soft stay soft. Returns how many faces became dominant.
    /// </summary>
    public static int SetDominantFaces(PolyMesh m, IEnumerable<int> faces, float maxAngle = DominantAngle)
    {
        var list = faces.Where(f => f >= 0 && f < m.Faces.Count).Distinct().ToList();
        int changed = 0;
        foreach (var f in list)
            if (!m.Faces[f].Dominant) { m.Faces[f].Dominant = true; changed++; }
        var edgeFaces = Topology.EdgeFaces(m);
        foreach (var e in EdgesOfFaces(m, list))
            if (Topology.EdgeAngle(m, e, edgeFaces) is { } angle && angle < maxAngle) m.SoftEdges.Add(e);
        return changed;
    }

    /// <summary>Clears <see cref="Face.Dominant"/> on the faces. Returns how many changed.</summary>
    public static int ClearDominantFaces(PolyMesh m, IEnumerable<int> faces)
    {
        int changed = 0;
        foreach (var f in faces)
            if (f >= 0 && f < m.Faces.Count && m.Faces[f].Dominant) { m.Faces[f].Dominant = false; changed++; }
        return changed;
    }

    /// <summary>
    /// Auto-smooth by angle: of <paramref name="edges"/> (all edges when null), those shared by exactly two faces whose
    /// normals differ by less than <paramref name="maxAngle"/> degrees become soft; every other edge becomes hard.
    /// Returns the number of soft edges in the scope afterwards.
    /// </summary>
    public static int AutoSmooth(PolyMesh m, float maxAngle, IEnumerable<Edge>? edges = null)
    {
        var edgeFaces = Topology.EdgeFaces(m);
        int soft = 0;
        foreach (var e in edges ?? edgeFaces.Keys.ToList())
        {
            if (Topology.EdgeAngle(m, e, edgeFaces) is { } angle && angle < maxAngle)
            {
                m.SoftEdges.Add(e);
                soft++;
            }
            else m.SoftEdges.Remove(e);
        }
        return soft;
    }

    /// <summary>Removes all smoothing: every edge hard and no dominant faces (flat shading).</summary>
    public static void ClearSmoothing(PolyMesh m)
    {
        m.SoftEdges.Clear();
        foreach (var f in m.Faces) f.Dominant = false;
    }
}
