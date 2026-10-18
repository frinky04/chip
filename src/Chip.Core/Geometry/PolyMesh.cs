using System.Numerics;
using Chip.Core.Texturing;

namespace Chip.Core.Geometry;

/// <summary>A polygon: an ordered vertex loop (counter-clockwise seen from the front) with its surface.</summary>
public sealed class Face
{
    public int[] Verts;
    public string Material;
    public TextureMapping Map;

    /// <summary>
    /// Explicit per-corner UVs (one per entry in <see cref="Verts"/>, texture space where 1 = one tile).
    /// When set they override <see cref="Map"/> everywhere (render, export) and stay glued to the geometry.
    /// Unwraps and hotspots write these; ops that reorder <see cref="Verts"/> must reorder them too.
    /// </summary>
    public Vector2[]? UVs;

    public Face(int[] verts, string material, TextureMapping map)
    {
        Verts = verts;
        Material = material;
        Map = map;
    }

    public Face Clone() => new((int[])Verts.Clone(), Material, Map) { UVs = (Vector2[]?)UVs?.Clone() };

    /// <summary>UV of corner <paramref name="i"/>: explicit if present, otherwise projected.</summary>
    public Vector2 UV(int i, Vector3 position, int texWidth, int texHeight) =>
        UVs is { } uv && i < uv.Length ? uv[i] : Map.Project(position, texWidth, texHeight);

    public IEnumerable<Edge> Edges()
    {
        for (int i = 0; i < Verts.Length; i++)
            yield return new Edge(Verts[i], Verts[(i + 1) % Verts.Length]);
    }
}

/// <summary>Undirected edge between two vertex indices, normalized so A &lt; B.</summary>
public readonly record struct Edge
{
    public int A { get; }
    public int B { get; }
    public Edge(int a, int b) { A = Math.Min(a, b); B = Math.Max(a, b); }
}

/// <summary>How a mesh takes part in export collision.</summary>
public enum CollisionMode { Default, None, Only, ConvexOnly }

/// <summary>
/// Editable n-gon mesh. Vertices live in world space; edges are derived from face loops.
/// Topology changes go through <see cref="MeshOps"/>.
/// </summary>
public sealed class PolyMesh
{
    public int Id;
    public string Name = "mesh";
    public bool Hidden;
    /// <summary>Visible but not pickable.</summary>
    public bool Locked;
    /// <summary>Default follows <c>Document.ExportCollision</c>; None never collides; Only collides without rendering; ConvexOnly is Only with a convex hull collider.</summary>
    public CollisionMode Collision;
    public List<Vector3> Positions = new();
    public List<Face> Faces = new();
    /// <summary>Edges shaded smooth (Scythe "soft normals"). Everything else is hard, i.e. flat per face.</summary>
    public HashSet<Edge> SoftEdges = new();

    public PolyMesh Clone() => new()
    {
        Id = Id, Name = Name, Hidden = Hidden, Locked = Locked, Collision = Collision,
        Positions = new List<Vector3>(Positions),
        Faces = Faces.Select(f => f.Clone()).ToList(),
        SoftEdges = new HashSet<Edge>(SoftEdges),
    };

    public IEnumerable<Edge> Edges()
    {
        var seen = new HashSet<Edge>();
        foreach (var f in Faces)
            foreach (var e in f.Edges())
                if (seen.Add(e)) yield return e;
    }

    /// <summary>Edges used by exactly one face (open borders).</summary>
    public HashSet<Edge> BoundaryEdges()
    {
        var count = new Dictionary<Edge, int>();
        foreach (var f in Faces)
            foreach (var e in f.Edges())
                count[e] = count.GetValueOrDefault(e) + 1;
        return count.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToHashSet();
    }

    /// <summary>Unit normal via Newell's method; robust for non-planar and concave loops.</summary>
    public Vector3 FaceNormal(int face)
    {
        var v = Faces[face].Verts;
        var n = Vector3.Zero;
        for (int i = 0; i < v.Length; i++)
        {
            var a = Positions[v[i]];
            var b = Positions[v[(i + 1) % v.Length]];
            n.X += (a.Y - b.Y) * (a.Z + b.Z);
            n.Y += (a.Z - b.Z) * (a.X + b.X);
            n.Z += (a.X - b.X) * (a.Y + b.Y);
        }
        float len = n.Length();
        return len < 1e-9f ? Vector3.UnitY : n / len;
    }

    public Vector3 FaceCenter(int face)
    {
        var v = Faces[face].Verts;
        var c = Vector3.Zero;
        foreach (var i in v) c += Positions[i];
        return c / v.Length;
    }

    public IEnumerable<Vector3> FacePoints(int face) => Faces[face].Verts.Select(i => Positions[i]);

    public (Vector3 Min, Vector3 Max) Bounds()
    {
        if (Positions.Count == 0) return (Vector3.Zero, Vector3.Zero);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in Positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        return (min, max);
    }
}
