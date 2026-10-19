using Chip.Core.Geometry;
using Chip.Core.Scene;

namespace Chip.Core.Dressing;

/// <summary>
/// Bake: the escape hatch for hand work. A mesh's dressing becomes ordinary geometry you can edit, and the mesh stops
/// following the rules (every rule is switched off for it). Run inside one undo step.
/// <list type="bullet">
/// <item>A bevelled (modified) mesh takes its bevelled shape.</item>
/// <item>A proxy's build becomes a new mesh; the box stays as collision only, or goes if it didn't collide.</item>
/// <item>Everything dress rules added from it (trims, props) becomes new meshes.</item>
/// </list>
/// </summary>
public static class DressingBake
{
    /// <summary>The meshes made and the sources baked.</summary>
    public sealed record Result(List<int> Added, List<int> Baked, List<int> Removed);

    /// <summary>Whether any of <paramref name="sourceIds"/> has dressing to bake.</summary>
    public static bool CanBake(DressedLevel level, IEnumerable<int> sourceIds)
    {
        var ids = sourceIds.ToHashSet();
        return level.Meshes.Any(m => level.Origin(m) is { Role: not DressRole.Passthrough } o && ids.Contains(o.SourceId));
    }

    public static Result Bake(Document doc, DressedLevel level, IEnumerable<int> sourceIds)
    {
        var result = new Result([], [], []);
        foreach (var id in sourceIds.Distinct().ToList())
        {
            if (doc.Find(id) is not { } source) continue;
            var outputs = level.Meshes.Where(m => level.Origin(m) is { Role: not DressRole.Passthrough } o && o.SourceId == id).ToList();
            if (outputs.Count == 0) continue;
            bool remove = false;
            foreach (var output in outputs)
            {
                var origin = level.Origin(output);
                switch (origin.Role)
                {
                    case DressRole.Modified:
                        source.Positions = new List<System.Numerics.Vector3>(output.Positions);
                        source.Faces = output.Faces.Select(f => f.Clone()).ToList();
                        source.SoftEdges = new HashSet<Edge>(output.SoftEdges);
                        break;
                    case DressRole.Proxy:
                        result.Added.Add(Add(doc, output, $"{source.Name}_{origin.Rule}", CollisionMode.None));
                        if (source.Collision == CollisionMode.None || (source.Collision == CollisionMode.Default && !doc.ExportCollision)) remove = true;
                        else source.Collision = CollisionMode.Only; // the box keeps colliding, unseen
                        break;
                    default:
                        result.Added.Add(Add(doc, output, $"{origin.Rule}_{source.Name}", CollisionMode.None));
                        break;
                }
            }
            if (remove)
            {
                doc.Meshes.Remove(source);
                result.Removed.Add(id);
            }
            else source.Dressing = source.Dressing.WithSkip(MeshDressing.AllRules, true);
            result.Baked.Add(id);
        }
        return result;
    }

    static int Add(Document doc, PolyMesh output, string name, CollisionMode collision)
    {
        var copy = output.Clone();
        copy.Name = name;
        copy.Locked = false;
        copy.Collision = collision;
        copy.Tags = default;
        copy.Dressing = MeshDressing.Empty;
        return doc.Add(copy).Id;
    }
}
