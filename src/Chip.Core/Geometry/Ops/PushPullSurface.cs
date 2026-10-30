using System.Numerics;

namespace Chip.Core.Geometry;

public static partial class MeshOps
{
    // Resolve a planar arrangement, rather than merging face loops by vertex ids. Sweeps can cross seams, consume
    // whole tiles, meet a subdivided edge, or leave several islands. Every retained cell keeps its source surface.
    static MergeFacesResult? ResolvePushPullSurfaces(PolyMesh m, IReadOnlyCollection<int> sides, IReadOnlySet<int> pushed)
    {
        var sideSet = sides.ToHashSet();
        var remaining = sides.Where(f => Topology.FaceArea(m, f) > 1e-8f).ToHashSet();
        var replacements = new Dictionary<int, List<Face>>();
        var originalPositions = m.Positions.ToArray();
        var points = new EdgePoints(m);
        var originalEdges = m.Edges().ToArray();
        var surfaceGroups = new List<List<int>>();

        while (remaining.Count > 0)
        {
            int seed = remaining.First();
            var frame = new FaceFrame(m, seed);
            bool OnPlane(int f) => MathF.Abs(Vector3.Dot(m.FaceNormal(f), frame.Normal)) > 0.9999f &&
                m.FacePoints(f).All(p => MathF.Abs(Vector3.Dot(p - frame.Origin, frame.Normal)) < 1e-4f);
            var strips = remaining.Where(OnPlane).ToArray();
            remaining.ExceptWith(strips);
            var stripLoops = strips.Select(f => frame.Loop(m, f)).ToArray();
            var originals = Enumerable.Range(0, m.Faces.Count)
                .Where(f => !sideSet.Contains(f) && !pushed.Contains(f) && OnPlane(f))
                .Where(f => stripLoops.Any(s => PushPullLoopsTouch(frame.Loop(m, f), s, 1e-5))).ToArray();
            if (originals.Length == 0) continue;

            // The existing surface supplies the orientation, including for inward-facing room shells.
            frame = new FaceFrame(m, originals[0]);
            var sourceIds = originals.Concat(strips).ToArray();
            var loops = sourceIds.ToDictionary(f => f, f => frame.Loop(m, f));
            var all = loops.Values.SelectMany(l => l).ToArray();
            double eps = Math.Max(1e-7, FaceFrame.Epsilon(all, frame.Origin));
            double minX = all.Min(p => p.X), maxX = all.Max(p => p.X);
            double minY = all.Min(p => p.Y), maxY = all.Max(p => p.Y);
            double margin = Math.Max(maxX - minX, maxY - minY) * 0.1 + eps * 100;
            P2[] bounds = [new(minX - margin, minY - margin), new(maxX + margin, minY - margin),
                new(maxX + margin, maxY + margin), new(minX - margin, maxY + margin)];
            var cuts = loops.Values.SelectMany(l => l.Select((p, i) => (p, l[(i + 1) % l.Length])));
            if (PlanarSplit.Split(bounds, cuts, eps) is not { } split) continue;
            var pieces = sourceIds.ToDictionary(f => f, _ => new List<Face>());
            var nodeVertices = new Dictionary<int, int>();
            int Vertex(int node)
            {
                if (nodeVertices.TryGetValue(node, out int found)) return found;
                var p = frame.To3(split.Nodes[node].P);
                float tolerance = (float)Math.Max(eps * 2, 1e-5);
                int v = m.Positions.FindIndex(q => Vector3.DistanceSquared(p, q) <= tolerance * tolerance);
                if (v < 0) { v = m.Positions.Count; m.Positions.Add(p); }
                foreach (var e in originalEdges)
                {
                    var a = originalPositions[e.A];
                    var d = originalPositions[e.B] - a;
                    float len2 = d.LengthSquared();
                    if (len2 <= tolerance * tolerance) continue;
                    float t = Vector3.Dot(p - a, d) / len2;
                    if (t > 0 && t < 1 && Vector3.DistanceSquared(a + t * d, p) <= tolerance * tolerance)
                        points.Register(v, e.A, e.B, t);
                }
                return nodeVertices[node] = v;
            }
            foreach (var region in split.Regions)
            {
                var polygon = region.Select(n => split.Nodes[n].P).ToArray();
                if (PushPullInterior(polygon, eps) is not { } sample) continue;
                int winding = sourceIds.Sum(f => P2.Winding(loops[f], sample));
                if (winding <= 0) continue;
                int owner = originals.FirstOrDefault(f => P2.Winding(loops[f], sample) > 0, -1);
                if (owner < 0)
                {
                    // A new extension inherits the closest existing surface, rather than the pushed face's material.
                    owner = originals.MinBy(f => loops[f].Select((p, i) =>
                        P2.SegmentDistance(sample, p, loops[f][(i + 1) % loops[f].Length])).Min());
                }
                var source = m.Faces[owner];
                var face = source.Derive(region.Select(Vertex).ToArray());
                face.Dominant = source.Dominant;
                if (source.UVs is { } uv)
                    face.UVs = face.Verts.Select(v => InterpolateUV(m.FacePoints(owner).ToArray(), uv, m.Positions[v])).ToArray();
                pieces[owner].Add(face);
            }
            foreach (int f in sourceIds) replacements[f] = pieces[f];
        }
        if (replacements.Count == 0) return null;

        var faceMap = new int[m.Faces.Count];
        var output = new List<Face>();
        for (int f = 0; f < m.Faces.Count; f++)
        {
            if (!replacements.TryGetValue(f, out var pieces))
            {
                faceMap[f] = output.Count;
                output.Add(m.Faces[f]);
                continue;
            }
            faceMap[f] = pieces.Count == 0 ? -1 : output.Count;
            var group = new List<int>();
            foreach (var piece in pieces) { group.Add(output.Count); output.Add(piece); }
            if (group.Count > 1) surfaceGroups.Add(group);
        }
        m.Faces = output;
        points.InsertIntoFaces();
        points.SplitSoftEdges();
        // The arrangement preserves every seam. Dissolve only the temporary cell boundaries within one source face.
        var merged = MergeFaceGroups(m, surfaceGroups);
        var clean = CleanUp(m, 1e-5f);
        return new MergeFacesResult(
            Enumerable.Range(0, originalPositions.Length).Select(v =>
                merged.VertexMap[v] < 0 ? -1 : clean.VertexMap[merged.VertexMap[v]]).ToArray(),
            faceMap.Select(f => f < 0 || merged.FaceMap[f] < 0 ? -1 : clean.FaceMap[merged.FaceMap[f]]).ToArray(), [], merged.Skipped);
    }

    static P2? PushPullInterior(IReadOnlyList<P2> loop, double eps)
    {
        var triangles = new List<int>();
        Triangulator.Triangulate(loop, eps, triangles);
        double best = 0;
        P2? sample = null;
        for (int i = 0; i + 2 < triangles.Count; i += 3)
        {
            var a = loop[triangles[i]]; var b = loop[triangles[i + 1]]; var c = loop[triangles[i + 2]];
            double area = P2.Cross(b - a, c - a);
            if (area > best) { best = area; sample = (a + b + c) * (1.0 / 3); }
        }
        return sample;
    }

    static bool PushPullLoopsTouch(IReadOnlyList<P2> a, IReadOnlyList<P2> b, double eps)
    {
        if (a.Max(p => p.X) < b.Min(p => p.X) - eps || b.Max(p => p.X) < a.Min(p => p.X) - eps ||
            a.Max(p => p.Y) < b.Min(p => p.Y) - eps || b.Max(p => p.Y) < a.Min(p => p.Y) - eps) return false;
        if (a.Any(p => P2.Winding(b, p) != 0) || b.Any(p => P2.Winding(a, p) != 0)) return true;
        for (int i = 0; i < a.Count; i++)
            for (int j = 0; j < b.Count; j++)
                if (SegmentsTouch(a[i], a[(i + 1) % a.Count], b[j], b[(j + 1) % b.Count], eps)) return true;
        return false;
    }
}
