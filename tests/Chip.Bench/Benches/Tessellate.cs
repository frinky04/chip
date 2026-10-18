using System.Numerics;
using Chip.Core.Geometry;

namespace Chip.Bench.Benches;

/// <summary>
/// Export tessellation of every stress-level mesh (what a vertex-lit Godot export pays). Corner normals are computed
/// in setup, as the exporter already has them. Setup prints triangles in and out.
/// </summary>
abstract class TessellateBench(float spacing) : IBench
{
    readonly TessellateOptions _options = new(spacing);
    Vector3[][][] _normals = [];
    int[][] _faces = [];

    public abstract string Name { get; }

    public void Setup()
    {
        var meshes = Stress.Doc.Meshes;
        _normals = meshes.Select(Topology.CornerNormals).ToArray();
        _faces = meshes.Select(m => Enumerable.Range(0, m.Faces.Count).ToArray()).ToArray();
        long trisIn = meshes.Sum(m => m.Faces.Sum(f => (long)Math.Max(0, f.Verts.Length - 2)));
        long trisOut = 0;
        for (int i = 0; i < meshes.Count; i++) trisOut += Tessellate(i).TriangleCount;
        Console.WriteLine($"  {Name}: {trisIn:N0} triangles in, {trisOut:N0} out");
    }

    public void Run()
    {
        for (int i = 0; i < Stress.Doc.Meshes.Count; i++) Tessellate(i);
    }

    TessellatedMesh Tessellate(int i) => Tessellator.Tessellate(Stress.Doc.Meshes[i], _faces[i], _normals[i], _options);
}

sealed class TessellateBench1m() : TessellateBench(1f)
{
    public override string Name => "export/tessellate-all-1m";
}

/// <summary>The dense end of what a retro vertex-lit look wants.</summary>
sealed class TessellateBench25cm() : TessellateBench(0.25f)
{
    public override string Name => "export/tessellate-all-25cm";
}
