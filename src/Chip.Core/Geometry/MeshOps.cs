using System.Numerics;
using Chip.Core.Texturing;

namespace Chip.Core.Geometry;

/// <summary>All topology-changing mesh edits. Face indices stay stable unless an op says otherwise.</summary>
public static class MeshOps
{
    public static PolyMesh CreateBox(Vector3 a, Vector3 b, string material, TextureMapping scaleFrom)
    {
        var min = Vector3.Min(a, b);
        var max = Vector3.Max(a, b);
        var m = new PolyMesh { Name = "block" };
        for (int i = 0; i < 8; i++)
            m.Positions.Add(new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z));
        int[][] loops =
        [
            [0, 4, 6, 2], // -X
            [1, 3, 7, 5], // +X
            [0, 1, 5, 4], // -Y
            [2, 6, 7, 3], // +Y
            [0, 2, 3, 1], // -Z
            [4, 5, 7, 6], // +Z
        ];
        foreach (var l in loops) m.Faces.Add(new Face(l, material, default));
        for (int f = 0; f < m.Faces.Count; f++)
            m.Faces[f].Map = TextureMapping.AlignToGrid(m.FaceNormal(f), scaleFrom);
        return m;
    }

    /// <summary>
    /// Extrudes a face region: the selected faces move onto duplicated vertices and every region border
    /// edge gets a bridging quad. Selected face indices are unchanged. Returns the vertices the caller should move
    /// and the new side faces, which have zero area until moved; world-align them afterwards with <see cref="AlignToGrid"/>.
    /// </summary>
    public static (HashSet<int> MoveVerts, List<int> SideFaces) ExtrudeFaces(PolyMesh m, IReadOnlyCollection<int> faces)
    {
        var region = faces.ToHashSet();
        var directed = new HashSet<(int, int)>();
        foreach (var f in region)
        {
            var v = m.Faces[f].Verts;
            for (int i = 0; i < v.Length; i++) directed.Add((v[i], v[(i + 1) % v.Length]));
        }

        var remap = new Dictionary<int, int>();
        int Dup(int v)
        {
            if (!remap.TryGetValue(v, out var nv))
            {
                nv = m.Positions.Count;
                m.Positions.Add(m.Positions[v]);
                remap[v] = nv;
            }
            return nv;
        }

        var sides = new List<Face>();
        foreach (var f in region)
        {
            var face = m.Faces[f];
            var v = face.Verts;
            for (int i = 0; i < v.Length; i++)
            {
                int a = v[i], b = v[(i + 1) % v.Length];
                if (directed.Contains((b, a))) continue; // interior edge of the region
                sides.Add(new Face([a, b, Dup(b), Dup(a)], face.Material, face.Map));
            }
        }
        foreach (var f in region)
            m.Faces[f].Verts = m.Faces[f].Verts.Select(Dup).ToArray();
        var sideIndices = new List<int>();
        foreach (var s in sides)
        {
            sideIndices.Add(m.Faces.Count);
            m.Faces.Add(s);
        }
        return (remap.Values.ToHashSet(), sideIndices);
    }

    /// <summary>World-aligns the given faces (used for extrusion sides after they gain area).</summary>
    public static void AlignToGrid(PolyMesh m, IEnumerable<int> faces)
    {
        foreach (var f in faces) m.Faces[f].Map = TextureMapping.AlignToGrid(m.FaceNormal(f), m.Faces[f].Map);
    }

    /// <summary>Removes faces and any vertices left unused. Vertex indices are compacted.</summary>
    public static void DeleteFaces(PolyMesh m, IEnumerable<int> faces)
    {
        var kill = faces.ToHashSet();
        m.Faces = m.Faces.Where((_, i) => !kill.Contains(i)).ToList();
        RemoveUnusedVertices(m);
    }

    /// <summary>Compacts away vertices no face uses. Returns old→new vertex indices (-1 = removed).</summary>
    public static int[] RemoveUnusedVertices(PolyMesh m)
    {
        var used = m.Faces.SelectMany(f => f.Verts).ToHashSet();
        var remap = new int[m.Positions.Count];
        var kept = new List<Vector3>();
        for (int i = 0; i < m.Positions.Count; i++)
        {
            remap[i] = used.Contains(i) ? kept.Count : -1;
            if (used.Contains(i)) kept.Add(m.Positions[i]);
        }
        m.Positions = kept;
        foreach (var f in m.Faces) f.Verts = f.Verts.Select(v => remap[v]).ToArray();
        return remap;
    }

    /// <summary>
    /// Merges vertices that landed on each other (Hammer behaviour when dragging a vertex onto another) and drops
    /// faces that collapse below three corners. Returns old→new vertex and face maps (-1 = removed), or null if nothing merged.
    /// </summary>
    public static (int[] VertexMap, int[] FaceMap)? CollapseCoincident(PolyMesh m, float epsilon)
    {
        var weld = new int[m.Positions.Count];
        bool merged = false;
        for (int i = 0; i < weld.Length; i++)
        {
            weld[i] = i;
            for (int j = 0; j < i; j++)
                if (weld[j] == j && Vector3.DistanceSquared(m.Positions[i], m.Positions[j]) <= epsilon * epsilon)
                {
                    weld[i] = j;
                    merged = true;
                    break;
                }
        }
        if (!merged) return null;

        var faceMap = new int[m.Faces.Count];
        var kept = new List<Face>();
        for (int f = 0; f < m.Faces.Count; f++)
        {
            var loop = new List<int>();
            foreach (var v in m.Faces[f].Verts.Select(v => weld[v]))
                if (loop.Count == 0 || loop[^1] != v) loop.Add(v);
            while (loop.Count > 1 && loop[0] == loop[^1]) loop.RemoveAt(loop.Count - 1);
            if (loop.Distinct().Count() < 3) { faceMap[f] = -1; continue; }
            m.Faces[f].Verts = loop.ToArray();
            faceMap[f] = kept.Count;
            kept.Add(m.Faces[f]);
        }
        m.Faces = kept;
        var compact = RemoveUnusedVertices(m);
        return (weld.Select(w => compact[w]).ToArray(), faceMap);
    }

    public static void FlipFaces(PolyMesh m, IEnumerable<int> faces)
    {
        foreach (var f in faces)
        {
            Array.Reverse(m.Faces[f].Verts);
            m.Faces[f].Map = TextureMapping.AlignToGrid(m.FaceNormal(f), m.Faces[f].Map);
        }
    }

    /// <summary>Copies faces (with fresh vertices) into the same mesh. Returns the new face indices.</summary>
    public static List<int> DuplicateFaces(PolyMesh m, IEnumerable<int> faces)
    {
        var remap = new Dictionary<int, int>();
        var added = new List<int>();
        foreach (var f in faces.ToList())
        {
            var src = m.Faces[f];
            var verts = src.Verts.Select(v =>
            {
                if (!remap.TryGetValue(v, out var nv)) { nv = m.Positions.Count; m.Positions.Add(m.Positions[v]); remap[v] = nv; }
                return nv;
            }).ToArray();
            added.Add(m.Faces.Count);
            m.Faces.Add(new Face(verts, src.Material, src.Map));
        }
        return added;
    }

    public static void SnapToGrid(PolyMesh m, IEnumerable<int> verts, float grid)
    {
        foreach (var v in verts)
        {
            var p = m.Positions[v];
            m.Positions[v] = new Vector3(Snap(p.X, grid), Snap(p.Y, grid), Snap(p.Z, grid));
        }
    }

    public static float Snap(float x, float grid) => MathF.Round(x / grid) * grid;
}
