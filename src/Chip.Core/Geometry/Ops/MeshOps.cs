using System.Numerics;

namespace Chip.Core.Geometry;

/// <summary>
/// All topology-changing mesh edits, one file per op family under <c>Geometry/Ops</c>.
/// Rules every op follows: face indices stay stable unless the op documents otherwise; vertex-index changes go
/// through <see cref="RemoveUnusedVertices"/> or <see cref="Reindex"/> so <see cref="PolyMesh.SoftEdges"/> and
/// <see cref="Face.UVs"/> stay consistent; one call = one logical edit (the caller wraps it in a Session step).
/// </summary>
public static partial class MeshOps
{
    public static float Snap(float x, float grid) => MathF.Round(x / grid) * grid;

    public static Vector3 Snap(Vector3 p, float grid) => new(Snap(p.X, grid), Snap(p.Y, grid), Snap(p.Z, grid));

    /// <summary>
    /// Applies an old→new vertex map (-1 = removed) to faces and soft edges, then replaces the position list.
    /// Faces must not reference removed vertices.
    /// </summary>
    public static void Reindex(PolyMesh m, int[] map, List<Vector3> newPositions)
    {
        foreach (var f in m.Faces) f.Verts = f.Verts.Select(v => map[v]).ToArray();
        m.SoftEdges = m.SoftEdges
            .Where(e => e.A < map.Length && e.B < map.Length && map[e.A] >= 0 && map[e.B] >= 0 && map[e.A] != map[e.B])
            .Select(e => new Edge(map[e.A], map[e.B]))
            .ToHashSet();
        m.Positions = newPositions;
        var live = m.Edges().ToHashSet();
        m.SoftEdges.IntersectWith(live);
    }
}
