using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace Chip.Core.Scene;

/// <summary>
/// Entity property values: plain JSON (numbers, bools, strings, <c>[r, g, b, a]</c> colours in 0..1, <c>[x, y, z]</c>
/// vectors, <c>{ "entity": id }</c> references, <c>"res://…"</c> resource paths), read against a <see cref="PropSpec"/>.
/// Reading is tolerant, so levels from before typed properties (every value a string: <c>"255 128 0"</c>, <c>"10"</c>,
/// <c>"1"</c>) still read as the type their class declares.
/// </summary>
public static class EntityValues
{
    public static readonly JsonElement Null = JsonSerializer.SerializeToElement<object?>(null);

    public static JsonElement Of(string value) => JsonSerializer.SerializeToElement(value);
    public static JsonElement Of(bool value) => JsonSerializer.SerializeToElement(value);
    public static JsonElement Of(double value) => JsonSerializer.SerializeToElement(value);
    public static JsonElement Of(long value) => JsonSerializer.SerializeToElement(value);

    /// <summary>A float from the UI as the decimal it shows (0.8f is stored as 0.8, not 0.800000011920929).</summary>
    public static JsonElement Of(float value) => Of(Shortest(value));

    public static JsonElement Color(Vector4 c) => Array([c.X, c.Y, c.Z, c.W]);
    public static JsonElement Array(params float[] values) => JsonSerializer.SerializeToElement(values.Select(Shortest).ToArray());
    public static JsonElement Ref(int entityId) => JsonSerializer.SerializeToElement(new Dictionary<string, int> { ["entity"] = entityId });

    static double Shortest(float f) => double.Parse(f.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    public static bool IsNull(JsonElement v) => v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;

    /// <summary>Structural equality (two <c>3</c>s from different documents are the same value).</summary>
    public static bool Same(JsonElement a, JsonElement b) =>
        IsNull(a) ? IsNull(b) : !IsNull(b) && JsonElement.DeepEquals(a, b);

    public static bool SameProperties(IReadOnlyDictionary<string, JsonElement> a, IReadOnlyDictionary<string, JsonElement> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var o) && Same(kv.Value, o));

    // ---- Tolerant readers ----

    /// <summary>A string as itself; anything else as its JSON text (<c>3</c>, <c>true</c>, <c>[1, 0, 0]</c>).</summary>
    public static string Text(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString()!,
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => v.GetRawText(),
    };

    public static bool? Bool(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => v.GetDouble() != 0,
        JsonValueKind.String => v.GetString()!.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" or "" => false,
            _ => null,
        },
        _ => null,
    };

    public static double? Number(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => v.GetDouble(),
        JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        _ => null,
    };

    /// <summary>
    /// <c>[r, g, b(, a)]</c> in 0..1; or a string: <c>#ff8800(ff)</c>, <c>"255 136 0"</c> (0..255 when any part is over 1),
    /// <c>"1 0.5 0"</c>, or Hammer's <c>"r g b brightness"</c> (brightness ignored).
    /// </summary>
    public static Vector4? Color(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Array)
        {
            var f = Floats(v);
            if (f is not { Length: 3 or 4 }) return null;
            return new Vector4(f[0], f[1], f[2], f.Length > 3 ? f[3] : 1);
        }
        if (v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString()!.Trim();
        var hex = s.TrimStart('#');
        if ((s.StartsWith('#') || hex.Length is 6 or 8) && hex.Length is 6 or 8 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgba))
        {
            if (hex.Length == 6) rgba = (rgba << 8) | 0xFF;
            return new Vector4((rgba >> 24) / 255f, ((rgba >> 16) & 0xFF) / 255f, ((rgba >> 8) & 0xFF) / 255f, (rgba & 0xFF) / 255f);
        }
        var parts = Split(s);
        if (parts is not { Length: >= 3 }) return null;
        bool bytes = parts[0] > 1 || parts[1] > 1 || parts[2] > 1;
        float k = bytes ? 1 / 255f : 1;
        return new Vector4(parts[0] * k, parts[1] * k, parts[2] * k, 1);
    }

    /// <summary><paramref name="n"/> numbers from an array or a space-separated string.</summary>
    public static float[]? Vector(JsonElement v, int n)
    {
        var f = v.ValueKind == JsonValueKind.Array ? Floats(v) : v.ValueKind == JsonValueKind.String ? Split(v.GetString()!) : null;
        return f != null && f.Length == n ? f : null;
    }

    /// <summary>The referenced entity's id, from <c>{ "entity": id }</c>.</summary>
    public static int? RefId(JsonElement v) =>
        v.ValueKind == JsonValueKind.Object && v.TryGetProperty("entity", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out int i) ? i : null;

    static float[]? Floats(JsonElement array)
    {
        var result = new float[array.GetArrayLength()];
        int i = 0;
        foreach (var x in array.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.Number) return null;
            result[i++] = (float)x.GetDouble();
        }
        return result;
    }

    static float[]? Split(string s)
    {
        var parts = s.Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var result = new float[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out result[i])) return null;
        return result;
    }

    // ---- Against a spec ----

    /// <summary>
    /// The value in the property's type (an old string "10" for a float becomes 10), or null with the reason when it
    /// can't be read as one. Unsupported properties pass through unchanged.
    /// </summary>
    public static JsonElement? Coerce(PropSpec p, JsonElement v, out string? error)
    {
        error = null;
        if (IsNull(v))
        {
            if (p.Type is PropType.Node or PropType.Resource or PropType.Unsupported) return Null;
            error = $"{p.Name} needs a value";
            return null;
        }
        switch (p.Type)
        {
            case PropType.Bool:
                if (Bool(v) is { } b) return Of(b);
                break;
            case PropType.Int:
                if (Number(v) is { } i && double.IsFinite(i)) return Of((long)Math.Round(i));
                break;
            case PropType.Float:
                if (Number(v) is { } d && double.IsFinite(d)) return v.ValueKind == JsonValueKind.Number ? v : Of(d);
                break;
            case PropType.String:
                return v.ValueKind == JsonValueKind.String ? v : Of(Text(v));
            case PropType.Enum:
                foreach (var o in p.Options)
                    if (Same(o.Value, v) || (Number(v) is { } n && Number(o.Value) == n) || (v.ValueKind == JsonValueKind.String && o.Label == v.GetString()))
                        return o.Value;
                error = $"{p.Name} is {Text(v)}, which isn't one of {string.Join(", ", p.Options.Select(o => o.Label))}";
                return null;
            case PropType.Color:
                if (Color(v) is { } c) return Color(c);
                break;
            case PropType.Vector2 or PropType.Vector3:
                if (Vector(v, p.Type == PropType.Vector2 ? 2 : 3) is { } vec)
                    return Array(p.Integer ? vec.Select(x => MathF.Round(x)).ToArray() : vec);
                break;
            case PropType.Node:
                if (RefId(v) is { } id) return Ref(id);
                break;
            case PropType.Resource:
                if (v.ValueKind == JsonValueKind.String) return v;
                break;
            case PropType.Unsupported:
                return v;
        }
        error = $"{p.Name} is {Text(v)}, which isn't a {TypeName(p.Type)}";
        return null;
    }

    /// <summary>The value the entity has for the property: its own (when readable) or the class default.</summary>
    public static JsonElement Effective(Entity e, PropSpec p) =>
        e.Properties.TryGetValue(p.Name, out var v) && Coerce(p, v, out _) is { } ok ? ok : p.Default;

    public static string TypeName(PropType t) => t switch
    {
        PropType.Bool => "true/false value",
        PropType.Int => "whole number",
        PropType.Float => "number",
        PropType.String => "text",
        PropType.Enum => "choice",
        PropType.Color => "colour",
        PropType.Vector2 => "2D vector",
        PropType.Vector3 => "3D vector",
        PropType.Node => "reference to an entity",
        PropType.Resource => "resource path",
        _ => "value",
    };
}
