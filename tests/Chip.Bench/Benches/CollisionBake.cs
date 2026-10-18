using Chip.Core.Geometry;

namespace Chip.Bench.Benches;

/// <summary>
/// Export-time collision bake of the whole stress level: every mesh, every face, default options. Setup prints the
/// bake counters once so the numbers sit next to the timing.
/// </summary>
sealed class CollisionBakeBench : IBench
{
    List<CollisionSource> _sources = null!;

    public string Name => "export/collision-bake";

    public void Setup()
    {
        _sources = Stress.Doc.Meshes.Select(m => new CollisionSource(m, Enumerable.Range(0, m.Faces.Count).ToArray())).ToList();
        var s = CollisionBake.Bake(_sources, new CollisionBakeOptions()).Stats;
        Console.WriteLine($"  collision bake: {s.InputTriangles} -> {s.OutputTriangles} triangles, {s.ConvexPieces} convex pieces, " +
                          $"{s.Chunks} chunks, {s.HiddenFacesRemoved} hidden faces removed, {s.MergedFaces} faces merged");
    }

    public void Run() => CollisionBake.Bake(_sources, new CollisionBakeOptions());
}
