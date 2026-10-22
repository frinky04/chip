using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Chip.Core.Dressing;

/// <summary>
/// One rules file as editable JSON, for the editor's Dressing rules window. <see cref="Write"/> saves it in Chip's
/// canonical layout: top-level keys and each rule's keys in a fixed order (kind first, then the kind's parameters in
/// table order), one parameter per line, short values on one line. Comments are not kept (use <c>note</c>). Agents and
/// people can still edit the file by hand; Chip reloads it as before.
/// </summary>
public sealed class RulesDocument
{
    readonly JsonObject _root;

    RulesDocument(string path, JsonObject root)
    {
        Path = path;
        _root = root;
    }

    /// <summary>Normalised path of the file (<see cref="RulePaths.Normalize"/>).</summary>
    public string Path { get; }

    /// <summary>Whether something changed since it was read.</summary>
    public bool Changed { get; private set; }

    /// <summary>An empty rules file (not yet written).</summary>
    public static RulesDocument New(string path, string? name)
    {
        var root = new JsonObject { ["$schema"] = "./" + DressingSchema.FileName, ["chipDressing"] = RuleSet.FormatVersion };
        if (name != null) root["name"] = name;
        root["rules"] = new JsonObject();
        return new RulesDocument(RulePaths.Normalize(path), root) { Changed = true };
    }

    static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Parses a rules file; null with <paramref name="error"/> set when it isn't a JSON object.</summary>
    public static RulesDocument? Parse(string path, string text, out string? error)
    {
        error = null;
        try
        {
            if (JsonNode.Parse(text, null, ReadOptions) is JsonObject root) return new RulesDocument(RulePaths.Normalize(path), root);
            error = "the file isn't a JSON object";
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            error = e.Message;
        }
        return null;
    }

    public string? Name
    {
        get => _root["name"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        set { if (value == Name) return; if (value == null) _root.Remove("name"); else _root["name"] = value; Changed = true; }
    }

    /// <summary>The imported files as written (relative to this file).</summary>
    public IReadOnlyList<string> Imports
    {
        get => _root["imports"] is JsonArray a ? a.Select(i => i is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList() : [];
        set
        {
            if (value.SequenceEqual(Imports)) return;
            if (value.Count == 0) _root.Remove("imports");
            else _root["imports"] = new JsonArray(value.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());
            Changed = true;
        }
    }

    /// <summary>The file's own variables (name → value as written).</summary>
    public IReadOnlyList<(string Name, JsonNode? Value)> Vars =>
        _root["vars"] is JsonObject o ? o.Select(kv => (kv.Key, kv.Value)).ToList() : [];

    /// <summary>Sets (or with null removes) one of the file's variables.</summary>
    public void SetVar(string name, JsonNode? value)
    {
        var vars = _root["vars"] as JsonObject;
        if (value == null)
        {
            if (vars == null || !vars.ContainsKey(name)) return;
            vars.Remove(name);
            if (vars.Count == 0) _root.Remove("vars");
            Changed = true;
            return;
        }
        if (vars == null) Insert(_root, "vars", vars = new JsonObject(), before: "rules");
        if (vars[name] is { } old && JsonNode.DeepEquals(old, value)) return;
        vars[name] = value.DeepClone();
        Changed = true;
    }

    JsonObject Rules
    {
        get
        {
            if (_root["rules"] is JsonObject r) return r;
            _root["rules"] = r = new JsonObject();
            return r;
        }
    }

    /// <summary>Rule ids in file order (definitions and patches).</summary>
    public IReadOnlyList<string> RuleIds => _root["rules"] is JsonObject r ? r.Select(kv => kv.Key).ToList() : [];

    /// <summary>The rule's entry as written (a definition, with <c>kind</c>, or a patch of an imported rule), or null.</summary>
    public JsonObject? Rule(string id) => _root["rules"] is JsonObject r ? r[id] as JsonObject : null;

    /// <summary>Whether this file defines the rule (its entry has a kind) rather than patching an imported one.</summary>
    public bool Defines(string id) => Rule(id)?.ContainsKey("kind") == true;

    /// <summary>Adds the rule at the end (or replaces the entry with that id where it is).</summary>
    public void SetRule(string id, JsonObject entry)
    {
        var rules = Rules;
        if (rules[id] is { } old && JsonNode.DeepEquals(old, entry)) return;
        if (rules.ContainsKey(id)) rules[id] = entry.DeepClone();
        else rules.Add(id, entry.DeepClone());
        Changed = true;
    }

    public void RemoveRule(string id)
    {
        if (_root["rules"] is JsonObject r && r.Remove(id)) Changed = true;
    }

    /// <summary>Renames a rule entry in place, keeping its position in the file.</summary>
    public void RenameRule(string id, string newId)
    {
        if (id == newId || _root["rules"] is not JsonObject r || r[id] is not { } entry) return;
        var copy = new JsonObject();
        foreach (var (k, v) in r.ToList())
            copy.Add(k == id ? newId : k, (k == id ? entry : v)?.DeepClone());
        _root["rules"] = copy;
        Changed = true;
    }

    /// <summary>
    /// Sets one key of the rule's entry, creating the entry (a patch) if there is none; null removes the key, and an emptied
    /// patch is removed.
    /// </summary>
    public void SetValue(string id, string key, JsonNode? value)
    {
        var entry = Rule(id);
        if (value == null)
        {
            if (entry == null || !entry.ContainsKey(key)) return;
            entry.Remove(key);
            if (entry.Count == 0) RemoveRule(id);
            Changed = true;
            return;
        }
        if (entry == null) Rules.Add(id, entry = new JsonObject());
        if (entry[key] is { } old && JsonNode.DeepEquals(old, value)) return;
        entry[key] = value.DeepClone();
        Changed = true;
    }

    static void Insert(JsonObject obj, string key, JsonNode value, string before)
    {
        var entries = obj.ToList();
        obj.Clear();
        bool done = false;
        foreach (var (k, v) in entries)
        {
            if (!done && k == before) { obj.Add(key, value); done = true; }
            obj.Add(k, v);
        }
        if (!done) obj.Add(key, value);
    }

    // ---- Writing ----

    static readonly string[] TopOrder = ["$schema", "chipDressing", "name", "note", "imports", "vars", "rules"];
    static readonly JsonSerializerOptions Values = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    const int Width = 110;

    /// <summary>The file in canonical layout.</summary>
    public string Write()
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        var keys = Ordered(_root, TopOrder).ToList();
        for (int i = 0; i < keys.Count; i++)
        {
            var (k, v) = keys[i];
            sb.Append("  ").Append(Key(k)).Append(": ");
            if (k == "rules" && v is JsonObject rules) WriteRules(sb, rules);
            else if (k == "vars" && v is JsonObject vars) WriteExpanded(sb, vars, 1, depth: 1);
            else WriteValue(sb, v, 1, ("  " + Key(k) + ": ").Length);
            sb.Append(i < keys.Count - 1 ? ",\n" : "\n");
        }
        sb.Append("}\n");
        return sb.ToString();
    }

    static void WriteRules(StringBuilder sb, JsonObject rules)
    {
        if (rules.Count == 0) { sb.Append("{}"); return; }
        sb.Append("{\n");
        int i = 0;
        foreach (var (id, entry) in rules)
        {
            sb.Append("    ").Append(Key(id)).Append(": ");
            if (entry is JsonObject rule) WriteExpanded(sb, rule, 2, depth: 1, RuleOrder(rule));
            else WriteValue(sb, entry, 2, 0);
            sb.Append(++i < rules.Count ? ",\n" : "\n");
        }
        sb.Append("  }");
    }

    /// <summary>kind, enabled, note, then the kind's parameters in table order, then anything else as written.</summary>
    static string[] RuleOrder(JsonObject rule)
    {
        var kind = rule["kind"] is JsonValue v && v.TryGetValue<string>(out var s) ? RuleKind.Find(s) : null;
        var all = RuleKind.Common.Select(p => p.Name).ToList();
        if (kind != null) all.AddRange(kind.Params.Select(p => p.Name));
        else foreach (var k in RuleKind.All) all.AddRange(k.Params.Select(p => p.Name));
        return all.Distinct().ToArray();
    }

    static IEnumerable<(string Key, JsonNode? Value)> Ordered(JsonObject obj, string[] order)
    {
        foreach (var k in order) if (obj.TryGetPropertyValue(k, out var v)) yield return (k, v);
        foreach (var (k, v) in obj) if (!order.Contains(k)) yield return (k, v);
    }

    /// <summary>An object with one key per line at <paramref name="indent"/> levels (two spaces each).</summary>
    static void WriteExpanded(StringBuilder sb, JsonObject obj, int indent, int depth, string[]? order = null)
    {
        if (obj.Count == 0) { sb.Append("{}"); return; }
        string pad = new(' ', (indent + 1) * 2);
        sb.Append("{\n");
        var entries = (order != null ? Ordered(obj, order) : obj.Select(kv => (kv.Key, kv.Value))).ToList();
        for (int i = 0; i < entries.Count; i++)
        {
            var (k, v) = entries[i];
            string head = pad + Key(k) + ": ";
            sb.Append(head);
            WriteValue(sb, v, indent + 1, head.Length);
            sb.Append(i < entries.Count - 1 ? ",\n" : "\n");
        }
        sb.Append(new string(' ', indent * 2)).Append('}');
    }

    /// <summary>A value on one line when it fits, else expanded with each item written the same way.</summary>
    static void WriteValue(StringBuilder sb, JsonNode? v, int indent, int column)
    {
        string inline = Inline(v);
        if (column + inline.Length <= Width || v is not (JsonObject or JsonArray)) { sb.Append(inline); return; }
        string pad = new(' ', (indent + 1) * 2);
        if (v is JsonArray a)
        {
            sb.Append("[\n");
            for (int i = 0; i < a.Count; i++)
            {
                sb.Append(pad);
                WriteValue(sb, a[i], indent + 1, pad.Length);
                sb.Append(i < a.Count - 1 ? ",\n" : "\n");
            }
            sb.Append(new string(' ', indent * 2)).Append(']');
        }
        else WriteExpanded(sb, (JsonObject)v, indent, 2);
    }

    static string Inline(JsonNode? v) => v switch
    {
        null => "null",
        JsonObject o => o.Count == 0 ? "{}" : "{ " + string.Join(", ", o.Select(kv => Key(kv.Key) + ": " + Inline(kv.Value))) + " }",
        JsonArray a => "[" + string.Join(", ", a.Select(Inline)) + "]",
        JsonValue jv when jv.GetValueKind() == JsonValueKind.Number => Number(jv),
        _ => v.ToJsonString(Values),
    };

    static string Number(JsonValue v)
    {
        double d = double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture);
        return d == Math.Floor(d) && Math.Abs(d) < 1e15 ? ((long)d).ToString(CultureInfo.InvariantCulture) : d.ToString("R", CultureInfo.InvariantCulture);
    }

    static string Key(string k) => JsonValue.Create(k)!.ToJsonString(Values);
}

/// <summary>Conversions between the loader's position-keeping <see cref="JNode"/> values and editable JSON nodes.</summary>
public static class JNodeJson
{
    public static JsonNode? ToJson(this JNode node) => node switch
    {
        JObject o => new JsonObject(o.Entries.Select(e => KeyValuePair.Create(e.Key, e.Value.ToJson()))),
        JArray a => new JsonArray(a.Items.Select(i => i.ToJson()).ToArray()),
        JString s => JsonValue.Create(s.Value),
        JNumber n => JsonValue.Create(n.Value),
        JBool b => JsonValue.Create(b.Value),
        _ => null,
    };

    /// <summary>A copy of <paramref name="a"/> with <paramref name="patch"/>'s keys laid over it; nested objects merge (as imports do).</summary>
    public static JsonObject MergedWith(this JsonObject a, JsonObject patch)
    {
        var result = (JsonObject)a.DeepClone();
        foreach (var (k, v) in patch)
            result[k] = result[k] is JsonObject x && v is JsonObject y ? x.MergedWith(y) : v?.DeepClone();
        return result;
    }
}
