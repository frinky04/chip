using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ToyGame;

/// <summary>
/// Writes Chip's entity manifest from the <see cref="EntityAttribute"/> classes, so the game's code stays the one place
/// entities are defined. Field <c>OpenAngle</c> becomes property <c>open_angle</c>; defaults come from a new instance.
/// </summary>
public static class Manifest
{
    public static IEnumerable<Type> EntityTypes() =>
        typeof(GameEntity).Assembly.GetTypes().Where(t => t.GetCustomAttribute<EntityAttribute>() != null).OrderBy(t => t.Name);

    public static string Generate()
    {
        var classes = new JsonObject();
        foreach (var type in EntityTypes())
        {
            var attr = type.GetCustomAttribute<EntityAttribute>()!;
            var instance = Activator.CreateInstance(type)!;
            var props = new JsonArray();
            foreach (var (field, prop) in Props(type)) props.Add(Spec(field, prop, field.GetValue(instance)));
            var c = new JsonObject { ["label"] = Humanize(type.Name) };
            if (attr.Doc.Length > 0) c["doc"] = attr.Doc;
            c["props"] = props;
            if (attr.Bounds is { Length: 6 } b) c["bounds"] = new JsonArray(new JsonArray(b[0], b[1], b[2]), new JsonArray(b[3], b[4], b[5]));
            if (attr.Icon != null) c["icon"] = attr.Icon;
            if (attr.Color != null) c["color"] = attr.Color;
            if (attr.Mesh) c["mesh"] = true;
            if (attr.Name is "door" or "light")
            {
                c["traits"] = new JsonArray("toggleable");
                c["inputs"] = JsonNode.Parse("""[{"name":"enable","params":[{"name":"enabled","type":"bool","default":true}]}]""");
                c["outputs"] = JsonNode.Parse("""[{"name":"changed","payload":{"type":"bool"}}]""");
            }
            if (attr.Name == "logic_relay")
            {
                c["logic"] = true;
                c["inputs"] = JsonNode.Parse("""[{"name":"trigger"}]""");
                c["outputs"] = JsonNode.Parse("""[{"name":"fired"}]""");
            }
            classes[attr.Name] = c;
        }
        var root = new JsonObject { ["version"] = 1, ["classes"] = classes };
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        return root.ToJsonString(options) + "\n";
    }

    public static IEnumerable<(FieldInfo Field, PropAttribute Prop)> Props(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Select(f => (f, f.GetCustomAttribute<PropAttribute>()!)).Where(p => p.Item2 != null).OrderBy(p => p.f.MetadataToken);

    static JsonObject Spec(FieldInfo field, PropAttribute prop, object? value)
    {
        var spec = new JsonObject { ["name"] = SnakeCase(field.Name) };
        var type = field.FieldType;
        bool list = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>);
        var item = list ? type.GetGenericArguments()[0] : type;
        string itemType = TypeName(item, prop);
        spec["type"] = list ? "array" : itemType;
        if (list) spec["of"] = itemType;
        if (item.IsEnum)
            spec["options"] = new JsonArray(Enum.GetNames(item).Select(n => (JsonNode)new JsonObject { ["label"] = n, ["value"] = n.ToLowerInvariant() }).ToArray());
        if (typeof(GameEntity).IsAssignableFrom(item) && item.GetCustomAttribute<EntityAttribute>() is { } target) spec["class"] = target.Name;
        if (!double.IsNaN(prop.Min) || !double.IsNaN(prop.Max))
        {
            spec["range"] = new JsonArray(double.IsNaN(prop.Min) ? 0 : prop.Min, double.IsNaN(prop.Max) ? 1 : prop.Max, item == typeof(int) ? 1 : 0);
            if (double.IsNaN(prop.Max)) spec["or_greater"] = true;
        }
        if (prop.Suffix.Length > 0) spec["suffix"] = prop.Suffix;
        if (prop.Doc.Length > 0) spec["doc"] = prop.Doc;
        if (prop.Group.Length > 0) spec["group"] = prop.Group;
        spec["default"] = Value(value);
        return spec;
    }

    static string TypeName(Type t, PropAttribute prop) =>
        t == typeof(bool) ? "bool" : t == typeof(int) ? "int" : t == typeof(float) ? "float"
        : t.IsEnum ? "enum" : t == typeof(Vector4) ? "color" : t == typeof(Vector3) ? "vector3" : t == typeof(Vector2) ? "vector2"
        : t == typeof(string) ? prop.Asset ? "asset" : "string"
        : typeof(GameEntity).IsAssignableFrom(t) ? "entity"
        : throw new NotSupportedException($"{t.Name} has no manifest type");

    /// <summary>A default as Chip stores values: enums by their option value, vectors and colours as arrays, references as null.</summary>
    static JsonNode? Value(object? v) => v switch
    {
        null or GameEntity => null,
        Enum e => e.ToString().ToLowerInvariant(),
        Vector4 c => new JsonArray(c.X, c.Y, c.Z, c.W),
        Vector3 p => new JsonArray(p.X, p.Y, p.Z),
        Vector2 p => new JsonArray(p.X, p.Y),
        System.Collections.IList l => new JsonArray(l.Cast<object?>().Where(x => x is not GameEntity).Select(Value).ToArray()),
        bool b => b,
        int i => i,
        float f => f,
        string s => s,
        _ => throw new NotSupportedException($"no manifest value for {v.GetType().Name}"),
    };

    public static string SnakeCase(string name)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }

    static string Humanize(string typeName)
    {
        var words = SnakeCase(typeName).Replace('_', ' ');
        return char.ToUpperInvariant(words[0]) + words[1..];
    }
}
