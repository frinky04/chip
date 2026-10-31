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
public sealed record InstanceDefinition(int Id, string Name, IReadOnlyList<DefinitionPart> Parts);

/// <summary>One mesh of a definition, in the definition's space. <see cref="Mesh"/> is never changed once made.</summary>
public sealed record DefinitionPart(int Key, PolyMesh Mesh);
