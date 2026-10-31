using Chip.Core.Geometry;

namespace Chip.Core.Scene;

/// <summary>
/// What linked copies share (a prop, a hero asset): meshes in the definition's own space, where the copy's origin is
/// the world origin. Each copy is a <see cref="Group"/> whose <see cref="Group.Instance"/> names the definition and whose
/// <see cref="Group.Origin"/> places it; its members are ordinary world-space meshes, built from the parts, each tagged
/// with its part's <see cref="DefinitionPart.Key"/> (<see cref="PolyMesh.Part"/>). Editing any copy rewrites the
/// definition and rebuilds the others (<see cref="InstanceSync"/>).
/// Immutable: a change makes a new definition (and a new <see cref="Document.Definitions"/> list), so undo snapshots
/// share them freely.
/// </summary>
public sealed record InstanceDefinition(int Id, string Name, IReadOnlyList<DefinitionPart> Parts)
{
    /// <summary>
    /// The prefab file this definition comes from, or null for one that lives only in this level. The level keeps the
    /// parts too, so a missing file still draws what it last was; when the file is there it wins (<see cref="Prefabs"/>).
    /// </summary>
    public PrefabRef? Source { get; init; }
}

/// <summary>
/// Where a prefab is: its path relative to the project root (forward slashes) and the id stored in the file, which
/// finds it again after it was moved or renamed.
/// </summary>
public sealed record PrefabRef(string Path, string Guid);

/// <summary>What makes a <c>.chip</c> file a prefab: its id (for references) and the name copies of it take.</summary>
public sealed record PrefabHeader(string Guid, string Name);

/// <summary>One mesh of a definition, in the definition's space. <see cref="Mesh"/> is never changed once made.</summary>
public sealed record DefinitionPart(int Key, PolyMesh Mesh);
