using System.Numerics;

namespace Chip.Core.Scene;

/// <summary>
/// A point entity (player start, light, generic marker). Engine-agnostic: a class name plus string key-values,
/// exported as an empty glTF node with the key-values in <c>extras</c>.
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
    public Dictionary<string, string> Properties = new();

    public Entity Clone() => new()
    {
        Id = Id, ClassName = ClassName, Name = Name, Position = Position, Rotation = Rotation, Hidden = Hidden,
        Properties = new Dictionary<string, string>(Properties),
    };
}
