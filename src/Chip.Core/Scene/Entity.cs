using System.Numerics;
using Chip.Core.Geometry;
using System.Text.Json;

namespace Chip.Core.Scene;

/// <summary>
/// An entity (player start, light, an engine scene). A class name from <see cref="EntityClasses"/> plus the property
/// values this entity sets (typed JSON, see <see cref="EntityValues"/>); a property it doesn't set has its class default.
/// A point entity exports as an empty glTF node with the values in <c>extras</c>. A mesh entity (a door, a moving
/// platform, a screen) also owns <see cref="Meshes"/>: they move with it, and export as its children, relative to its
/// <see cref="Position"/> and <see cref="Rotation"/> (the origin, such as a door's hinge).
/// </summary>
public sealed class Entity
{
    public int Id;
    public string ClassName = "info_target";
    public string Name = "";
    public Vector3 Position;
    /// <summary>Euler angles in degrees, applied Y (yaw), then X (pitch), then Z (roll).</summary>
    public Vector3 Rotation;
    public bool Hidden;
    /// <summary>Locked entities draw but can't be picked or selected (like <c>PolyMesh.Locked</c>).</summary>
    public bool Locked;
    /// <summary>Only the values this entity sets. Elements are immutable, so a shallow copy is a full copy.</summary>
    public Dictionary<string, JsonElement> Properties = new();
    /// <summary>The meshes this entity is made of (ids). Empty for a point entity. A mesh belongs to at most one entity.</summary>
    public List<int> Meshes = new();

    public TagSet Tags;
    public List<Connection> Outputs = new();

    public bool IsMeshEntity => Meshes.Count > 0;

    public Entity Clone() => new()
    {
        Id = Id, ClassName = ClassName, Name = Name, Position = Position, Rotation = Rotation, Hidden = Hidden, Locked = Locked,
        Tags = Tags, Outputs = Outputs.Select(c => c.Clone()).ToList(),
        Properties = new Dictionary<string, JsonElement>(Properties), Meshes = new List<int>(Meshes),
    };
}
