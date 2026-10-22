using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Geometry.Primitives;
using Chip.Core.Scene;
using Chip.Core.Texturing;

namespace Chip.Core.Diagnostics;

/// <summary>
/// Deterministic large levels for benchmarks and performance tests. The default is the Phase 2 target:
/// 2,000 meshes and about 100k faces, a mix of blocks, cylinders, stairs, arches and spheres laid out on a grid
/// (some smoothed, several materials), plus point entities and a few groups.
/// </summary>
public static class StressLevel
{
    public const int TargetMeshes = 2000;

    static readonly string[] Materials = ["dev_measurewall01a", "dev_measurewall01d", "dev_measurecrate02", "clip"];

    /// <summary>Builds the level. Same arguments, same document.</summary>
    public static Document Generate(int meshes = TargetMeshes, int seed = 1, int entities = 200)
    {
        var rng = new Random(seed);
        var doc = new Document();
        var scale = TextureMapping.Default;
        int side = (int)MathF.Ceiling(MathF.Sqrt(meshes));
        const float cell = 6f;

        for (int i = 0; i < meshes; i++)
        {
            var origin = new Vector3((i % side) * cell, 0, (i / side) * cell);
            var size = new Vector3(1 + rng.NextSingle() * 3, 1 + rng.NextSingle() * 3, 1 + rng.NextSingle() * 3);
            var frame = PrimitiveFrame.FromBox(origin, origin + size, Vector3.UnitY, Vector3.UnitX);
            string material = Materials[rng.Next(Materials.Length)];
            // Weights tuned so the average lands near 50 faces a mesh (100k at 2,000 meshes).
            var (shape, opts) = rng.Next(20) switch
            {
                < 3 => (PrimitiveShape.Box, new PrimitiveOptions()),
                < 7 => (PrimitiveShape.Cylinder, new PrimitiveOptions { Sides = 16, Smooth = rng.Next(2) == 0 }),
                < 10 => (PrimitiveShape.Stairs, new PrimitiveOptions { Steps = 8 }),
                < 13 => (PrimitiveShape.Arch, new PrimitiveOptions { ArchSegments = 8, Smooth = rng.Next(2) == 0 }),
                < 18 => (PrimitiveShape.Sphere, new PrimitiveOptions { SphereRings = 8, SphereSegments = 16, Smooth = true }),
                _ => (PrimitiveShape.Torus, new PrimitiveOptions { TorusSegments = 12, TorusSides = 6, Smooth = true }),
            };
            var mesh = Primitive.Build(shape, opts, frame, material, scale);
            mesh.Name = $"{Primitive.Name(shape)} {i}";
            doc.Add(mesh);
        }

        string[] classes = ["info_player_start", "light_point", "light_spot", "info_target"];
        for (int i = 0; i < entities; i++)
        {
            var at = new Vector3(rng.NextSingle() * side * cell, rng.NextSingle() * 4, rng.NextSingle() * side * cell);
            doc.Add(EntityClasses.Create(classes[rng.Next(classes.Length)], at, rng.Next(8) * 45));
        }

        // A few groups of neighbouring meshes, so outliner and group code paths see real data.
        for (int g = 0; g < Math.Min(20, meshes / 10); g++)
        {
            var group = new Group { Name = $"group {g}" };
            for (int k = 0; k < 10; k++) group.Members.Add(doc.Meshes[g * 10 + k].Id);
            doc.Add(group);
        }
        return doc;
    }

    public static int FaceCount(Document doc) => doc.Meshes.Sum(m => m.Faces.Count);
}
