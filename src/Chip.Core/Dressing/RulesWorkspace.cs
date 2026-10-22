using System.Text.Json.Nodes;

namespace Chip.Core.Dressing;

/// <summary>
/// One rule as the editor sees it: its id, kind, definition as written with imports merged and the level's patch laid
/// over (<see cref="Raw"/>; <c>$vars</c> left in), which file defines it, and which keys the level file changes.
/// </summary>
public sealed record RuleEntry(string Id, string? Kind, JsonObject Raw, JsonObject Base, string DefinedIn, bool Local, IReadOnlySet<string> LevelKeys)
{
    public RuleKind? KindInfo => Kind == null ? null : RuleKind.Find(Kind);
    public bool Enabled => Raw["enabled"] is not JsonValue v || !v.TryGetValue<bool>(out var b) || b;
    public string? Note => Raw["note"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    /// <summary>Whether the level file patches this imported rule.</summary>
    public bool ChangedInLevel => !Local && LevelKeys.Count > 0;
}

/// <summary>
/// The level's rules file and the style files it imports, as editable documents, for the Dressing rules window. Edits
/// go to the level file: a rule the level defines is changed in place, a rule from a style is changed by a patch in the
/// level file (only the keys that differ; <see cref="Reset"/> removes them). With <c>style: true</c> an edit goes to the
/// style file that defines the rule instead. <see cref="Changes"/> gives the new text of every file that changed; the
/// editor writes them as one undo step. Never throws on file content: a file that isn't valid JSON can't be edited and
/// says why (<see cref="MainError"/>).
/// </summary>
public sealed class RulesWorkspace
{
    readonly IDressingFiles _files;
    readonly Dictionary<string, RulesDocument?> _docs = new(StringComparer.OrdinalIgnoreCase);
    List<RuleEntry>? _entries;
    Dictionary<string, (JsonNode? Value, string DefinedIn)>? _vars;

    public RulesWorkspace(IDressingFiles files, string mainPath, string? levelName = null)
    {
        _files = files;
        MainPath = RulePaths.Normalize(mainPath);
        var text = files.ReadText(MainPath);
        Exists = text != null;
        string? error = null;
        Main = text == null ? RulesDocument.New(MainPath, levelName == null ? null : $"{levelName} dressing") : RulesDocument.Parse(MainPath, text, out error);
        MainError = Main == null ? $"{Path.GetFileName(MainPath)} isn't valid JSON ({error}). Fix it in a text editor first." : null;
        _docs[MainPath] = Main;
    }

    public string MainPath { get; }
    /// <summary>Whether the level's rules file exists yet (the first edit creates it).</summary>
    public bool Exists { get; }
    /// <summary>The level's file, or null when it can't be parsed (<see cref="MainError"/> says why).</summary>
    public RulesDocument? Main { get; }
    public string? MainError { get; }
    public bool CanEdit => Main != null;

    /// <summary>A rules file by normalised path (null when missing or not valid JSON).</summary>
    public RulesDocument? Doc(string path)
    {
        path = RulePaths.Normalize(path);
        if (_docs.TryGetValue(path, out var doc)) return doc;
        var text = _files.ReadText(path);
        doc = text == null ? null : RulesDocument.Parse(path, text, out _);
        _docs[path] = doc;
        return doc;
    }

    /// <summary>The new text of every file an edit changed.</summary>
    public IReadOnlyList<(string Path, string Text)> Changes() =>
        _docs.Values.Where(d => d is { Changed: true }).Select(d => (d!.Path, d.Write())).ToList();

    // ---- Reading ----

    /// <summary>Every rule, imported ones first, in the order the dresser runs them (broken ones included).</summary>
    public IReadOnlyList<RuleEntry> Rules
    {
        get
        {
            if (_entries == null) Build();
            return _entries!;
        }
    }

    public RuleEntry? Find(string id) => Rules.FirstOrDefault(r => r.Id == id);

    /// <summary>Every variable rules can use, with the file that sets it (the level's own win).</summary>
    public IReadOnlyDictionary<string, (JsonNode? Value, string DefinedIn)> Vars
    {
        get
        {
            if (_vars == null) Build();
            return _vars!;
        }
    }

    /// <summary>Files the level imports, resolved, in order (direct imports only).</summary>
    public IReadOnlyList<string> ImportPaths => Main?.Imports.Select(i => RulePaths.Resolve(MainPath, i)).ToList() ?? [];

    /// <summary>A value as rules see it: a <c>"$name"</c> string becomes that variable's value.</summary>
    public JsonNode? Resolve(JsonNode? value) =>
        value is JsonValue v && v.TryGetValue<string>(out var s) && s.StartsWith('$') && Vars.TryGetValue(s[1..], out var var) ? var.Value : value;

    /// <summary>The variable a value refers to (<c>"$trim"</c> → trim), or null.</summary>
    public static string? VarName(JsonNode? value) =>
        value is JsonValue v && v.TryGetValue<string>(out var s) && s.StartsWith('$') && s.Length > 1 ? s[1..] : null;

    void Build()
    {
        var rules = new List<(string Id, JsonObject Raw, string DefinedIn)>();
        var vars = new Dictionary<string, (JsonNode?, string)>(StringComparer.Ordinal);
        var levelKeys = new Dictionary<string, HashSet<string>>();
        var bases = new Dictionary<string, JsonObject>();
        void Load(string path, List<string> stack)
        {
            if (Doc(path) is not { } doc || stack.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
            stack.Add(path);
            foreach (var import in doc.Imports) Load(RulePaths.Resolve(path, import), stack);
            stack.RemoveAt(stack.Count - 1);
            foreach (var (name, value) in doc.Vars) vars[name] = (value?.DeepClone(), path);
            bool main = stack.Count == 0;
            foreach (var id in doc.RuleIds)
            {
                var entry = doc.Rule(id)!;
                int i = rules.FindIndex(r => r.Id == id);
                if (entry.ContainsKey("kind"))
                {
                    var item = (id, (JsonObject)entry.DeepClone(), path);
                    if (i >= 0) rules[i] = item; else rules.Add(item);
                    levelKeys.Remove(id);
                    bases.Remove(id);
                }
                else if (i >= 0)
                {
                    if (main)
                    {
                        bases[id] = (JsonObject)rules[i].Raw.DeepClone();
                        levelKeys[id] = entry.Select(kv => kv.Key).ToHashSet();
                    }
                    rules[i] = (id, rules[i].Raw.MergedWith(entry), rules[i].DefinedIn);
                }
            }
        }
        Load(MainPath, []);
        _vars = vars;
        _entries = rules.Select(r => new RuleEntry(r.Id, Text(r.Raw["kind"]), r.Raw, bases.GetValueOrDefault(r.Id) ?? r.Raw,
            r.DefinedIn, string.Equals(r.DefinedIn, MainPath, StringComparison.OrdinalIgnoreCase),
            levelKeys.TryGetValue(r.Id, out var keys) ? keys : new HashSet<string>())).ToList();
    }

    static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    void Changed() { _entries = null; _vars = null; }

    // ---- Editing ----

    /// <summary>
    /// Sets one key of a rule (null removes it, so the default applies). A rule from a style gets a level patch holding only
    /// keys that differ from the style; with <paramref name="style"/> the style file itself changes. False if nothing could change.
    /// </summary>
    public bool Set(string id, string key, JsonNode? value, bool style = false)
    {
        Problem = null;
        if (Main == null || Find(id) is not { } rule) return false;
        if (rule.Local) Main.SetValue(id, key, value);
        else if (style)
        {
            if (Doc(rule.DefinedIn) is not { } doc) return false;
            doc.SetValue(id, key, value);
        }
        else
        {
            var baseValue = rule.Base[key];
            bool same = value == null ? baseValue == null : baseValue != null && JsonNode.DeepEquals(baseValue, value);
            if (same) Main.SetValue(id, key, null);
            else
            {
                var patch = value ?? DefaultJson(rule, key);
                // A patch merges into the style's object, so it can add and change keys but never take one away.
                if (patch is JsonObject v && baseValue is JsonObject b && b.Select(kv => kv.Key).FirstOrDefault(k => !v.ContainsKey(k)) is { } gone)
                {
                    Problem = $"A level can't take \"{gone}\" out of the style's {key}. Edit the style, or duplicate the rule into the level.";
                    return false;
                }
                Main.SetValue(id, key, patch);
            }
        }
        Changed();
        return true;
    }

    /// <summary>Why the last edit couldn't be made, for the status bar (null when it could, or failed for no user-facing reason).</summary>
    public string? Problem { get; private set; }

    /// <summary>Switches a rule on or off (for a style's rule, in this level only unless <paramref name="style"/>).</summary>
    public bool SetEnabled(string id, bool on, bool style = false)
    {
        if (Find(id) is not { } rule) return false;
        bool baseOn = rule.Base["enabled"] is not JsonValue v || !v.TryGetValue<bool>(out var b) || b;
        bool local = rule.Local || style;
        JsonNode? value = on ? (local || baseOn ? null : JsonValue.Create(true)) : JsonValue.Create(false);
        if (!local && on == baseOn) value = null;
        if (!local) { Main?.SetValue(id, "enabled", value); Changed(); return Main != null; }
        return Set(id, "enabled", value, style);
    }

    /// <summary>Takes away the level's change to one key of a style's rule (or every change with <paramref name="key"/> null).</summary>
    public void Reset(string id, string? key = null)
    {
        if (Main == null || Find(id) is not { Local: false } rule) return;
        foreach (var k in key == null ? rule.LevelKeys.ToList() : [key]) Main.SetValue(id, k, null);
        Changed();
    }

    /// <summary>Adds a rule to the level file.</summary>
    public void Add(string id, JsonObject definition)
    {
        Main?.SetRule(id, definition);
        Changed();
    }

    /// <summary>Removes a rule. A style's rule can only go with <paramref name="style"/> (it leaves the style for every level).</summary>
    public bool Delete(string id, bool style = false)
    {
        if (Main == null || Find(id) is not { } rule) return false;
        if (!rule.Local)
        {
            if (!style || Doc(rule.DefinedIn) is not { } doc) return false;
            doc.RemoveRule(id);
        }
        Main.RemoveRule(id);
        Changed();
        return true;
    }

    /// <summary>Renames a rule (the level's patch of a style rule follows when the style one is renamed).</summary>
    public bool Rename(string id, string newId, bool style = false)
    {
        if (Main == null || Find(id) is not { } rule || Find(newId) != null || !IsValidId(newId)) return false;
        if (!rule.Local)
        {
            if (!style || Doc(rule.DefinedIn) is not { } doc) return false;
            doc.RenameRule(id, newId);
        }
        Main.RenameRule(id, newId);
        Changed();
        return true;
    }

    /// <summary>A copy of a rule (as it applies in this level) defined in the level file under <paramref name="newId"/>.</summary>
    public bool Duplicate(string id, string newId)
    {
        if (Main == null || Find(id) is not { } rule || Find(newId) != null) return false;
        Add(newId, Rebase(rule.Raw, rule.DefinedIn, MainPath));
        return true;
    }

    /// <summary>Sets one of the level's variables (overriding a style's), or removes it with null.</summary>
    public void SetVar(string name, JsonNode? value)
    {
        Main?.SetVar(name, value);
        Changed();
    }

    /// <summary>Replaces the level's imports with <paramref name="paths"/> (absolute or relative to the level's rules file).</summary>
    public void SetImports(IEnumerable<string> paths)
    {
        if (Main == null) return;
        Main.Imports = paths.Select(p => Path.IsPathRooted(p) ? Relative(MainPath, p) : p).ToList();
        Changed();
    }

    /// <summary>
    /// Moves the level's own rules and variables into a new style file at <paramref name="stylePath"/> and imports it, so
    /// other levels can use the same look. The level keeps its patches of other styles' rules. False when the file exists.
    /// </summary>
    public bool SaveAsStyle(string stylePath, string name)
    {
        if (Main == null) return false;
        stylePath = RulePaths.Normalize(stylePath);
        if (_files.ReadText(stylePath) != null) return false;
        var style = RulesDocument.New(stylePath, name);
        _docs[stylePath] = style;
        foreach (var (var, value) in Main.Vars.ToList())
        {
            style.SetVar(var, value);
            Main.SetVar(var, null);
        }
        foreach (var id in Main.RuleIds.Where(Main.Defines).ToList())
        {
            style.SetRule(id, Rebase(Main.Rule(id)!, MainPath, stylePath));
            Main.RemoveRule(id);
        }
        Main.Imports = Main.Imports.Append(Relative(MainPath, stylePath)).ToList();
        Changed();
        return true;
    }

    /// <summary><paramref name="baseId"/>, or with -2, -3 ... added until no rule has the id.</summary>
    public string UniqueId(string baseId)
    {
        if (Find(baseId) == null) return baseId;
        for (int i = 2; ; i++)
            if (Find($"{baseId}-{i}") == null) return $"{baseId}-{i}";
    }

    public static bool IsValidId(string id) => Geometry.TagSet.IsValid(id, out _) && !id.StartsWith(Geometry.TagSet.SkipPrefix, StringComparison.Ordinal);

    /// <summary>The value the loader uses for <paramref name="key"/> when the rule doesn't give one, as JSON.</summary>
    public static JsonNode? DefaultJson(RuleEntry rule, string key) => key switch
    {
        "enabled" => JsonValue.Create(true),
        _ => rule.KindInfo?.Params.FirstOrDefault(p => p.Name == key) is { } spec ? ParamJson.Default(spec) : null,
    };

    /// <summary><paramref name="to"/> as a path relative to <paramref name="fromFile"/>'s folder, with forward slashes.</summary>
    public static string Relative(string fromFile, string to)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(fromFile)) ?? "";
        return Path.GetRelativePath(dir, Path.GetFullPath(to)).Replace('\\', '/');
    }

    /// <summary>A rule copied from one file to another, with prop file paths rewritten so they point at the same files.</summary>
    static JsonObject Rebase(JsonObject rule, string fromFile, string toFile)
    {
        var copy = (JsonObject)rule.DeepClone();
        if (string.Equals(RulePaths.Normalize(fromFile), RulePaths.Normalize(toFile), StringComparison.OrdinalIgnoreCase)) return copy;
        JsonNode? Fix(JsonNode? n)
        {
            if (n is JsonValue v && v.TryGetValue<string>(out var s) && ParamJson.IsPropFile(s) && !Path.IsPathRooted(s))
                return JsonValue.Create(Relative(toFile, RulePaths.Resolve(fromFile, s)));
            if (n is JsonArray a) return new JsonArray(a.Select(i => Fix(i?.DeepClone())).ToArray());
            return n;
        }
        foreach (var key in new[] { "prop", "props" })
            if (copy[key] is { } value) copy[key] = Fix(value.DeepClone());
        return copy;
    }
}
