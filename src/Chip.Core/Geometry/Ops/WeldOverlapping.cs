using System.Numerics;

namespace Chip.Core.Geometry;

public static partial class MeshOps
{
    /// <summary>Scythe "Weld Overlapping" threshold: 1 mm.</summary>
    public const float WeldDistance = 1e-3f;

    /// <summary>
    /// Merges vertices closer than <paramref name="epsilon"/> (only among <paramref name="verts"/> when given). Each
    /// vertex snaps onto the first one found within range, so positions never drift. Returns the maps and the number of
    /// vertices removed, or null when nothing was close enough.
    /// </summary>
    public static (int[] VertexMap, int[] FaceMap, int Welded)? WeldOverlapping(PolyMesh m, float epsilon = WeldDistance, IEnumerable<int>? verts = null)
    {
        var candidates = (verts ?? Enumerable.Range(0, m.Positions.Count)).Where(v => v >= 0 && v < m.Positions.Count).Distinct().Order().ToList();
        var weld = Enumerable.Range(0, m.Positions.Count).ToArray();
        var cells = new Dictionary<(int, int, int), List<int>>();
        (int, int, int) Cell(Vector3 p) => ((int)MathF.Floor(p.X / epsilon), (int)MathF.Floor(p.Y / epsilon), (int)MathF.Floor(p.Z / epsilon));
        int welded = 0;
        foreach (var v in candidates)
        {
            var p = m.Positions[v];
            var (cx, cy, cz) = Cell(p);
            int found = -1;
            for (int dx = -1; dx <= 1 && found < 0; dx++)
                for (int dy = -1; dy <= 1 && found < 0; dy++)
                    for (int dz = -1; dz <= 1 && found < 0; dz++)
                        if (cells.TryGetValue((cx + dx, cy + dy, cz + dz), out var list))
                            foreach (var r in list)
                                if (Vector3.DistanceSquared(m.Positions[r], p) <= epsilon * epsilon) { found = r; break; }
            if (found >= 0) { weld[v] = found; welded++; continue; }
            if (!cells.TryGetValue((cx, cy, cz), out var cell)) cells[(cx, cy, cz)] = cell = new List<int>(1);
            cell.Add(v);
        }
        if (welded == 0) return null;
        var (vmap, fmap) = MergeVertices(m, weld);
        return (vmap, fmap, welded);
    }
}
