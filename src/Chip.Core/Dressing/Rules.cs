using System.Globalization;
using System.Text;
using Chip.Core.Geometry;

namespace Chip.Core.Dressing;

/// <summary>
/// Shape rules replace a blockout mesh (a modifier like bevel swaps in a changed copy; a proxy hides the mesh and builds
/// from it). Dress rules add geometry. Both read only the blockout.
/// </summary>
public enum Stage { Shape, Dress }

/// <summary>A kind of rule (<c>bevel</c>, <c>sweep</c>): its stage and the parameters it accepts.</summary>
public abstract class RuleKind
{
    public abstract string Name { get; }
    /// <summary>What the editor calls the kind ("Trim" for sweep).</summary>
    public virtual string DisplayName => Name;
    public abstract Stage Stage { get; }
    public abstract string Summary { get; }
    public abstract IReadOnlyList<ParamSpec> Params { get; }

    /// <summary>Every kind, in the order the schema and docs list them.</summary>
    public static IReadOnlyList<RuleKind> All { get; } =
        [new BevelKind(), new RailingKind(), new StairsKind(), new SweepKind(), new RepeatKind(), new ScatterKind()];

    public static RuleKind? Find(string name) => All.FirstOrDefault(k => k.Name == name);

    /// <summary>Keys every rule may have besides its kind's parameters.</summary>
    public static readonly ParamSpec[] Common =
    [
        new("kind", ParamType.Choice, null, "What the rule does.", Required: true),
        new("enabled", ParamType.Bool, true, "false switches the rule off (handy in a level file overriding a shared style)."),
        new("note", ParamType.Text, null, "Free text for people and agents reading the file. Ignored by Chip."),
    ];
}

/// <summary>A loaded, checked rule: its id, kind, and every parameter with defaults filled in.</summary>
public sealed class Rule
{
    readonly Dictionary<string, object?> _values;

    internal Rule(string id, RuleKind kind, bool enabled, string? note, SourcePos pos, Dictionary<string, object?> values, ulong fingerprint, JObject def)
    {
        Id = id; Kind = kind; Enabled = enabled; Note = note; Pos = pos; _values = values; Fingerprint = fingerprint; Def = def;
    }

    /// <summary>The rule as written, imports merged and variables substituted: what per-mesh overrides are laid over.</summary>
    internal JObject Def { get; }

    /// <summary>Whether <paramref name="name"/> was written in the rule (rather than defaulted).</summary>
    public bool IsSet(string name) => Def.Has(name);

    public string Id { get; }
    public RuleKind Kind { get; }
    public bool Enabled { get; }
    public string? Note { get; }
    /// <summary>Where the rule is defined.</summary>
    public SourcePos Pos { get; }
    /// <summary>Changes whenever anything about the rule does (after imports and vars), so its cached results rebuild.</summary>
    public ulong Fingerprint { get; }

    public T Get<T>(string name) => (T)_values[name]!;
    public T? Find<T>(string name) where T : class => _values.TryGetValue(name, out var v) ? v as T : null;
    public float Number(string name) => (float)_values[name]!;
    public int Int(string name) => (int)_values[name]!;
    public string? Text(string name) => _values.TryGetValue(name, out var v) ? v as string : null;
    public IReadOnlyDictionary<string, object?> Values => _values;

    /// <summary>The tag that opts a face out of this rule.</summary>
    public string SkipTag => TagSet.SkipPrefix + Id;
}

/// <summary>
/// A rules file with its imports resolved, variables substituted and every rule checked. Never throws on content:
/// problems become <see cref="Diagnostics"/>, and a rule with errors is left out of <see cref="Rules"/> (listed in
/// <see cref="Broken"/>) while the others still run.
/// </summary>
public sealed class RuleSet
{
    public const int FormatVersion = 1;

    public static readonly RuleSet Empty = new(null, null, [], [], [], [], 0);

    RuleSet(string? path, string? name, List<Rule> rules, List<string> broken, List<Diagnostic> diagnostics, List<string> files, int imports,
        Dictionary<string, JNode>? vars = null)
    {
        Path = path; Name = name; Rules = rules; Broken = broken; Diagnostics = diagnostics; Files = files; ImportCount = imports;
        _vars = vars ?? new();
    }

    readonly Dictionary<string, JNode> _vars;

    public string? Path { get; }
    public string? Name { get; }
    /// <summary>Valid rules (enabled or not), imports first, in file order.</summary>
    public IReadOnlyList<Rule> Rules { get; }
    /// <summary>Ids of rules left out because they have errors.</summary>
    public IReadOnlyList<string> Broken { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    /// <summary>Every file read, main file first: stamp these to notice edits.</summary>
    public IReadOnlyList<string> Files { get; }
    public int ImportCount { get; }
    public bool HasErrors => Diagnostics.Any(d => d.IsError);

    static readonly string[] TopKeys = ["$schema", "chipDressing", "name", "note", "imports", "vars", "rules"];

    public static RuleSet Load(IDressingFiles files, string path)
    {
        var diags = new List<Diagnostic>();
        var read = new List<string>();
        string? name = null;
        var layer = LoadFile(files, RulePaths.Normalize(path), null, [], diags, read, ref name);
        var rules = new List<Rule>();
        var broken = new List<string>();
        if (layer != null)
            foreach (var (id, def) in layer.Rules)
            {
                int before = diags.Count;
                var rule = Bind(id, def, layer.Vars, diags);
                if (rule != null && diags.Skip(before).All(d => !d.IsError)) rules.Add(rule);
                else broken.Add(id);
            }
        return new RuleSet(path, name, rules, broken, diags, read, Math.Max(0, read.Count - 1), layer?.Vars);
    }

    sealed class Layer
    {
        public readonly Dictionary<string, JNode> Vars = new(StringComparer.Ordinal);
        public readonly List<(string Id, JObject Def)> Rules = new();

        public void SetRule(string id, JObject def)
        {
            int i = Rules.FindIndex(r => r.Id == id);
            if (i >= 0) Rules[i] = (id, def);
            else Rules.Add((id, def));
        }

        public JObject? FindRule(string id) => Rules.Find(r => r.Id == id).Def;
    }

    static Layer? LoadFile(IDressingFiles files, string path, SourcePos? from, List<string> stack, List<Diagnostic> diags, List<string> read, ref string? name)
    {
        var text = files.ReadText(path);
        if (text == null)
        {
            diags.Add(new(Severity.Error, "E101", $"can't read \"{path}\"", from == null ? "check the path, or create the file" : "check the import path (it is relative to the importing file)", from, from == null ? null : "imports"));
            return null;
        }
        read.Add(path);
        var root = JNode.Parse(text, path, out var parseError);
        if (root == null)
        {
            diags.Add(new(Severity.Error, "E100", $"not valid JSON: {parseError!.Value.Message}", "fix the JSON at that position (comments and trailing commas are fine)", parseError.Value.Pos));
            return null;
        }
        if (root is not JObject top)
        {
            diags.Add(new(Severity.Error, "E100", $"a rules file is an object, got {root.TypeName}", "start the file with { \"chipDressing\": 1, \"rules\": { } }", root.Pos));
            return null;
        }
        foreach (var key in top.Keys.Where(k => !TopKeys.Contains(k)))
            diags.Add(ParamReader.UnknownKey(key, TopKeys, top.KeyPos(key), key, null, "a rules file"));
        switch (top["chipDressing"])
        {
            case null:
                diags.Add(new(Severity.Warning, "W102", "no \"chipDressing\" version", $"add \"chipDressing\": {FormatVersion} at the top", top.Pos));
                break;
            case JNumber { Value: > FormatVersion } v:
                diags.Add(new(Severity.Error, "E102", $"chipDressing {v} is newer than this Chip understands ({FormatVersion})", "update Chip", v.Pos, "chipDressing"));
                return null;
            case JNumber:
                break;
            case var other:
                diags.Add(new(Severity.Error, "E201", $"chipDressing is a number, got {other.TypeName}", $"write \"chipDressing\": {FormatVersion}", other.Pos, "chipDressing"));
                break;
        }
        if (stack.Count == 0 && top["name"] is JString n) name = n.Value;

        var layer = new Layer();
        stack.Add(path);
        if (top["imports"] is { } imports)
        {
            if (imports is not JArray list || list.Items.Any(i => i is not JString))
                diags.Add(new(Severity.Error, "E201", "imports is a list of file paths", "write \"imports\": [\"../styles/industrial.dressing.json\"]", imports.Pos, "imports"));
            else
                foreach (var item in list.Items.Cast<JString>())
                {
                    string target = RulePaths.Resolve(path, item.Value);
                    if (stack.Contains(target, StringComparer.OrdinalIgnoreCase))
                    {
                        diags.Add(new(Severity.Error, "E303", $"import cycle: {string.Join(" → ", stack.Append(target))}", "remove one of the imports", item.Pos, "imports"));
                        continue;
                    }
                    var child = LoadFile(files, target, item.Pos, stack, diags, read, ref name);
                    if (child == null) continue;
                    foreach (var (k, v) in child.Vars) layer.Vars[k] = v;
                    foreach (var (id, def) in child.Rules) layer.SetRule(id, def);
                }
        }
        stack.RemoveAt(stack.Count - 1);

        if (top["vars"] is { } vars)
        {
            if (vars is JObject vo) foreach (var (k, _, v) in vo.Entries) layer.Vars[k] = v;
            else diags.Add(new(Severity.Error, "E201", $"vars is an object of names to values, got {vars.TypeName}", "write \"vars\": { \"trimHeight\": 0.12 }", vars.Pos, "vars"));
        }
        if (top["rules"] is { } rules)
        {
            if (rules is not JObject ro)
                diags.Add(new(Severity.Error, "E201", $"rules is an object keyed by rule id, got {rules.TypeName}", "write \"rules\": { \"baseboard\": { \"kind\": \"sweep\", ... } }", rules.Pos, "rules"));
            else
                foreach (var (id, idPos, value) in ro.Entries)
                {
                    if (value is not JObject def)
                    {
                        diags.Add(new(Severity.Error, "E201", $"a rule is an object, got {value.TypeName}", null, value.Pos, $"rules.{id}", Rule: id));
                        continue;
                    }
                    if (def.Has("kind")) layer.SetRule(id, def);
                    else if (layer.FindRule(id) is { } existing) layer.SetRule(id, existing.MergedWith(def));
                    else
                    {
                        var guess = Suggest.Closest(id, layer.Rules.Select(r => r.Id));
                        diags.Add(new(Severity.Error, "E302", $"rule \"{id}\" has no kind and no imported rule to change",
                            guess != null ? $"did you mean \"{guess}\"? Or add \"kind\" to define a new rule" : "add \"kind\" to define a new rule, or import the file that defines it",
                            idPos, $"rules.{id}", Rule: id));
                    }
                }
        }
        return layer;
    }

    static Rule? Bind(string id, JObject def, Dictionary<string, JNode> vars, List<Diagnostic> diags)
    {
        string path = $"rules.{id}";
        if (!TagSet.IsValid(id, out var why) || id.StartsWith(TagSet.SkipPrefix, StringComparison.Ordinal))
        {
            diags.Add(new(Severity.Error, "E203", $"\"{id}\" isn't a valid rule id: {why ?? "it can't start with skip:"}",
                TagSet.Normalize(id) is { } n && !n.StartsWith(TagSet.SkipPrefix) ? $"use \"{n}\" (lowercase letters, digits, - and _)" : "use lowercase letters, digits, - and _",
                def.Pos, path, Rule: id));
            return null;
        }
        if (Substitute(def, vars, path, diags, id) is not JObject obj) return null;

        if (obj["kind"] is not JString kindNode || RuleKind.Find(kindNode.Value) is not { } kind)
        {
            var typed = (obj["kind"] as JString)?.Value;
            var guess = typed == null ? null : Suggest.Closest(typed, RuleKind.All.Select(k => k.Name));
            diags.Add(new(Severity.Error, "E105", typed == null ? "kind must be text" : $"unknown kind \"{typed}\"",
                (guess != null ? $"did you mean \"{guess}\"? " : "") + $"(kinds: {string.Join(", ", RuleKind.All.Select(k => k.Name))})",
                obj["kind"]?.Pos ?? obj.Pos, $"{path}.kind", Rule: id));
            return null;
        }

        var allowed = RuleKind.Common.Concat(kind.Params).ToList();
        foreach (var key in obj.Keys.Where(k => allowed.All(p => p.Name != k)))
            diags.Add(ParamReader.UnknownKey(key, allowed.Select(p => p.Name), obj.KeyPos(key), $"{path}.{key}", id, $"kind \"{kind.Name}\""));

        bool enabled = obj["enabled"] is not JBool { Value: false };
        if (obj["enabled"] is { } en and not JBool)
            diags.Add(new(Severity.Error, "E201", $"enabled is true or false, got {en.TypeName}", null, en.Pos, $"{path}.enabled", Rule: id));
        string? note = (obj["note"] as JString)?.Value;

        var values = new Dictionary<string, object?>();
        foreach (var spec in kind.Params)
        {
            if (obj[spec.Name] is { } node) values[spec.Name] = ParamReader.Read(spec, node, $"{path}.{spec.Name}", diags, id) ?? spec.Default;
            else if (spec.Required)
                diags.Add(new(Severity.Error, "E204", $"{kind.Name} needs \"{spec.Name}\": {spec.Description}", Example(spec), obj.Pos, path, Rule: id));
            else values[spec.Name] = spec.Default;
        }
        return new Rule(id, kind, enabled, note, obj.Pos, values, Hash(Canonical(obj)), obj);
    }

    /// <summary>
    /// <paramref name="rule"/> with one mesh's overriding parameters (<see cref="MeshDressing.Set"/>) laid over it. Problems
    /// in the overrides come back in <paramref name="problems"/> tagged with the mesh, and the plain rule is returned.
    /// </summary>
    public Rule WithOverrides(Rule rule, string overridesJson, int meshId, string meshName, List<Diagnostic> problems)
    {
        string where = $"\"{meshName}\" overrides";
        int before = problems.Count;
        var node = JNode.Parse(overridesJson, where, out var error);
        if (node is not JObject patch)
        {
            problems.Add(new(Severity.Error, "E205", $"{where} for {rule.Id} aren't a JSON object{(error is { } e ? $": {e.Message}" : "")}",
                "reset this mesh's overrides in the inspector", MeshId: meshId, Rule: rule.Id));
            return rule;
        }
        if (patch.Has("kind"))
        {
            problems.Add(new(Severity.Error, "E205", $"{where} can't change the kind of {rule.Id}", "remove \"kind\" from the overrides",
                MeshId: meshId, Rule: rule.Id));
            return rule;
        }
        var bound = Bind(rule.Id, rule.Def.MergedWith(patch), _vars, problems);
        for (int i = before; i < problems.Count; i++) problems[i] = problems[i] with { MeshId = meshId };
        return bound != null && problems.Skip(before).All(d => !d.IsError) ? bound : rule;
    }

    static string? Example(ParamSpec spec) => spec.Type switch
    {
        ParamType.Faces => $"add \"{spec.Name}\": {{ \"tags\": [\"wall\"] }}",
        ParamType.Meshes => $"add \"{spec.Name}\": {{ \"tags\": [\"railing\"] }} and tag the meshes in object mode (Ctrl+T)",
        ParamType.Prop => $"add \"{spec.Name}\": {{ \"box\": [0.1, 1, 0.1] }} or \"props/thing.glb\"",
        ParamType.Props => $"add \"{spec.Name}\": [{{ \"box\": [0.3, 0.2, 0.3] }}, \"props/rubble.glb\"]",
        _ => null,
    };

    /// <summary>Replaces every "$name" string with that variable's value (whole values only; no expressions).</summary>
    static JNode? Substitute(JNode node, Dictionary<string, JNode> vars, string path, List<Diagnostic> diags, string rule)
    {
        switch (node)
        {
            case JString s when s.Value.StartsWith('$'):
            {
                var name = s.Value[1..];
                if (vars.TryGetValue(name, out var v)) return v;
                var guess = Suggest.Closest(name, vars.Keys);
                diags.Add(new(Severity.Error, "E301", $"no variable \"{name}\"",
                    guess != null ? $"did you mean \"${guess}\"?" : $"define it under \"vars\" (variables: {(vars.Count == 0 ? "none" : string.Join(", ", vars.Keys))})",
                    s.Pos, path, Rule: rule));
                return null;
            }
            case JObject o:
            {
                var copy = new JObject { Pos = o.Pos };
                foreach (var (k, kp, v) in o.Entries)
                    if (Substitute(v, vars, $"{path}.{k}", diags, rule) is { } sv) copy.Add(k, kp, sv);
                    else return null;
                return copy;
            }
            case JArray a:
            {
                var copy = new JArray { Pos = a.Pos };
                foreach (var item in a.Items)
                    if (Substitute(item, vars, path, diags, rule) is { } si) copy.Items.Add(si);
                    else return null;
                return copy;
            }
            default:
                return node;
        }
    }

    /// <summary>Key-sorted JSON text of a value, so equal rules hash equally whatever order or file they came from.</summary>
    static string Canonical(JNode node)
    {
        var sb = new StringBuilder();
        void Write(JNode n)
        {
            switch (n)
            {
                case JObject o:
                    sb.Append('{');
                    foreach (var (k, _, v) in o.Entries.OrderBy(e => e.Key, StringComparer.Ordinal)) { sb.Append('"').Append(k).Append("\":"); Write(v); sb.Append(','); }
                    sb.Append('}');
                    break;
                case JArray a:
                    sb.Append('[');
                    foreach (var i in a.Items) { Write(i); sb.Append(','); }
                    sb.Append(']');
                    break;
                case JString s: sb.Append('"').Append(s.Value).Append('"'); break;
                case JNumber num: sb.Append(num.Value.ToString("R", CultureInfo.InvariantCulture)); break;
                case JBool b: sb.Append(b.Value ? "true" : "false"); break;
                default: sb.Append("null"); break;
            }
        }
        Write(node);
        return sb.ToString();
    }

    internal static ulong Hash(string text)
    {
        ulong h = 14695981039346656037UL;
        foreach (var c in text) { h ^= c; h *= 1099511628211UL; }
        return h;
    }
}
