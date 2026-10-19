using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Chip.Core.Scene;

namespace Chip.Core.Dressing;

/// <summary>Where a level's rules live.</summary>
public static class DressingRules
{
    /// <summary><c>hangar.chip</c> → <c>hangar.dressing.json</c> beside it.</summary>
    public static string DefaultPathFor(string levelPath) =>
        RulePaths.Normalize(Path.Combine(Path.GetDirectoryName(levelPath) ?? "", Path.GetFileNameWithoutExtension(levelPath) + ".dressing.json"));

    /// <summary>
    /// The level's rules file: <see cref="Document.DressingPath"/> resolved against the level, else the default name if that
    /// file exists. Null for unsaved levels without an absolute path, or when there is no rules file.
    /// </summary>
    public static string? PathFor(Document doc, string? levelPath, IDressingFiles files)
    {
        if (doc.DressingPath is { Length: > 0 } p)
            return Path.IsPathRooted(p) ? RulePaths.Normalize(p) : levelPath == null ? null : RulePaths.Resolve(levelPath, p);
        if (levelPath == null) return null;
        var fallback = DefaultPathFor(levelPath);
        return files.Stamp(fallback) != 0 ? fallback : null;
    }
}

/// <summary>
/// <c>Chip --rules-check level.chip [--json]</c>: loads the level's rules, dresses it headlessly and reports every problem
/// (with position, JSON path and a fix) plus what each rule did. Exit code 0 clean, 1 warnings, 2 errors, 3 no rules file.
/// Agents run this after editing a rules file; <c>--shot</c> shows what the result looks like.
/// </summary>
public static class RulesCheck
{
    public static int Run(Document doc, string? levelPath, IDressingFiles files, TextWriter output, bool json = false)
    {
        var rulesPath = DressingRules.PathFor(doc, levelPath, files);
        if (rulesPath == null)
        {
            string expected = levelPath == null ? "<level>.dressing.json" : DressingRules.DefaultPathFor(levelPath);
            if (json) output.WriteLine(new JsonObject { ["error"] = $"no rules file (expected {expected})" }.ToJsonString());
            else output.WriteLine($"no rules file: create {expected}, or set \"dressing\" in the level");
            return 3;
        }
        var level = new Dresser(files).Dress(doc, rulesPath);
        int errors = level.ErrorCount, warnings = level.WarningCount;
        if (json) output.WriteLine(Json(level, rulesPath).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        else Text(level, rulesPath, output);
        return errors > 0 ? 2 : warnings > 0 ? 1 : 0;
    }

    static void Text(DressedLevel level, string rulesPath, TextWriter o)
    {
        var rules = level.Rules;
        o.WriteLine($"rules: {rulesPath}{(rules.ImportCount > 0 ? $" (+{rules.ImportCount} import{(rules.ImportCount == 1 ? "" : "s")})" : "")}  chipDressing {RuleSet.FormatVersion}");
        foreach (var d in level.Diagnostics) o.WriteLine(d);
        if (level.Stats.Count > 0 || rules.Broken.Count > 0)
        {
            o.WriteLine($"{"rule",-16} {"stage",-6} {"targets",-12} {"outputs",-10} {"skipped",-8} ms");
            foreach (var s in level.Stats)
                o.WriteLine(s.Enabled
                    ? $"{s.Rule,-16} {s.Stage.ToString().ToLowerInvariant(),-6} {$"{s.Targets} {s.TargetNoun}",-12} {$"{s.Outputs} mesh",-10} {s.Skipped,-8} {s.Ms.ToString("0.0", CultureInfo.InvariantCulture)}"
                    : $"{s.Rule,-16} -      disabled");
            foreach (var id in rules.Broken) o.WriteLine($"{id,-16} -      not run (errors above)");
        }
        int e = level.ErrorCount, w = level.WarningCount;
        o.WriteLine($"{e} error{(e == 1 ? "" : "s")}, {w} warning{(w == 1 ? "" : "s")}.  content {level.ContentHash}");
    }

    static JsonObject Json(DressedLevel level, string rulesPath)
    {
        var diags = new JsonArray();
        foreach (var d in level.Diagnostics)
        {
            var o = new JsonObject { ["severity"] = d.IsError ? "error" : "warning", ["code"] = d.Code, ["message"] = d.Message };
            if (d.Fix != null) o["fix"] = d.Fix;
            if (d.Pos is { } p) { o["file"] = p.File; o["line"] = p.Line; o["col"] = p.Col; }
            if (d.Path != null) o["path"] = d.Path;
            if (d.MeshId is { } m) o["mesh"] = m;
            if (d.Rule != null) o["rule"] = d.Rule;
            diags.Add(o);
        }
        var stats = new JsonArray();
        foreach (var s in level.Stats)
            stats.Add(new JsonObject
            {
                ["rule"] = s.Rule, ["kind"] = s.Kind, ["stage"] = s.Stage.ToString().ToLowerInvariant(), ["enabled"] = s.Enabled,
                ["targets"] = s.Targets, ["targetNoun"] = s.TargetNoun, ["outputs"] = s.Outputs, ["skipped"] = s.Skipped,
                ["ms"] = Math.Round(s.Ms, 2),
            });
        return new JsonObject
        {
            ["rules"] = rulesPath, ["imports"] = level.Rules.ImportCount, ["errors"] = level.ErrorCount, ["warnings"] = level.WarningCount,
            ["diagnostics"] = diags, ["stats"] = stats, ["broken"] = new JsonArray(level.Rules.Broken.Select(b => (JsonNode)b!).ToArray()),
            ["content"] = level.ContentHash,
        };
    }
}

/// <summary>
/// The JSON Schema for rules files, generated from the same parameter tables the loader checks against, so editors
/// (and agents) get completion and validation that can't drift from Chip. Chip writes it beside new rules files.
/// </summary>
public static class DressingSchema
{
    public const string FileName = "dressing-1.schema.json";

    public static string Generate()
    {
        JsonObject Var() => new() { ["type"] = "string", ["pattern"] = "^\\$", ["description"] = "A variable from \"vars\"." };
        JsonNode Typed(ParamSpec p)
        {
            JsonObject t = p.Type switch
            {
                ParamType.Number => new() { ["type"] = "number" },
                ParamType.Int => new() { ["type"] = "integer" },
                ParamType.Text => new() { ["type"] = "string" },
                ParamType.Bool => new() { ["type"] = "boolean" },
                ParamType.Choice => new() { ["enum"] = new JsonArray(p.Choices!.Select(c => (JsonNode)c!).ToArray()) },
                ParamType.Pair => new() { ["type"] = "array", ["items"] = new JsonObject { ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "number" }, Var()) }, ["minItems"] = 2, ["maxItems"] = 2 },
                ParamType.Faces => new() { ["$ref"] = "#/$defs/faces" },
                ParamType.Meshes => new() { ["$ref"] = "#/$defs/meshes" },
                ParamType.Prop => new() { ["$ref"] = "#/$defs/prop" },
                ParamType.Props => new() { ["anyOf"] = new JsonArray(new JsonObject { ["$ref"] = "#/$defs/prop" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["$ref"] = "#/$defs/prop" }, ["minItems"] = 1 }) },
                ParamType.Numbers => new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "number", ["minimum"] = 0 }, ["minItems"] = 1 },
                _ => new() { ["$ref"] = "#/$defs/profile" },
            };
            if (!double.IsInfinity(p.Min)) t["minimum"] = p.Min;
            if (!double.IsInfinity(p.Max)) t["maximum"] = p.Max;
            return new JsonObject { ["description"] = p.Description, ["anyOf"] = new JsonArray(t, Var()) };
        }

        var defs = new JsonObject
        {
            ["faces"] = new JsonObject
            {
                ["type"] = "object", ["additionalProperties"] = false,
                ["description"] = "Which faces: every key given must hold. Tags are lowercase letters, digits, - and _.",
                ["properties"] = new JsonObject
                {
                    ["tags"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Faces with all of these tags." },
                    ["notTags"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Faces with none of these tags." },
                    ["materials"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Material name globs (* and ?), any of." },
                    ["facing"] = new JsonObject { ["enum"] = new JsonArray(FaceSelector.Facings.Select(f => (JsonNode)f!).ToArray()), ["description"] = "up (floors), down (ceilings), upright (walls)." },
                },
            },
            ["meshes"] = new JsonObject
            {
                ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray("tags"),
                ["description"] = "Which meshes, by their mesh tags (Ctrl+T in object mode).",
                ["properties"] = new JsonObject
                {
                    ["tags"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Meshes with all of these tags." },
                    ["notTags"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Meshes with none of these tags." },
                },
            },
            ["prop"] = new JsonObject
            {
                ["description"] = "A .glb/.gltf file relative to this rules file, or a built-in shape in metres. Props stand on their origin: +Y up, +X along, +Z out.",
                ["anyOf"] = new JsonArray(
                    new JsonObject { ["type"] = "string", ["pattern"] = "\\.(glb|gltf)$" },
                    new JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray("box"), ["properties"] = new JsonObject { ["box"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "number" }, ["minItems"] = 3, ["maxItems"] = 3, ["description"] = "[width, height, depth]" } } },
                    new JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray("cylinder"), ["properties"] = new JsonObject { ["cylinder"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "number" }, ["minItems"] = 2, ["maxItems"] = 2, ["description"] = "[diameter, height]" }, ["sides"] = new JsonObject { ["type"] = "integer", ["minimum"] = 3, ["maximum"] = 64 } } }),
            },
            ["profile"] = new JsonObject
            {
                ["type"] = "object", ["additionalProperties"] = false,
                ["description"] = "Cross-section in metres: x out of the face, y along the surface it sits on; (0, 0) is the corner.",
                ["properties"] = new JsonObject
                {
                    ["rect"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "number" }, Var()) }, ["minItems"] = 2, ["maxItems"] = 2, ["description"] = "[depth, height]" },
                    ["round"] = new JsonObject { ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "number" }, Var()), ["description"] = "Diameter." },
                    ["sides"] = new JsonObject { ["type"] = "integer", ["minimum"] = 3, ["maximum"] = 64, ["description"] = "Sides of a round profile (default 8)." },
                    ["points"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "array", ["minItems"] = 2, ["maxItems"] = 2 }, ["minItems"] = 3, ["description"] = "Polygon [[x, y], ...]." },
                },
            },
        };

        var kinds = new JsonArray();
        var patchProps = new JsonObject();
        foreach (var kind in RuleKind.All)
        {
            var props = new JsonObject
            {
                ["kind"] = new JsonObject { ["const"] = kind.Name, ["description"] = $"{kind.Name} ({kind.Stage.ToString().ToLowerInvariant()}): {kind.Summary}" },
                ["enabled"] = new JsonObject { ["type"] = "boolean" },
                ["note"] = new JsonObject { ["type"] = "string" },
            };
            foreach (var p in kind.Params)
            {
                props[p.Name] = Typed(p);
                patchProps[p.Name] ??= new JsonObject { ["description"] = p.Description };
            }
            var required = new JsonArray("kind");
            foreach (var p in kind.Params.Where(p => p.Required)) required.Add(p.Name);
            kinds.Add(new JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = props, ["required"] = required });
        }
        patchProps["enabled"] = new JsonObject { ["type"] = "boolean" };
        patchProps["note"] = new JsonObject { ["type"] = "string" };
        kinds.Add(new JsonObject
        {
            ["description"] = "No kind: changes the imported rule with this id (only the keys given).",
            ["type"] = "object", ["not"] = new JsonObject { ["required"] = new JsonArray("kind") }, ["properties"] = patchProps,
        });

        var schema = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = "Chip dressing rules",
            ["description"] = "Rules that turn a Chip blockout into the shipped level. See docs/dressing.md. Lengths are metres.",
            ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["$schema"] = new JsonObject { ["type"] = "string" },
                ["chipDressing"] = new JsonObject { ["const"] = RuleSet.FormatVersion, ["description"] = "Format version." },
                ["name"] = new JsonObject { ["type"] = "string" },
                ["note"] = new JsonObject { ["type"] = "string" },
                ["imports"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Rules files to build on, relative to this one. Later imports and this file win." },
                ["vars"] = new JsonObject { ["type"] = "object", ["description"] = "Values rules use as \"$name\" (whole values only)." },
                ["rules"] = new JsonObject
                {
                    ["type"] = "object", ["description"] = "Rules by id (lowercase letters, digits, - and _).",
                    ["propertyNames"] = new JsonObject { ["pattern"] = "^[a-z0-9][a-z0-9_-]*$" },
                    ["additionalProperties"] = new JsonObject { ["anyOf"] = kinds },
                },
            },
            ["$defs"] = defs,
        };
        return schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>A starter rules file: a baseboard round every wall standing on a floor, and softened outside corners.</summary>
    public static string Starter(string? levelName) => $$"""
        {
          "$schema": "./{{FileName}}",
          "chipDressing": {{RuleSet.FormatVersion}},
          "name": "{{levelName ?? "Level"}} dressing",
          "vars": { "trim": "dev_measurewall01d" },
          "rules": {
            "baseboard": {
              "kind": "sweep",
              "note": "Runs along the foot of every face tagged wall that stands on a floor. Tag walls with Ctrl+T.",
              "faces": { "tags": ["wall"] },
              "profile": { "rect": [0.02, 0.12] },
              "material": "$trim"
            },
            "wall-bevel": {
              "kind": "bevel",
              "enabled": false,
              "note": "Softens outside corners between wall faces. Set enabled to true to try it.",
              "faces": { "tags": ["wall"] },
              "width": 0.03
            }
          }
        }
        """;
}
