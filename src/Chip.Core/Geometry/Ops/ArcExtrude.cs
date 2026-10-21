using System.Numerics;

namespace Chip.Core.Geometry;

/// <summary>
/// An arc sweep: the line it turns around, the total turn in radians (right-handed about <see cref="AxisDir"/>), and
/// how many equal steps it takes.
/// </summary>
public readonly record struct ArcSweep(Vector3 AxisPoint, Vector3 AxisDir, float Angle, int Segments)
{
    /// <summary>
    /// The sweep that carries a region along <paramref name="travel"/> and curls it toward <paramref name="bendDir"/> (a
    /// unit vector across the travel), round an axis <paramref name="radius"/> from <paramref name="center"/> on that
    /// side. A floor patch with travel +Y bent toward +X rises, then arches over to the right.
    /// </summary>
    public static ArcSweep Bend(Vector3 center, Vector3 travel, Vector3 bendDir, float radius, float angle, int segments) =>
        new(center + bendDir * radius, MeshOps.SafeNormalize(Vector3.Cross(travel, bendDir)), angle, segments);

    /// <summary>The turn after <paramref name="k"/> of the steps.</summary>
    public Matrix4x4 Step(int k) =>
        Matrix4x4.CreateTranslation(-AxisPoint)
        * Matrix4x4.CreateFromAxisAngle(AxisDir, Angle * k / Segments)
        * Matrix4x4.CreateTranslation(AxisPoint);

    public bool Valid => Segments >= 1 && AxisDir != Vector3.Zero && float.IsFinite(Angle + AxisPoint.X + AxisPoint.Y + AxisPoint.Z);
}

/// <summary>What an arc extrude made: the new faces, and the vertex/face maps when vertices on the axis were merged.</summary>
public sealed record ArcExtrudeResult(List<int> SideFaces, (int[] VertexMap, int[] FaceMap)? Collapse);

public static partial class MeshOps
{
    /// <summary>
    /// Extrudes a face region round an axis in <see cref="ArcSweep.Segments"/> equal steps, the same as that many
    /// Shift+rotate gizmo drags around a temporary pivot. The selected faces end up as the cap at the far end (indices
    /// unchanged unless vertices merged); rings that sit on the axis collapse into fans. <paramref name="smooth"/> softens
    /// the edges between steps so the curve shades round; the corners along the sweep stay hard.
    /// </summary>
    public static ArcExtrudeResult ArcExtrudeFaces(PolyMesh m, IReadOnlyCollection<int> faces, ArcSweep sweep, bool smooth)
    {
        var steps = new List<List<int>>();
        if (faces.Count == 0 || !sweep.Valid) return new([], null);
        var start = faces.ToDictionary(f => f, f => m.Faces[f].Verts.Select(v => m.Positions[v]).ToArray());
        for (int k = 1; k <= sweep.Segments; k++)
        {
            steps.Add(ExtrudeFaces(m, faces).SideFaces);
            // Exact from the start positions every step, so long sweeps don't drift.
            var turn = sweep.Step(k);
            foreach (var (f, pts) in start)
            {
                var v = m.Faces[f].Verts;
                for (int i = 0; i < v.Length; i++) m.Positions[v[i]] = Vector3.Transform(pts[i], turn);
            }
        }
        return FinishArc(m, steps, smooth);
    }

    /// <summary>
    /// Extrudes open (boundary) edges round an axis in equal steps, so a floor's border curls up into a cove or fans out
    /// into a curved walkway. Edges used by two faces are ignored. Returns the outer edges at the far end (to select)
    /// alongside the result.
    /// </summary>
    public static (List<Edge> NewEdges, ArcExtrudeResult Result) ArcExtrudeEdges(PolyMesh m, IEnumerable<Edge> edges, ArcSweep sweep, bool smooth)
    {
        var current = edges.Distinct().ToList();
        var steps = new List<List<int>>();
        if (current.Count == 0 || !sweep.Valid) return (current, new([], null));
        var turn = sweep.Step(1);
        for (int k = 1; k <= sweep.Segments; k++)
        {
            int before = m.Positions.Count;
            var (next, made) = ExtrudeEdges(m, current);
            if (made.Count == 0) break;
            steps.Add(made);
            current = next;
            // Each new vertex copies the one it was extruded from, so one step's turn carries it along the arc.
            for (int v = before; v < m.Positions.Count; v++) m.Positions[v] = Vector3.Transform(m.Positions[v], turn);
        }
        var result = FinishArc(m, steps, smooth);
        if (result.Collapse is { } mp)
            current = current.Select(e => (mp.VertexMap[e.A], mp.VertexMap[e.B]))
                .Where(e => e.Item1 >= 0 && e.Item2 >= 0 && e.Item1 != e.Item2)
                .Select(e => new Edge(e.Item1, e.Item2)).Distinct().ToList();
        return (current, result);
    }

    /// <summary>
    /// Softens edges between consecutive steps (when asked), world-aligns the new faces, then merges vertices left on the
    /// axis (their faces become triangles or drop out).
    /// </summary>
    static ArcExtrudeResult FinishArc(PolyMesh m, List<List<int>> steps, bool smooth)
    {
        var sides = steps.SelectMany(s => s).ToList();
        if (smooth && steps.Count > 1)
        {
            var step = new Dictionary<int, int>();
            for (int k = 0; k < steps.Count; k++) foreach (var f in steps[k]) step[f] = k;
            foreach (var (e, fs) in Topology.EdgeFaces(m))
                if (fs.Count == 2 && step.TryGetValue(fs[0], out int a) && step.TryGetValue(fs[1], out int b) && a != b)
                    m.SoftEdges.Add(e);
        }
        AlignToGrid(m, sides);
        var maps = CollapseCoincident(m, 1e-4f);
        if (maps is { } mp) sides = sides.Select(f => mp.FaceMap[f]).Where(f => f >= 0).ToList();
        return new(sides, maps);
    }
}
