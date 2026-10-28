using System.Numerics;

namespace ToyGame;

/// <summary>
/// A class the level designer can place. These types are the entity definitions: <c>ToyGame manifest</c> writes them
/// to <c>chip/entities.json</c>, which Chip reads, and <see cref="Level"/> builds them back from an exported level.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class EntityAttribute(string name) : Attribute
{
    public string Name => name;
    public string Doc { get; init; } = "";
    /// <summary>The box Chip draws: min x, y, z then max x, y, z, in metres.</summary>
    public float[]? Bounds { get; init; }
    /// <summary>Made of level geometry (tie meshes to it in Chip) instead of placed as a point.</summary>
    public bool Mesh { get; init; }
}

/// <summary>A field the level sets. Fields without it stay runtime state.</summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class PropAttribute : Attribute
{
    public string Doc { get; init; } = "";
    public double Min { get; init; } = double.NaN;
    public double Max { get; init; } = double.NaN;
    public string Suffix { get; init; } = "";
    public string Group { get; init; } = "";
    /// <summary>A string field holding a path under <c>assets/</c>.</summary>
    public bool Asset { get; init; }
}

public abstract class GameEntity
{
    public int Id;
    public string Name = "";
    public Vector3 Position;
    public Quaternion Rotation = Quaternion.Identity;
    /// <summary>A mesh entity's colliders (its geometry moves with it).</summary>
    public List<Collider> Colliders = [];
}

[Entity("screen", Mesh = true, Doc = "A screen that plays a video on its faces painted with the video surface.")]
public sealed class Screen : GameEntity
{
    [Prop(Doc = "What it plays.", Asset = true)] public string Video = "assets/videos/intro.webm";
    [Prop] public bool Loop = true;
}

[Entity("player_start", Doc = "Where the player appears.", Bounds = [-0.3f, 0, -0.3f, 0.3f, 1.8f, 0.3f])]
public sealed class PlayerStart : GameEntity
{
    [Prop(Doc = "Which team spawns here.", Min = 1, Max = 4)] public int Team = 1;
}

[Entity("pickup", Doc = "Something the player collects by walking into it.", Bounds = [-0.25f, 0, -0.25f, 0.25f, 0.5f, 0.25f])]
public sealed class Pickup : GameEntity
{
    [Prop(Doc = "Item id.")] public string Item = "coin";
    [Prop(Min = 1, Max = 99)] public int Amount = 1;
    [Prop(Doc = "Comes back 30 s after it's taken.")] public bool Respawns;
}

public enum DoorMode { Swing, Slide }

[Entity("door", Doc = "A door that opens when the player comes near.", Bounds = [-0.5f, 0, -0.05f, 0.5f, 2, 0.05f])]
public sealed class Door : GameEntity
{
    [Prop] public DoorMode Mode;
    [Prop(Min = 0, Max = 180, Suffix = "°")] public float OpenAngle = 90;
    [Prop(Doc = "Opens together with this door.")] public Door? Partner;
    [Prop(Doc = "Played when it opens.", Asset = true)] public string Sound = "assets/sounds/door.wav";
    [Prop(Doc = "Any of these keys unlocks it; none means it's never locked.", Group = "Lock")] public List<string> Keys = [];
}

[Entity("light", Doc = "A point light.")]
public sealed class Light : GameEntity
{
    [Prop] public Vector4 Color = Vector4.One;
    [Prop(Min = 0, Suffix = " m")] public float Range = 8;
    [Prop(Doc = "Lights that switch on and off with this one.")] public List<Light> Linked = [];
}
