using System.Text.Json;
using System.Text.Json.Nodes;

namespace Chip.Core.Scene;

/// <summary>
/// <c>Chip --entities-check &lt;level|folder|manifest&gt; [--json]</c>: finds the game project (or takes a manifest
/// directly), reads its entity manifest the way the editor does, and lints what the editor's tolerant reader would
/// silently drop: unknown types and keys, defaults the type can't read, enums without options, duplicate names. Lists
/// the classes it found. Exit code 0 clean, 1 warnings, 2 errors (the project file or manifest can't be read),
/// 3 no project or no manifest. Agents run this after writing a manifest by hand.
/// </summary>
public static class EntityManifestCheck
{
    static readonly HashSet<string> Types =
        ["bool", "int", "float", "string", "enum", "color", "vector2", "vector3", "node", "entity", "resource", "asset", "array", "unsupported"];
    static readonly HashSet<string> ClassKeys = ["scene", "uid", "label", "base", "doc", "props", "bounds", "preview", "mesh"];
    static readonly HashSet<string> PropKeys =
        ["name", "type", "of", "default", "doc", "group", "options", "range", "or_greater", "or_less", "suffix", "class", "integer",
         "engine", "godot", "file", "multiline"];

    public static int Run(string path, TextWriter output, bool json = false)
    {
        var full = Path.GetFullPath(path);
        string? manifest;
        GameProject? project = null;
        var problems = new List<(bool Error, string Message)>();
        if (full.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full) != GameProject.FileName) manifest = full;
        else
        {
            project = GameProject.Find(full, out var projectProblem);
            if (project == null)
                return Finish(output, json, null, null, null,
                    [(true, $"no game project at or above {full}: add {GameProject.FileName} at the game's root (docs/engines.md)")], 3);
            if (projectProblem != null) problems.Add((true, projectProblem));
            manifest = project.Manifest;
        }
        if (!File.Exists(manifest))
            return Finish(output, json, project, manifest, null, [.. problems, (true, $"no manifest at {manifest}")], 3);

        string text;
        EntityCatalog? catalog = null;
        try
        {
            text = File.ReadAllText(manifest);
            catalog = EntityCatalog.Parse(text, manifest);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            problems.Add((true, $"{Path.GetFileName(manifest)} {e.Message}"));
            return Finish(output, json, project, manifest, null, problems, 2);
        }
        Lint(text, manifest, problems);
        return Finish(output, json, project, manifest, catalog, problems, 0);
    }

    /// <summary>What the editor reads without complaint but probably isn't what the author meant.</summary>
    static void Lint(string text, string manifest, List<(bool, string)> problems)
    {
        void Warn(string m) => problems.Add((false, m));
        var dir = Path.GetDirectoryName(manifest)!;
        using var doc = JsonDocument.Parse(text);
        foreach (var top in doc.RootElement.EnumerateObject())
            if (top.Name is not ("version" or "classes")) Warn($"unknown top-level key \"{top.Name}\" (known: version, classes)");
        foreach (var c in doc.RootElement.GetProperty("classes").EnumerateObject())
        {
            string cls = c.Name;
            foreach (var k in c.Value.EnumerateObject())
                if (!ClassKeys.Contains(k.Name)) Warn($"{cls}: unknown key \"{k.Name}\" (known: {string.Join(", ", ClassKeys)})");
            if (c.Value.TryGetProperty("bounds", out var b) && !(b.ValueKind == JsonValueKind.Array && b.GetArrayLength() == 2
                && EntityValues.Vector(b[0], 3) != null && EntityValues.Vector(b[1], 3) != null))
                Warn($"{cls}: bounds should be [[minX, minY, minZ], [maxX, maxY, maxZ]]; the default 0.4 m box is used");
            if (c.Value.TryGetProperty("preview", out var pv) && pv.ValueKind == JsonValueKind.String
                && !File.Exists(Path.Combine(dir, pv.GetString()!)))
                Warn($"{cls}: preview {pv.GetString()} doesn't exist (relative to the manifest); the box is drawn");
            if (!c.Value.TryGetProperty("props", out var ps)) continue;
            if (ps.ValueKind != JsonValueKind.Array) { Warn($"{cls}: props should be a list; the class has none"); continue; }
            var seen = new HashSet<string>();
            foreach (var p in ps.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object) { Warn($"{cls}: a property that isn't an object"); continue; }
                string name = p.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "?";
                string at = $"{cls}.{name}";
                if (!seen.Add(name)) Warn($"{at}: declared twice; the first wins");
                foreach (var k in p.EnumerateObject())
                    if (!PropKeys.Contains(k.Name)) Warn($"{at}: unknown key \"{k.Name}\" (known: {string.Join(", ", PropKeys)})");
                string type = p.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";
                if (!Types.Contains(type)) { Warn($"{at}: unknown type \"{type}\" (known: {string.Join(", ", Types)}); shown as not editable"); continue; }
                if (type == "array")
                {
                    string of = p.TryGetProperty("of", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString()! : "";
                    if (!Types.Contains(of) || of is "array" or "unsupported")
                        Warn($"{at}: a list needs \"of\": an item type other than array or unsupported; shown as not editable");
                }
                string itemType = type == "array" && p.TryGetProperty("of", out var it) && it.ValueKind == JsonValueKind.String ? it.GetString()! : type;
                if (itemType == "enum" && !(p.TryGetProperty("options", out var os) && os.ValueKind == JsonValueKind.Array && os.GetArrayLength() > 0))
                    Warn($"{at}: an enum needs \"options\": [{{ \"label\": ..., \"value\": ... }}]");
            }
        }
    }

    /// <summary>Defaults the type can't read become "none" in <see cref="EntityCatalog.Parse"/>; report them from the parsed classes.</summary>
    static IEnumerable<string> DroppedDefaults(string text, EntityCatalog catalog)
    {
        using var doc = JsonDocument.Parse(text);
        foreach (var c in doc.RootElement.GetProperty("classes").EnumerateObject())
        {
            if (catalog.Find(c.Name) is not { } cls || !c.Value.TryGetProperty("props", out var ps) || ps.ValueKind != JsonValueKind.Array) continue;
            foreach (var p in ps.EnumerateArray())
                if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    && p.TryGetProperty("default", out var d) && d.ValueKind != JsonValueKind.Null
                    && cls.Prop(n.GetString()!) is { Type: not PropType.Unsupported } spec && spec.Default.ValueKind == JsonValueKind.Null)
                    yield return $"{c.Name}.{spec.Name}: default {d.GetRawText()} isn't a {spec.Type.ToString().ToLowerInvariant()}; the class has no default";
        }
    }

    static int Finish(TextWriter o, bool json, GameProject? project, string? manifest, EntityCatalog? catalog,
        List<(bool Error, string Message)> problems, int code)
    {
        if (catalog != null && manifest != null)
            foreach (var m in DroppedDefaults(File.ReadAllText(manifest), catalog)) problems.Add((false, m));
        if (code == 0 && problems.Count > 0) code = problems.Any(p => p.Error) ? 2 : 1;
        var classes = catalog?.Classes.Where(c => !c.EditorOnly).ToList() ?? [];
        if (json)
        {
            var result = new JsonObject
            {
                ["project"] = project?.Root, ["projectFile"] = project?.File, ["engine"] = project?.Engine,
                ["collisionSuffixes"] = project?.CollisionSuffixes, ["assetPrefix"] = project?.AssetPrefix, ["manifest"] = manifest,
                ["errors"] = problems.Count(p => p.Error), ["warnings"] = problems.Count(p => !p.Error),
                ["diagnostics"] = new JsonArray(problems.Select(p => (JsonNode)new JsonObject
                {
                    ["severity"] = p.Error ? "error" : "warning", ["message"] = p.Message,
                }).ToArray()),
                ["classes"] = new JsonArray(classes.Select(c => (JsonNode)new JsonObject
                {
                    ["name"] = c.Name, ["mesh"] = c.IsMesh, ["props"] = new JsonArray(c.Props.Select(p => (JsonNode)new JsonObject
                    {
                        ["name"] = p.Name, ["type"] = TypeName(p),
                    }).ToArray()),
                }).ToArray()),
            };
            o.WriteLine(result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return code;
        }
        if (project != null)
            o.WriteLine($"project: {project.Root} ({project.Engine}{(project.File == null ? ", from project.godot" : "")}; " +
                $"collision suffixes {(project.CollisionSuffixes ? "on" : "off")}{(project.AssetPrefix.Length > 0 ? $"; assets {project.AssetPrefix}" : "")})");
        if (project is { Surfaces.Count: > 0 })
            o.WriteLine($"surfaces: {string.Join(", ", project.Surfaces.Select(s => $"{s.Name} ({(s.Skip ? "skipped" : s.Render ? "rendered" : "tool")}{(s.Volume ? ", volume" : "")})"))}");
        if (manifest != null) o.WriteLine($"manifest: {manifest}");
        foreach (var (error, message) in problems) o.WriteLine($"{(error ? "error" : "warning")}: {message}");
        foreach (var c in classes)
            o.WriteLine($"  {c.Name + (c.IsMesh ? " (mesh)" : ""),-24} {(c.Props.Count == 0 ? "no properties" : string.Join(", ", c.Props.Select(p => $"{p.Name}:{TypeName(p)}")))}");
        int e = problems.Count(p => p.Error), w = problems.Count - e;
        o.WriteLine($"{classes.Count} class{(classes.Count == 1 ? "" : "es")}, {e} error{(e == 1 ? "" : "s")}, {w} warning{(w == 1 ? "" : "s")}.");
        return code;
    }

    static string TypeName(PropSpec p) => p.Type == PropType.Array ? $"array<{TypeName(p.ItemType)}>" : TypeName(p.Type);

    /// <summary>The manifest's engine-neutral name for a type (<c>entity</c>, not Godot's <c>node</c>).</summary>
    static string TypeName(PropType t) => t switch
    {
        PropType.Node => "entity", PropType.Resource => "asset", _ => t.ToString().ToLowerInvariant(),
    };
}
