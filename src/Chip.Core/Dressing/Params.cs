using System.Globalization;
using System.Numerics;
using Chip.Core.Geometry;

namespace Chip.Core.Dressing;

public enum ParamType { Number, Int, Text, Bool, Choice, Pair, Faces, Profile }

/// <summary>
/// One parameter a rule kind accepts: its type, default, range and description. The tables drive validation, defaults,
/// the JSON Schema and the inspector, so adding a parameter is one line.
/// </summary>
public sealed record ParamSpec(string Name, ParamType Type, object? Default, string Description,
    double Min = double.NegativeInfinity, double Max = double.PositiveInfinity, string[]? Choices = null, bool Required = false);

/// <summary>Which faces a rule looks at. Every key that is set must hold; an empty selector matches every face.</summary>
public sealed record FaceSelector(string[] Tags, string[] NotTags, string[] Materials, string? Facing)
{
    public static readonly FaceSelector Any = new([], [], [], null);
    public static readonly FaceSelector Up = new([], [], [], "up");

    public static readonly string[] Facings = ["up", "down", "upright"];

    /// <summary>Whether face <paramref name="f"/> of <paramref name="m"/> matches (its tags, material and which way it faces).</summary>
    public bool Matches(PolyMesh m, int f, Vector3 normal)
    {
        var face = m.Faces[f];
        foreach (var t in Tags) if (!face.Tags.Contains(t)) return false;
        foreach (var t in NotTags) if (face.Tags.Contains(t)) return false;
        if (Materials.Length > 0 && !Materials.Any(g => Glob(g, face.Material))) return false;
        return Facing switch
        {
            "up" => normal.Y > 0.7f,
            "down" => normal.Y < -0.7f,
            "upright" => MathF.Abs(normal.Y) < 0.3f,
            _ => true,
        };
    }

    /// <summary>Case-insensitive glob with <c>*</c> (any run) and <c>?</c> (one character).</summary>
    public static bool Glob(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(text[t]))) { p++; t++; }
            else if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = t; }
            else if (star >= 0) { p = star + 1; t = ++mark; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}

/// <summary>
/// A cross-section to sweep, as a closed polygon in the plane across the edge: x points out of the face the edge belongs
/// to, y along the neighbouring surface (up from a floor, down from a ceiling). The corner the edge sits in is (0, 0).
/// </summary>
public sealed record Profile(Vector2[] Points)
{
    public static readonly Profile Default = Rect(0.015f, 0.12f);

    public static Profile Rect(float depth, float height) => new([new(0, 0), new(0, height), new(depth, height), new(depth, 0)]);

    public static Profile Round(float diameter, int sides)
    {
        float r = diameter * 0.5f;
        var pts = new Vector2[sides];
        for (int i = 0; i < sides; i++)
        {
            float a = MathF.Tau * i / sides;
            pts[i] = new Vector2(r + r * MathF.Cos(a), r + r * MathF.Sin(a));
        }
        return new Profile(pts);
    }

    /// <summary>Furthest any point is from the corner: how far a sweep reaches, for neighbour queries.</summary>
    public float Reach => Points.Max(p => p.Length());
}

/// <summary>Converts and checks parameter values against their <see cref="ParamSpec"/>, reporting precise diagnostics.</summary>
static class ParamReader
{
    public static object? Read(ParamSpec spec, JNode node, string path, List<Diagnostic> diags, string rule)
    {
        Diagnostic Bad(string message, string? fix = null) =>
            new(Severity.Error, "E201", message, fix, node.Pos, path, Rule: rule);

        switch (spec.Type)
        {
            case ParamType.Number:
            case ParamType.Int:
            {
                if (node is not JNumber n)
                {
                    string? fix = node is JString s && double.TryParse(s.Value.TrimEnd('m', 'c', ' '), NumberStyles.Float, CultureInfo.InvariantCulture, out var guess)
                        ? $"write {(s.Value.Contains("cm") ? (guess / 100).ToString(CultureInfo.InvariantCulture) : guess.ToString(CultureInfo.InvariantCulture))} without quotes (lengths are metres)"
                        : null;
                    diags.Add(Bad($"expected a number, got {node.TypeName}", fix));
                    return null;
                }
                if (spec.Type == ParamType.Int && n.Value != Math.Floor(n.Value)) { diags.Add(Bad($"expected a whole number, got {n}")); return null; }
                if (n.Value < spec.Min || n.Value > spec.Max)
                {
                    diags.Add(new Diagnostic(Severity.Error, "E202", $"{n} is out of range for {spec.Name} ({Range(spec)})",
                        $"use a value {Range(spec)}", node.Pos, path, Rule: rule));
                    return null;
                }
                return spec.Type == ParamType.Int ? (object)(int)n.Value : (float)n.Value;
            }
            case ParamType.Text:
                if (node is JString t) return t.Value;
                diags.Add(Bad($"expected text, got {node.TypeName}"));
                return null;
            case ParamType.Bool:
                if (node is JBool b) return b.Value;
                diags.Add(Bad($"expected true or false, got {node.TypeName}"));
                return null;
            case ParamType.Choice:
                if (node is JString c && spec.Choices!.Contains(c.Value)) return c.Value;
                diags.Add(Bad(node is JString w ? $"\"{w.Value}\" isn't one of {string.Join(", ", spec.Choices!)}" : $"expected one of {string.Join(", ", spec.Choices!)}",
                    node is JString w2 && Suggest.Closest(w2.Value, spec.Choices!) is { } s2 ? $"did you mean \"{s2}\"?" : null));
                return null;
            case ParamType.Pair:
                if (node is JArray { Items.Count: 2 } a && a.Items.All(i => i is JNumber))
                    return new Vector2((float)((JNumber)a.Items[0]).Value, (float)((JNumber)a.Items[1]).Value);
                diags.Add(Bad($"expected two numbers like [0, 0.1], got {node.TypeName}"));
                return null;
            case ParamType.Faces:
                return ReadFaces(node, path, diags, rule);
            case ParamType.Profile:
                return ReadProfile(node, path, diags, rule);
        }
        return null;
    }

    static string Range(ParamSpec s) =>
        double.IsNegativeInfinity(s.Min) ? $"at most {s.Max}" : double.IsPositiveInfinity(s.Max) ? $"at least {s.Min}" : $"from {s.Min} to {s.Max}";

    static readonly string[] SelectorKeys = ["tags", "notTags", "materials", "facing"];

    static FaceSelector? ReadFaces(JNode node, string path, List<Diagnostic> diags, string rule)
    {
        if (node is not JObject o)
        {
            diags.Add(new(Severity.Error, "E201", $"expected a face selector like {{ \"tags\": [\"wall\"] }}, got {node.TypeName}", null, node.Pos, path, Rule: rule));
            return null;
        }
        int before = diags.Count;
        string[] Names(string key, bool tags)
        {
            if (o[key] is not { } v) return [];
            if (v is not JArray arr || arr.Items.Any(i => i is not JString))
            {
                diags.Add(new(Severity.Error, "E201", $"{key} is a list of names like [\"wall\"]", null, v.Pos, $"{path}.{key}", Rule: rule));
                return [];
            }
            var names = arr.Items.Cast<JString>().Select(s => s.Value).ToArray();
            if (tags)
                foreach (var s in arr.Items.Cast<JString>())
                    if (!TagSet.IsValid(s.Value, out var why))
                        diags.Add(new(Severity.Error, "E203", $"\"{s.Value}\" isn't a valid tag: {why}", TagSet.Normalize(s.Value) is { } n ? $"use \"{n}\"" : null, s.Pos, $"{path}.{key}", Rule: rule));
            return names;
        }
        foreach (var key in o.Keys.Where(k => !SelectorKeys.Contains(k)))
            diags.Add(UnknownKey(key, SelectorKeys, o.KeyPos(key), $"{path}.{key}", rule, "face selector"));
        string? facing = null;
        if (o["facing"] is { } f)
        {
            if (f is JString fs && FaceSelector.Facings.Contains(fs.Value)) facing = fs.Value;
            else diags.Add(new(Severity.Error, "E201", $"facing is one of {string.Join(", ", FaceSelector.Facings)}", null, f.Pos, $"{path}.facing", Rule: rule));
        }
        var sel = new FaceSelector(Names("tags", true), Names("notTags", true), Names("materials", false), facing);
        return diags.Count == before ? sel : null;
    }

    static Profile? ReadProfile(JNode node, string path, List<Diagnostic> diags, string rule)
    {
        Diagnostic Bad(string m, JNode at) => new(Severity.Error, "E201", m,
            "use { \"rect\": [depth, height] }, { \"round\": diameter } or { \"points\": [[x, y], ...] }", at.Pos, path, Rule: rule);
        if (node is not JObject o || o.Entries.Count == 0) { diags.Add(Bad($"expected a profile, got {node.TypeName}", node)); return null; }
        string[] known = ["rect", "round", "sides", "points"];
        foreach (var key in o.Keys.Where(k => !known.Contains(k)))
        {
            diags.Add(UnknownKey(key, known, o.KeyPos(key), $"{path}.{key}", rule, "profile"));
            return null;
        }
        if (o["rect"] is { } r)
        {
            if (r is JArray { Items.Count: 2 } ra && ra.Items.All(i => i is JNumber { Value: > 0 }))
                return Profile.Rect((float)((JNumber)ra.Items[0]).Value, (float)((JNumber)ra.Items[1]).Value);
            diags.Add(Bad("rect is [depth, height], both above 0 (metres)", r));
            return null;
        }
        if (o["round"] is { } d)
        {
            int sides = o["sides"] is JNumber { Value: >= 3 and <= 64 } sn ? (int)sn.Value : 8;
            if (d is JNumber { Value: > 0 } dn) return Profile.Round((float)dn.Value, sides);
            diags.Add(Bad("round is a diameter above 0 (metres)", d));
            return null;
        }
        if (o["points"] is JArray { Items.Count: >= 3 } pa && pa.Items.All(i => i is JArray { Items.Count: 2 } p && p.Items.All(n => n is JNumber)))
            return new Profile(pa.Items.Cast<JArray>().Select(p => new Vector2((float)((JNumber)p.Items[0]).Value, (float)((JNumber)p.Items[1]).Value)).ToArray());
        diags.Add(Bad("points is a list of at least three [x, y] pairs", o["points"] ?? node));
        return null;
    }

    public static Diagnostic UnknownKey(string key, IEnumerable<string> allowed, SourcePos pos, string path, string? rule, string what)
    {
        var list = allowed.ToList();
        var guess = Suggest.Closest(key, list);
        return new Diagnostic(Severity.Error, "E104", $"unknown key \"{key}\" for {what}",
            (guess != null ? $"did you mean \"{guess}\"? " : "") + $"(allowed: {string.Join(", ", list)})", pos, path, Rule: rule);
    }
}
