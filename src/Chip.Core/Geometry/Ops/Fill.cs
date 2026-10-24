using System.Numerics;

namespace Chip.Core.Geometry;

public static partial class MeshOps
{
    /// <summary>
    /// Caps holes with one face each. A hole is a loop of open (boundary) edges; it is filled when any of its edges is in
    /// <paramref name="rim"/>, so one picked edge is enough (null fills every hole in the mesh). The new face runs each
    /// rim edge opposite to its neighbour, so it faces the same way as the surface around it. Its surface comes from the
    /// neighbour facing most like it, among the most common material on the rim; tags are kept only when that neighbour
    /// faces the same way (a ceiling capped onto walls isn't a wall). Holes whose outline would cross itself are skipped.
    /// Face indices of existing faces don't move.
    /// </summary>
    public static FillResult FillHoles(PolyMesh m, IEnumerable<Edge>? rim = null)
    {
        var edgeFaces = Topology.EdgeFaces(m);
        var wanted = rim?.ToHashSet();
        // Around a hole the loop runs b→a wherever a face runs a→b along an open edge.
        var outgoing = new Dictionary<int, List<int>>();
        var neighbour = new Dictionary<(int, int), int>();
        for (int f = 0; f < m.Faces.Count; f++)
        {
            var v = m.Faces[f].Verts;
            for (int i = 0; i < v.Length; i++)
            {
                int a = v[i], b = v[(i + 1) % v.Length];
                if (a == b || edgeFaces[new Edge(a, b)].Count != 1) continue;
                (outgoing.TryGetValue(b, out var list) ? list : outgoing[b] = new List<int>(1)).Add(a);
                neighbour[(b, a)] = f;
            }
        }

        var loops = HoleLoops(outgoing, out int broken);
        var faces = new List<int>();
        int skipped = broken, nonPlanar = 0;
        foreach (var loop in loops)
        {
            if (wanted != null && !Enumerable.Range(0, loop.Count).Any(i => wanted.Contains(new Edge(loop[i], loop[(i + 1) % loop.Count])))) continue;
            var rimFaces = Enumerable.Range(0, loop.Count).Select(i => neighbour[(loop[i], loop[(i + 1) % loop.Count])]).ToList();
            int f = m.Faces.Count;
            m.Faces.Add(m.Faces[rimFaces[0]].Derive(loop.ToArray()));
            if (FaceIssue(m, f) != null)
            {
                m.Faces.RemoveAt(f);
                skipped++;
                continue;
            }

            var normal = m.FaceNormal(f);
            var material = rimFaces.GroupBy(r => m.Faces[r].Material).OrderByDescending(g => g.Count()).First().Key;
            int src = rimFaces.Where(r => m.Faces[r].Material == material).MaxBy(r => Vector3.Dot(m.FaceNormal(r), normal));
            var face = m.Faces[src].Derive(loop.ToArray());
            if (Vector3.Dot(m.FaceNormal(src), normal) < FillSameFacing) face.Tags = default;
            m.Faces[f] = face;
            AlignToGrid(m, [f], m.Faces[src].Map);
            var centre = m.FaceCenter(f);
            if (loop.Any(v => MathF.Abs(Vector3.Dot(m.Positions[v] - centre, normal)) > WeldDistance)) nonPlanar++;
            faces.Add(f);
        }
        return new FillResult(faces, skipped, nonPlanar);
    }

    /// <summary>A rim face facing closer than ~45° to the fill counts as the same surface, so its tags carry over.</summary>
    const float FillSameFacing = 0.7f;

    /// <summary>
    /// Splits the directed open edges into simple loops. Where two holes meet at a corner the walk would visit it twice;
    /// each time it comes back to a corner already on its path, that stretch is cut off as its own loop. Walks that dead-end
    /// (open edges wound against each other) are dropped and counted in <paramref name="broken"/>.
    /// </summary>
    static List<List<int>> HoleLoops(Dictionary<int, List<int>> outgoing, out int broken)
    {
        broken = 0;
        var loops = new List<List<int>>();
        var used = new HashSet<(int, int)>();
        foreach (var start in outgoing.Keys.Order().ToList())
        {
            var path = new List<int> { start };
            var at = new Dictionary<int, int> { [start] = 0 };
            int cur = start;
            while (true)
            {
                int to = outgoing.TryGetValue(cur, out var outs) ? outs.FirstOrDefault(t => !used.Contains((cur, t)), -1) : -1;
                if (to < 0)
                {
                    if (path.Count > 1) broken++;
                    break;
                }
                used.Add((cur, to));
                if (at.TryGetValue(to, out int k))
                {
                    var loop = path.GetRange(k, path.Count - k);
                    if (loop.Count >= 3) loops.Add(loop);
                    foreach (var v in path.Skip(k + 1)) at.Remove(v);
                    path.RemoveRange(k + 1, path.Count - k - 1);
                }
                else
                {
                    at[to] = path.Count;
                    path.Add(to);
                }
                cur = to;
            }
        }
        return loops;
    }
}

/// <summary>What <see cref="MeshOps.FillHoles"/> made: the new faces, holes it couldn't fill, and how many fills aren't flat.</summary>
public sealed record FillResult(List<int> Faces, int Skipped, int NonPlanar);
