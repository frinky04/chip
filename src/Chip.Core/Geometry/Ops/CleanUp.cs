using System.Numerics;

namespace Chip.Core.Geometry;

/// <summary>
/// What <see cref="MeshOps.CleanUp"/> did: old→new vertex and face maps (-1 = removed; faces split off by untangling
/// are appended) and a count per fix.
/// </summary>
public sealed record CleanUpResult(int[] VertexMap, int[] FaceMap, int Welded, int Untangled, int TJunctions, int Dissolved, int RemovedFaces)
{
    public bool Changed => Welded + Untangled + TJunctions + Dissolved + RemovedFaces > 0;
}

public static partial class MeshOps
{
    /// <summary>
    /// Repairs a mesh in one go: welds vertices closer than <paramref name="weldDistance"/>, splits faces that cross or
    /// pinch themselves into simple ones, sews T-junctions (a vertex on a neighbour's open edge goes into that edge, so
    /// the faces share their edges again), removes faces with no area left, and dissolves vertices that only continue a
    /// straight edge (left behind when what split the edge was deleted).
    /// </summary>
    public static CleanUpResult CleanUp(PolyMesh m, float weldDistance = WeldDistance)
    {
        var vmap = Enumerable.Range(0, m.Positions.Count).ToArray();
        var fmap = Enumerable.Range(0, m.Faces.Count).ToArray();
        int welded = 0;
        if (WeldOverlapping(m, weldDistance) is { } w)
        {
            welded = w.Welded;
            vmap = vmap.Select(v => w.VertexMap[v]).ToArray();
            fmap = fmap.Select(f => w.FaceMap[f]).ToArray();
        }

        var tangled = Enumerable.Range(0, m.Faces.Count)
            .Where(f => FaceIssue(m, f) is MeshIssueKind.RepeatedVertex or MeshIssueKind.SelfIntersectingFace).ToList();
        int untangled = UntangleFaces(m, tangled).Untangled;
        int tJunctions = SewTJunctions(m, weldDistance);

        var dead = Enumerable.Range(0, m.Faces.Count).Where(f => FaceIssue(m, f) == MeshIssueKind.DegenerateFace).ToHashSet();
        if (dead.Count > 0)
        {
            var after = new int[m.Faces.Count];
            var kept = new List<Face>();
            for (int f = 0; f < m.Faces.Count; f++)
            {
                after[f] = dead.Contains(f) ? -1 : kept.Count;
                if (!dead.Contains(f)) kept.Add(m.Faces[f]);
            }
            m.Faces = kept;
            fmap = fmap.Select(f => f < 0 ? -1 : after[f]).ToArray();
        }

        int dissolved = DissolveStraightVertices(m, m.Faces.SelectMany(f => f.Verts).ToHashSet());
        var compact = RemoveUnusedVertices(m);
        vmap = vmap.Select(v => v < 0 ? -1 : compact[v]).ToArray();
        // Faces gone to welding or to having no area.
        return new CleanUpResult(vmap, fmap, welded, untangled, tJunctions, dissolved, fmap.Count(f => f < 0));
    }

    /// <summary>
    /// Splits faces whose outline crosses or touches itself into the simple regions it winds round (lobes wound
    /// backwards are dropped; a pinch becomes two faces). The first region keeps the face's index; the rest are
    /// appended. Where the outline crossed itself a shared vertex is added, inserted into the neighbours along those
    /// edges too. Faces that are already simple are left alone.
    /// </summary>
    public static (int Untangled, List<int> NewFaces) UntangleFaces(PolyMesh m, IEnumerable<int> faces)
    {
        var points = new EdgePoints(m);
        var pieces = new List<(int Face, List<int[]> Loops, Face Original, Vector3[] Corners)>();
        foreach (int f in faces.Distinct().Order())
        {
            var face = m.Faces[f];
            if (face.Verts.Distinct().Count() < 3 || FaceIssue(m, f) is not (MeshIssueKind.RepeatedVertex or MeshIssueKind.SelfIntersectingFace)) continue;
            var frame = new FaceFrame(m, f);
            var loop = frame.Loop(m, f);
            if (PlanarSplit.Split(loop, [], 10 * FaceFrame.Epsilon(loop, frame.Origin)) is not { Regions.Count: > 0 } split) continue;
            if (PieceLoops(m, f, frame, split, points) is not { } loops) continue;
            pieces.Add((f, loops, face.Clone(), face.Verts.Select(v => m.Positions[v]).ToArray()));
        }
        var added = new List<int>();
        WritePieces(m, pieces, points, added);
        return (pieces.Count, added);
    }

    /// <summary>Puts each T-junction vertex into the open edge it lies on. Returns how many were sewn.</summary>
    static int SewTJunctions(PolyMesh m, float epsilon)
    {
        var found = TJunctions(m, epsilon);
        if (found.Count == 0) return 0;
        var points = new EdgePoints(m);
        foreach (var (_, a, b, v, t) in found) points.Register(v, a, b, t);
        points.InsertIntoFaces();
        points.SplitSoftEdges();
        return found.Count;
    }
}
