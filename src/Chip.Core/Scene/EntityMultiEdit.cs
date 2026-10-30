using System.Numerics;
using System.Text.Json;

namespace Chip.Core.Scene;

/// <summary>
/// What several selected entities have in common, so the inspector can edit them together: the class properties they
/// all have, the values outside their classes, and which values differ ("mixed"). One entity is the simple case of the
/// same rules: all its class's properties, nothing mixed.
/// </summary>
public static class EntityMultiEdit
{
    /// <summary>
    /// The properties every entity's class declares under the same name and type, in the first class's order. Empty when
    /// any class is one the project doesn't define (it has no properties to share).
    /// </summary>
    public static List<PropSpec> CommonProps(IReadOnlyList<Entity> entities)
    {
        var classes = entities.Select(e => e.ClassName).Distinct().Select(EntityClasses.Get).ToList();
        if (classes.Count == 0 || classes.Any(c => !c.Known)) return [];
        return classes[0].Props
            .Where(p => classes.Skip(1).All(c => c.Prop(p.Name) is { } q && q.Type == p.Type && q.ItemType == p.ItemType))
            .ToList();
    }

    /// <summary>The entity's own class's spec for a shared property: its own default, range and choices.</summary>
    public static PropSpec SpecFor(Entity e, PropSpec shared) => EntityClasses.Get(e.ClassName).Prop(shared.Name) ?? shared;

    /// <summary>The values an entity sets outside its class: every one for a class the project doesn't define, else the ones it doesn't declare.</summary>
    public static IEnumerable<string> FreeKeys(Entity e)
    {
        var c = EntityClasses.Get(e.ClassName);
        return e.Properties.Keys.Where(k => !c.Known || c.Prop(k) == null);
    }

    /// <summary>The <see cref="FreeKeys(Entity)"/> of any of the entities, sorted.</summary>
    public static List<string> FreeKeys(IReadOnlyList<Entity> entities) =>
        entities.SelectMany(FreeKeys).Distinct().Order(StringComparer.Ordinal).ToList();

    /// <summary>Whether the values aren't all the same.</summary>
    public static bool Mixed(IReadOnlyList<JsonElement> values) => values.Skip(1).Any(v => !EntityValues.Same(v, values[0]));

    /// <summary>Per component, whether the vectors differ there.</summary>
    public static bool[] MixedAxes(IReadOnlyList<Vector3> values) =>
    [
        values.Any(v => v.X != values[0].X), values.Any(v => v.Y != values[0].Y), values.Any(v => v.Z != values[0].Z),
    ];

    /// <summary>Per component, whether the vector values (<paramref name="n"/> long) differ there. A value that isn't a vector reads as zeros.</summary>
    public static bool[] MixedAxes(IReadOnlyList<JsonElement> values, int n)
    {
        var vectors = values.Select(v => EntityValues.Vector(v, n) ?? new float[n]).ToList();
        var result = new bool[n];
        for (int a = 0; a < n; a++) result[a] = vectors.Any(v => v[a] != vectors[0][a]);
        return result;
    }

    /// <summary>
    /// The vector with one component replaced and the others kept, so typing X for several entities lines them up on X
    /// and leaves each one's Y and Z.
    /// </summary>
    public static JsonElement WithAxis(JsonElement vector, int n, int axis, float value)
    {
        var v = EntityValues.Vector(vector, n) ?? new float[n];
        v[axis] = value;
        return EntityValues.Array(v);
    }

    /// <summary>The vector with one component replaced.</summary>
    public static Vector3 WithAxis(Vector3 v, int axis, float value) =>
        axis == 0 ? v with { X = value } : axis == 1 ? v with { Y = value } : v with { Z = value };
}
