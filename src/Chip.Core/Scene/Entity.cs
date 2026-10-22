using System.Numerics;
using System.Text.Json;

namespace Chip.Core.Scene;

/// <summary>
/// A point entity (player start, light, an engine scene). A class name from <see cref="EntityClasses"/> plus the property
/// values this entity sets (typed JSON, see <see cref="EntityValues"/>); a property it doesn't set has its class default.
/// Exported as an empty glTF node with the values in <c>extras</c>.
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

    public Entity Clone() => new()
    {
        Id = Id, ClassName = ClassName, Name = Name, Position = Position, Rotation = Rotation, Hidden = Hidden, Locked = Locked,
        Properties = new Dictionary<string, JsonElement>(Properties),
    };
}
