using System.Numerics;
using Chip.Core.Picking;

namespace Chip.Core.Scene;

/// <summary>
/// A built-in point entity class: its display name, the box drawn and picked around the origin (entity space, metres)
/// and the key-values a new entity starts with.
/// </summary>
public sealed record EntityClass(string Name, string Label, Vector3 BoxMin, Vector3 BoxMax, IReadOnlyDictionary<string, string> Defaults);

/// <summary>The classes the entity tool offers. Any other class name is a generic point entity.</summary>
public static class EntityClasses
{
    static readonly Dictionary<string, string> None = new();

    public static readonly IReadOnlyList<EntityClass> BuiltIn =
    [
        // Origin at the feet, like Source and Godot character scenes; about a person's size.
        new("info_player_start", "Player start", new(-0.3f, 0, -0.3f), new(0.3f, 1.8f, 0.3f), None),
        new("light_point", "Point light", new(-0.15f), new(0.15f), new Dictionary<string, string>
        {
            ["color"] = "255 255 255", ["energy"] = "1", ["range"] = "10",
        }),
        new("light_spot", "Spot light", new(-0.15f), new(0.15f), new Dictionary<string, string>
        {
            ["color"] = "255 255 255", ["energy"] = "1", ["range"] = "10", ["angle"] = "45",
        }),
        new("info_target", "Target", new(-0.15f), new(0.15f), None),
        new("prop_marker", "Prop marker", new(-0.25f, 0, -0.25f), new(0.25f, 0.5f, 0.25f), new Dictionary<string, string> { ["model"] = "" }),
        new("trigger_marker", "Trigger marker", new(-0.5f), new(0.5f), new Dictionary<string, string> { ["event"] = "" }),
    ];

    /// <summary>Any class name not in <see cref="BuiltIn"/>.</summary>
    public static EntityClass Generic(string name) => new(name, name, new(-0.2f), new(0.2f), None);

    public static EntityClass Get(string className) =>
        BuiltIn.FirstOrDefault(c => c.Name == className) ?? Generic(className);

    public static bool IsBuiltIn(string className) => BuiltIn.Any(c => c.Name == className);

    /// <summary>A new entity of the class with its default key-values (id assigned by <see cref="Document.Add(Entity)"/>).</summary>
    public static Entity Create(string className, Vector3 position, float yawDegrees = 0) => new()
    {
        ClassName = className, Position = position, Rotation = new Vector3(0, yawDegrees, 0),
        Properties = new Dictionary<string, string>(Get(className).Defaults),
    };
}

/// <summary>Entity orientation, frames and hit-testing. Forward is -Z (glTF and Godot), up is +Y.</summary>
public static class EntityOps
{
    const float Deg = MathF.PI / 180;

    /// <summary>
    /// <see cref="Entity.Rotation"/> as a quaternion: yaw about Y, pitch about X, roll about Z, composed so roll applies first
    /// in the object's frame (Godot's YXZ order).
    /// </summary>
    public static Quaternion Orientation(Vector3 eulerDegrees) =>
        Quaternion.CreateFromYawPitchRoll(eulerDegrees.Y * Deg, eulerDegrees.X * Deg, eulerDegrees.Z * Deg);

    /// <summary>Inverse of <see cref="Orientation"/>, in degrees, rounded to a thousandth so typed values stay clean.</summary>
    public static Vector3 ToEuler(Quaternion q)
    {
        var m = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(q));
        // m is row-vector (v * m); R = transpose(m) in column form, R = Ry * Rx * Rz.
        float sx = Math.Clamp(-m.M32, -1, 1);
        float x = MathF.Asin(sx), y, z;
        if (MathF.Abs(sx) < 0.99999f)
        {
            y = MathF.Atan2(m.M31, m.M33);
            z = MathF.Atan2(m.M12, m.M22);
        }
        else
        {
            y = MathF.Atan2(-m.M13, m.M11);
            z = 0;
        }
        static float Clean(float rad)
        {
            float d = MathF.Round(rad / Deg * 1000) / 1000;
            return d == 0 ? 0 : d; // no negative zero in the inspector
        }
        return new Vector3(Clean(x), Clean(y), Clean(z));
    }

    public static Quaternion Orientation(Entity e) => Orientation(e.Rotation);

    // ---- Edits (run inside a Session step) ----

    /// <summary>Changes the class, adding the new class's default keys the entity doesn't have yet (existing values stay).</summary>
    public static void SetClass(Entity e, string className)
    {
        e.ClassName = className.Trim();
        foreach (var (k, v) in EntityClasses.Get(e.ClassName).Defaults) e.Properties.TryAdd(k, v);
    }

    /// <summary>Sets or adds a key-value. Returns an error message (and changes nothing) when the key is blank.</summary>
    public static string? SetProperty(Entity e, string key, string value)
    {
        key = key.Trim();
        if (key.Length == 0) return "Type a key for the property";
        e.Properties[key] = value;
        return null;
    }

    /// <summary>Renames a key, keeping its value. Returns an error message (and changes nothing) when the new key is blank or taken.</summary>
    public static string? RenameProperty(Entity e, string oldKey, string newKey)
    {
        newKey = newKey.Trim();
        if (newKey == oldKey) return null;
        if (newKey.Length == 0) return "A property key can't be empty. Use the remove button to delete it";
        if (e.Properties.ContainsKey(newKey)) return $"There is already a property called {newKey}";
        if (!e.Properties.Remove(oldKey, out var value)) return $"There is no property called {oldKey}";
        e.Properties[newKey] = value;
        return null;
    }

    /// <summary>Entity space to world.</summary>
    public static Matrix4x4 World(Entity e) => Matrix4x4.CreateFromQuaternion(Orientation(e)) * Matrix4x4.CreateTranslation(e.Position);

    public static Vector3 Forward(Entity e) => Vector3.Transform(-Vector3.UnitZ, Orientation(e));

    public static Vector3 Up(Entity e) => Vector3.Transform(Vector3.UnitY, Orientation(e));

    /// <summary>World-space centre of the class box (where the glyph is drawn).</summary>
    public static Vector3 Center(Entity e)
    {
        var c = EntityClasses.Get(e.ClassName);
        return Vector3.Transform((c.BoxMin + c.BoxMax) * 0.5f, World(e));
    }

    /// <summary>The class box's eight corners in world space (bit 0 = x, bit 1 = y, bit 2 = z).</summary>
    public static Vector3[] BoxCorners(Entity e)
    {
        var c = EntityClasses.Get(e.ClassName);
        var w = World(e);
        var result = new Vector3[8];
        for (int i = 0; i < 8; i++)
            result[i] = Vector3.Transform(new Vector3((i & 1) == 0 ? c.BoxMin.X : c.BoxMax.X, (i & 2) == 0 ? c.BoxMin.Y : c.BoxMax.Y,
                (i & 4) == 0 ? c.BoxMin.Z : c.BoxMax.Z), w);
        return result;
    }

    /// <summary>Distance along the ray to the entity's oriented class box (slab test in entity space), or null on a miss.</summary>
    public static float? RayHit(Entity e, Ray ray)
    {
        var c = EntityClasses.Get(e.ClassName);
        var inv = Quaternion.Conjugate(Orientation(e));
        var o = Vector3.Transform(ray.Origin - e.Position, inv);
        var d = Vector3.Transform(ray.Dir, inv);
        float tmin = 0, tmax = float.MaxValue;
        for (int a = 0; a < 3; a++)
        {
            float oa = a == 0 ? o.X : a == 1 ? o.Y : o.Z, da = a == 0 ? d.X : a == 1 ? d.Y : d.Z;
            float lo = a == 0 ? c.BoxMin.X : a == 1 ? c.BoxMin.Y : c.BoxMin.Z, hi = a == 0 ? c.BoxMax.X : a == 1 ? c.BoxMax.Y : c.BoxMax.Z;
            if (MathF.Abs(da) < 1e-9f)
            {
                if (oa < lo || oa > hi) return null;
                continue;
            }
            float t1 = (lo - oa) / da, t2 = (hi - oa) / da;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tmin = MathF.Max(tmin, t1);
            tmax = MathF.Min(tmax, t2);
            if (tmin > tmax) return null;
        }
        return tmin;
    }
}
