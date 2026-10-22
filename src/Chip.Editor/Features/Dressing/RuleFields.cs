using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using Chip.Core.Dressing;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using NativeFileDialogSharp;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.Dressing;

/// <summary>
/// The Dressing rules window's fields for one rule: every parameter of its kind in words and units (lengths in cm, angles
/// in degrees), "Where" instead of edges + onto, tags as chips, a profile picker with a preview, prop editors. Values are
/// read from the rule as written (styles merged, the level's changes laid over) and each change is one undo step written
/// to the rules file. A value the level changes from its style shows in orange with a reset; a value taken from a shared
/// variable shows the variable's name.
/// </summary>
sealed class RuleFields(DressingFeature feature)
{
    readonly Dictionary<string, Vector4> _drafts = new();
    readonly Dictionary<string, string> _textDrafts = new();
    string _tagQuery = "";
    readonly HashSet<string> _expanded = new();

    EditorContext _ctx = null!;
    RulesWorkspace _ws = null!;
    RuleEntry _rule = null!;
    bool _style;
    IReadOnlyList<string> _levelTags = [];

    static float LabelW => Theme.Px(108);

    public void Draw(EditorContext ctx, RulesWorkspace ws, RuleEntry rule, bool style, IReadOnlyList<string> levelTags)
    {
        _ctx = ctx; _ws = ws; _rule = rule; _style = style; _levelTags = levelTags;
        if (rule.KindInfo is not { } kind)
        {
            ImGui.TextColored(Theme.Error, rule.Kind == null ? "This rule has no kind." : $"Unknown kind \"{rule.Kind}\".");
            ImGui.TextColored(Theme.TextDim, "Fix it in the rules file (Open JSON).");
            return;
        }
        bool trimLike = kind is SweepKind or RepeatKind;
        var place = trimLike ? EdgePlaces.Of(Value("edges"), Value("onto")) : null;
        string edges = ParamJson.Text(Value("edges")) ?? (kind is BevelKind ? "convex" : "contact");
        foreach (var spec in kind.Params)
        {
            if (spec.Type == ParamType.Numbers) continue;                                  // weights live with the props
            if (trimLike && spec.Name == "onto" && (place != null || edges != "contact")) continue;
            if (spec.Name == "minDeg" && edges is not ("convex" or "concave")) continue;
            ImGui.PushID(spec.Name);
            if (trimLike && spec.Name == "edges") WhereRow(spec, place);
            else Row(spec);
            ImGui.PopID();
        }
    }

    // ---- Values ----

    /// <summary>The value as rules see it (variables resolved), or null when the rule doesn't set it.</summary>
    JsonNode? Value(string key) => _ws.Resolve(_rule.Raw[key]);

    JsonNode? ValueOrDefault(ParamSpec spec) => Value(spec.Name) ?? ParamJson.Default(spec);

    void Set(string key, JsonNode? value, string? label = null) =>
        feature.EditRules($"Dressing: {_rule.Id} {label ?? key}", w => w.Set(_rule.Id, key, value, _style));

    void SetMany(string label, params (string Key, JsonNode? Value)[] values) =>
        feature.EditRules($"Dressing: {_rule.Id} {label}", w =>
        {
            foreach (var (k, v) in values)
                if (!w.Set(_rule.Id, k, v, _style)) return false;
            return true;
        });

    /// <summary>The file a value is written to (prop paths are relative to it).</summary>
    string TargetFile => _style && !_rule.Local ? _rule.DefinedIn : _ws.MainPath;

    // ---- Rows ----

    void Label(ParamSpec spec, string? key = null)
    {
        key ??= spec.Name;
        bool changed = _rule.LevelKeys.Contains(key);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(changed ? Theme.Accent : Theme.TextDim, spec.DisplayLabel);
        Tip(spec.Description + (spec.Required ? "\nRequired." : "") + (changed ? "\nChanged in this level (orange): × puts the style's value back." : ""));
        ImGui.SameLine(LabelW);
    }

    static float SideW => Theme.Px(24);

    /// <summary>The width left for a row's widget: every row keeps a side column at the right, so fields never change width.</summary>
    static float Avail() => ImGui.GetContentRegionAvail().X - SideW - ImGui.GetStyle().ItemSpacing.X;

    bool Resettable(string key) => _rule.LevelKeys.Contains(key) && !_style;

    /// <summary>
    /// The side column: × puts the style's value back when this level changes it; $ opens the shared value the field uses.
    /// </summary>
    void Side(params string[] keys)
    {
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - SideW);
        if (keys.FirstOrDefault(Resettable) is { } key)
        {
            var old = _rule.Base[key];
            if (ImGui.Button("×##reset", new Vector2(SideW, 0)))
                feature.EditRules($"Dressing: reset {_rule.Id} {key}", w => { foreach (var k in keys) w.Reset(_rule.Id, k); return true; });
            Tip($"Back to the style's value{(old == null ? " (the default)" : $": {Describe(old)}")}");
        }
        else if (keys.Select(k => RulesWorkspace.VarName(_rule.Raw[k])).FirstOrDefault(v => v != null) is { } var)
        {
            if (ImGui.Button("$##var", new Vector2(SideW, 0))) feature.Window.ShowSharedValues();
            Tip($"Uses the shared value \"{var}\" ({Describe(_ws.Resolve(_rule.Raw[keys[0]]))}). Click to edit it for every rule using it; changing the field here gives this rule its own value.");
        }
        else ImGui.Dummy(new Vector2(SideW, ImGui.GetFrameHeight()));
    }

    static string Describe(JsonNode? value) =>
        ParamJson.Text(value) ?? value?.ToJsonString().Replace(",", ", ").Replace(":", ": ") ?? "nothing";

    /// <summary>In style mode a value this level overrides can't be edited (the level's value would still win).</summary>
    bool Locked(string key) => _style && _rule.LevelKeys.Contains(key);

    void Row(ParamSpec spec)
    {
        Label(spec);
        bool locked = Locked(spec.Name);
        if (locked) ImGui.BeginDisabled();
        float w = Avail();
        var value = ValueOrDefault(spec);
        switch (spec.Type)
        {
            case ParamType.Number:
                ImGui.SetNextItemWidth(w);
                if (NumberField("##v", ParamJson.Number(value) ?? 0, spec.Unit, spec.Min, spec.Max, out var n)) Set(spec.Name, ParamJson.Num(n), spec.DisplayLabel);
                break;
            case ParamType.Int:
            {
                ImGui.SetNextItemWidth(w);
                int v = (int)(ParamJson.Number(value) ?? 0);
                if (Drag("##v", v, out var d, 0.1f, (float)Math.Max(spec.Min, -1e6), (float)Math.Min(spec.Max, 1e6), "%.0f"))
                    Set(spec.Name, JsonValue.Create((int)Math.Round(d)), spec.DisplayLabel);
                break;
            }
            case ParamType.Bool:
            {
                bool v = ParamJson.Bool(value) ?? false;
                if (ImGui.Checkbox("##v", ref v)) Set(spec.Name, JsonValue.Create(v), spec.DisplayLabel);
                break;
            }
            case ParamType.Choice:
            {
                string current = ParamJson.Text(value) ?? "";
                ImGui.SetNextItemWidth(w);
                if (ImGui.BeginCombo("##v", spec.ChoiceLabel(current)))
                {
                    foreach (var choice in spec.Choices!)
                        if (ImGui.Selectable(spec.ChoiceLabel(choice), choice == current) && choice != current) Set(spec.Name, JsonValue.Create(choice), spec.DisplayLabel);
                    ImGui.EndCombo();
                }
                break;
            }
            case ParamType.Text:
                if (spec.Name == "material") MaterialField(spec, ParamJson.Text(Value(spec.Name)), w);
                else
                {
                    ImGui.SetNextItemWidth(w);
                    if (TextField("##v", ParamJson.Text(Value(spec.Name)) ?? "", "", out var t)) Set(spec.Name, t.Length == 0 ? null : JsonValue.Create(t), spec.DisplayLabel);
                }
                break;
            case ParamType.Pair:
                PairField(spec, ParamJson.Pair(_ws.ResolveDeep(value)) ?? Vector2.Zero, w);
                break;
            case ParamType.Faces:
                FacesField(spec, Value(spec.Name) as JsonObject ?? (ParamJson.Default(spec) as JsonObject) ?? new JsonObject());
                break;
            case ParamType.Meshes:
                MeshesField(spec, Value(spec.Name) as JsonObject ?? new JsonObject());
                break;
            case ParamType.Profile:
                ProfileField(spec, value as JsonObject, w);
                break;
            case ParamType.Prop:
                PropField(spec.Name, Value(spec.Name), w, v => Set(spec.Name, v, spec.DisplayLabel));
                break;
            case ParamType.Props:
                PropsField(spec);
                break;
        }
        if (locked)
        {
            ImGui.EndDisabled();
            Tip("This level changes this value, so editing the shared style here wouldn't show. Switch to \"This level\" and reset it first.");
        }
        Side(spec.Name);
    }

    // ---- Numbers ----

    static (float Scale, string Format, float Speed) Display(ParamUnit unit) => unit switch
    {
        ParamUnit.Length => (100, "%.1f cm", 0.1f),
        ParamUnit.Degrees => (1, "%.0f°", 0.5f),
        ParamUnit.PerM2 => (1, "%.2f per m²", 0.01f),
        ParamUnit.Scale => (1, "%.2f×", 0.01f),
        _ => (1, "%.3g", 0.01f),
    };

    /// <summary>
    /// A number shown in its unit (metres as cm), committed when the drag or typing ends. <paramref name="label"/> is shown
    /// inside the field ("depth 1.5 cm"), for fields that sit side by side.
    /// </summary>
    bool NumberField(string id, double value, ParamUnit unit, double min, double max, out double result, string? label = null)
    {
        var (scale, fmt, speed) = Display(unit);
        if (label != null) fmt = $"{label} {fmt}";
        float lo = (float)Math.Max(min * scale, -1e6), hi = (float)Math.Min(max * scale, 1e6);
        bool done = Drag(id, (float)(value * scale), out var shown, speed, lo, hi, fmt);
        result = shown / scale;
        return done;
    }

    /// <summary>A drag field that keeps its value while dragging and reports it once, on release (one undo step).</summary>
    bool Drag(string id, float value, out float result, float speed, float min, float max, string format)
    {
        string key = ImGui.GetID(id).ToString(CultureInfo.InvariantCulture);
        float v = _drafts.TryGetValue(key, out var d) ? d.X : value;
        if (ImGui.DragFloat(id, ref v, speed, min, max, format, ImGuiSliderFlags.AlwaysClamp)) _drafts[key] = new Vector4(v);
        result = v;
        if (ImGui.IsItemDeactivatedAfterEdit() && _drafts.Remove(key)) return true;
        if (!ImGui.IsItemActive()) _drafts.Remove(key);
        return false;
    }

    /// <summary>A text field committed on Enter or leaving it.</summary>
    bool TextField(string id, string value, string hint, out string result)
    {
        string key = ImGui.GetID(id).ToString(CultureInfo.InvariantCulture);
        string v = _textDrafts.TryGetValue(key, out var d) ? d : value;
        if (ImGui.InputTextWithHint(id, hint, ref v, 256)) _textDrafts[key] = v;
        result = v.Trim();
        if (ImGui.IsItemDeactivatedAfterEdit() && _textDrafts.Remove(key)) return result != value;
        if (!ImGui.IsItemActive()) _textDrafts.Remove(key);
        return false;
    }

    void PairField(ParamSpec spec, Vector2 v, float w)
    {
        var (a, b, tipA, tipB) = spec.Name switch
        {
            "offset" => ("out", "up", "Out from the face (negative: into it)", "Along the surface it sits on: up from a floor, down from a ceiling"),
            _ => ("min", "max", "Smallest (each prop picks a random value in the range)", "Largest"),
        };
        float half = (w - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
        // Replace only the side that changed, so the other keeps a shared value if it uses one.
        var raw = Value(spec.Name) as JsonArray;
        void SetSide(int i, double n) => Set(spec.Name, new JsonArray(
            i == 0 ? ParamJson.Num(n) : raw?[0]?.DeepClone() ?? ParamJson.Num(v.X),
            i == 1 ? ParamJson.Num(n) : raw?[1]?.DeepClone() ?? ParamJson.Num(v.Y)), spec.DisplayLabel);
        ImGui.SetNextItemWidth(half);
        if (NumberField("##a", v.X, spec.Unit, double.NegativeInfinity, double.PositiveInfinity, out var x, a)) SetSide(0, x);
        Tip(tipA);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(half);
        if (NumberField("##b", v.Y, spec.Unit, double.NegativeInfinity, double.PositiveInfinity, out var y, b)) SetSide(1, y);
        Tip(tipB);
    }

    void MaterialField(ParamSpec spec, string? current, float w)
    {
        float pick = ImGui.GetFrameHeight();
        ImGui.SetNextItemWidth(w - pick - ImGui.GetStyle().ItemSpacing.X);
        string hint = _rule.KindInfo is ProxyKind ? "the replaced mesh's" : _rule.KindInfo is RepeatKind or ScatterKind ? "the prop's own" : "the face's material";
        if (TextField("##v", current ?? "", hint, out var t)) Set(spec.Name, t.Length == 0 ? null : JsonValue.Create(t), spec.DisplayLabel);
        ImGui.SameLine();
        if (IconButton("pick", Icon.ChevronDown, $"Pick a material (the active one is {_ctx.ActiveMaterial})", pick)) ImGui.OpenPopup("##materials");
        if (ImGui.BeginPopup("##materials"))
        {
            if (ImGui.Selectable($"Active: {_ctx.ActiveMaterial}")) Set(spec.Name, JsonValue.Create(_ctx.ActiveMaterial), spec.DisplayLabel);
            if (current != null && ImGui.Selectable("Default (" + hint + ")")) Set(spec.Name, null, spec.DisplayLabel);
            ImGui.Separator();
            ImGui.SetNextItemWidth(Theme.Px(220));
            ImGui.InputTextWithHint("##filter", "Filter", ref _tagQuery, 64);
            if (ImGui.BeginChild("##list", Theme.Px(220, 220)))
            {
                foreach (var m in _ctx.Materials.All)
                    if (_tagQuery.Length == 0 || m.Name.Contains(_tagQuery, StringComparison.OrdinalIgnoreCase))
                        if (ImGui.Selectable(m.Name, m.Name == current)) { Set(spec.Name, JsonValue.Create(m.Name), spec.DisplayLabel); ImGui.CloseCurrentPopup(); }
            }
            ImGui.EndChild();
            ImGui.EndPopup();
        }
    }

    // ---- Where ----

    void WhereRow(ParamSpec edgesSpec, EdgePlace? place)
    {
        ImGui.AlignTextToFramePadding();
        bool changed = _rule.LevelKeys.Contains("edges") || _rule.LevelKeys.Contains("onto");
        ImGui.TextColored(changed ? Theme.Accent : Theme.TextDim, "Where");
        Tip("Which edges of the faces get it.\n" + string.Join("\n", EdgePlaces.All.Select(p => $"{p.Label}: {p.Tip}")));
        ImGui.SameLine(LabelW);
        bool locked = Locked("edges") || Locked("onto");
        if (locked) ImGui.BeginDisabled();
        ImGui.SetNextItemWidth(Avail());
        if (ImGui.BeginCombo("##where", place?.Label ?? "Custom"))
        {
            foreach (var p in EdgePlaces.All)
            {
                if (ImGui.Selectable(p.Label, p == place) && p != place)
                {
                    var (e, o) = EdgePlaces.Values(p);
                    SetMany($"where {p.Label.ToLowerInvariant()}", ("edges", e), ("onto", o));
                }
                Tip(p.Tip);
            }
            if (ImGui.Selectable("Custom...", place == null) && place != null)
                SetMany("where custom", ("edges", JsonValue.Create("contact")), ("onto", new JsonObject { ["facing"] = "up", ["tags"] = new JsonArray("floor") }));
            Tip("Where the faces touch faces you choose by tag, material or facing.");
            ImGui.EndCombo();
        }
        if (locked) ImGui.EndDisabled();
        Side("edges", "onto");
        if (place == null && ParamJson.Text(Value("edges")) is { } custom && custom != "contact")
        {
            ImGui.PushID("edgesRaw");
            Row(edgesSpec);
            ImGui.PopID();
        }
    }

    // ---- Selectors ----

    static readonly (string? Value, string Label)[] Facings = [(null, "Any orientation"), ("upright", "Walls (upright)"), ("up", "Floors (facing up)"), ("down", "Ceilings (facing down)")];

    void FacesField(ParamSpec spec, JsonObject sel)
    {
        float x = ImGui.GetCursorPosX();
        var tags = ParamJson.Names(_ws.Resolve(sel["tags"]));
        if (TagChips("tags", tags, mesh: false, out var newTags)) SetSelector(spec, sel, "tags", newTags.Length == 0 ? null : ParamJson.List(newTags));

        ImGui.SetCursorPosX(x);
        string? facing = ParamJson.Text(_ws.Resolve(sel["facing"]));
        ImGui.SetNextItemWidth(Avail());
        if (ImGui.BeginCombo("##facing", Facings.FirstOrDefault(f => f.Value == facing).Label ?? facing))
        {
            foreach (var (value, label) in Facings)
                if (ImGui.Selectable(label, value == facing) && value != facing) SetSelector(spec, sel, "facing", value == null ? null : JsonValue.Create(value));
            ImGui.EndCombo();
        }
        Tip("Only faces pointing this way");

        var not = ParamJson.Names(_ws.Resolve(sel["notTags"]));
        var materials = ParamJson.Names(_ws.Resolve(sel["materials"]));
        string more = $"{_rule.Id}.{spec.Name}";
        if (not.Length > 0 || materials.Length > 0 || _expanded.Contains(more))
        {
            ImGui.SetCursorPosX(x);
            ImGui.TextColored(Theme.TextDim, "except");
            Tip("Faces with any of these tags are left out (tag a face skip:<rule> to leave out one rule)");
            ImGui.SameLine();
            if (TagChips("notTags", not, mesh: false, out var newNot)) SetSelector(spec, sel, "notTags", newNot.Length == 0 ? null : ParamJson.List(newNot));
            ImGui.SetCursorPosX(x);
            ImGui.SetNextItemWidth(Avail());
            if (TextField("##materials", string.Join(", ", materials), "any material (e.g. concrete*, metal_?)", out var m))
            {
                var list = m.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                SetSelector(spec, sel, "materials", list.Length == 0 ? null : ParamJson.List(list));
            }
            Tip("Only faces with one of these materials (* and ? match any text)");
        }
        else
        {
            ImGui.SetCursorPosX(x);
            if (ImGui.SmallButton("More filters")) _expanded.Add(more);
            Tip("Leave out tags, or pick faces by material");
        }
    }

    void SetSelector(ParamSpec spec, JsonObject sel, string key, JsonNode? value)
    {
        var copy = (JsonObject)sel.DeepClone();
        if (value == null) copy.Remove(key); else copy[key] = value;
        Set(spec.Name, copy, spec.DisplayLabel);
    }

    void MeshesField(ParamSpec spec, JsonObject sel)
    {
        var tags = ParamJson.Names(_ws.Resolve(sel["tags"]));
        if (TagChips("tags", tags, mesh: true, out var newTags)) SetSelector(spec, sel, "tags", newTags.Length == 0 ? null : ParamJson.List(newTags));
    }

    /// <summary>Tags as chips (× removes one) and a + that offers the level's tags or a new one. True with the new list on a change.</summary>
    bool TagChips(string id, string[] tags, bool mesh, out string[] result)
    {
        result = tags;
        ImGui.PushID(id);
        float right = ImGui.GetWindowContentRegionMax().X;
        bool changed = false;
        for (int i = 0; i < tags.Length; i++)
        {
            string text = $"{tags[i]}  ×";
            float w = ImGui.CalcTextSize(text).X + ImGui.GetStyle().FramePadding.X * 2;
            if (i > 0) { ImGui.SameLine(); if (ImGui.GetCursorPosX() + w > right) ImGui.NewLine(); }
            if (i > 0 && ImGui.GetCursorPosX() < LabelW) ImGui.SetCursorPosX(LabelW);
            var c = DressingFeature.TagColour(tags[i]) with { W = 0.35f };
            ImGui.PushStyleColor(ImGuiCol.Button, c);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, c with { W = 0.55f });
            if (ImGui.Button($"{text}##{i}")) { result = tags.Where((_, j) => j != i).ToArray(); changed = true; }
            ImGui.PopStyleColor(2);
            Tip(mesh ? $"Meshes tagged {tags[i]} (object mode, Ctrl+T). Click to remove." : $"Faces tagged {tags[i]} (Ctrl+T). Click to remove.");
        }
        if (tags.Length > 0) ImGui.SameLine();
        if (ImGui.Button(tags.Length == 0 ? "+ tag" : "+")) { ImGui.OpenPopup("##addTag"); _tagQuery = ""; }
        Tip(tags.Length == 0 ? "Choose a tag. Without one the rule takes every face." : "Also require this tag");
        if (ImGui.BeginPopup("##addTag"))
        {
            if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
            ImGui.SetNextItemWidth(Theme.Px(200));
            bool enter = ImGui.InputTextWithHint("##q", "tag name", ref _tagQuery, 64, ImGuiInputTextFlags.EnterReturnsTrue);
            string? typed = Core.Geometry.TagSet.Normalize(_tagQuery);
            foreach (var t in _levelTags.Where(t => !tags.Contains(t) && t.Contains(_tagQuery.Trim(), StringComparison.OrdinalIgnoreCase)).Take(12))
                if (ImGui.Selectable(t)) { result = [.. tags, t]; changed = true; ImGui.CloseCurrentPopup(); }
            if (typed != null && !_levelTags.Contains(typed) && !tags.Contains(typed))
            {
                if (ImGui.Selectable($"New tag \"{typed}\"") || enter) { result = [.. tags, typed]; changed = true; ImGui.CloseCurrentPopup(); }
            }
            else if (enter && typed != null && !tags.Contains(typed)) { result = [.. tags, typed]; changed = true; ImGui.CloseCurrentPopup(); }
            ImGui.EndPopup();
        }
        ImGui.PopID();
        return changed;
    }

    // ---- Profiles ----

    enum Shape { Strip, Wedge, Quarter, Cove, Round, Points }

    static readonly (Shape Shape, string Label, string Tip)[] Shapes =
    [
        (Shape.Strip, "Flat strip", "A rectangle: depth out of the face, height along the surface (baseboards, skirting)"),
        (Shape.Wedge, "Wedge", "A triangle filling the corner (chamfered trim, simple crown)"),
        (Shape.Quarter, "Quarter round", "A quarter circle filling the corner (shoe moulding, rounded beads)"),
        (Shape.Cove, "Cove", "A square with a quarter circle cut out (cove and crown mouldings)"),
        (Shape.Round, "Round", "A circle (pipes, rails, cables)"),
        (Shape.Points, "Custom points", "Any outline: edit its corners as points (x out of the face, y along the surface)"),
    ];

    static Shape ShapeOf(JsonObject? p) =>
        p == null ? Shape.Strip : p.ContainsKey("rect") ? Shape.Strip : p.ContainsKey("wedge") ? Shape.Wedge : p.ContainsKey("quarter") ? Shape.Quarter
        : p.ContainsKey("cove") ? Shape.Cove : p.ContainsKey("round") ? Shape.Round : Shape.Points;

    void ProfileField(ParamSpec spec, JsonObject? p, float w)
    {
        p ??= ParamJson.Default(spec) as JsonObject ?? new JsonObject { ["rect"] = new JsonArray(0.015, 0.12) };
        var shape = ShapeOf(p);
        // The size in metres: (depth, height) for strips and wedges, (radius or diameter, sides) for curves.
        Vector2 Size(string key) => _ws.Resolve(p[key]) is JsonArray a && a.Count == 2
            ? new Vector2((float)(ParamJson.Number(_ws.Resolve(a[0])) ?? 0.02), (float)(ParamJson.Number(_ws.Resolve(a[1])) ?? 0.1)) : new Vector2(0.02f, 0.1f);
        float Scalar(string key) => (float)(ParamJson.Number(_ws.Resolve(p[key])) ?? 0.05);
        Vector2 extent = shape switch
        {
            Shape.Strip => Size("rect"),
            Shape.Wedge => Size("wedge"),
            Shape.Quarter => new Vector2(Scalar("quarter")),
            Shape.Cove => new Vector2(Scalar("cove")),
            Shape.Round => new Vector2(Scalar("round")),
            _ => new Vector2(0.05f),
        };

        var loaded = feature.Current()?.Rules.Rules.FirstOrDefault(r => r.Id == _rule.Id)?.Values.GetValueOrDefault(spec.Name) as Profile;
        float preview = Theme.Px(84);
        float fieldW = MathF.Max(Theme.Px(120), w - preview - ImGui.GetStyle().ItemSpacing.X);
        var previewAt = ImGui.GetCursorScreenPos() + new Vector2(fieldW + ImGui.GetStyle().ItemSpacing.X, 0);
        ImGui.BeginGroup();
        ImGui.SetNextItemWidth(fieldW);
        if (ImGui.BeginCombo("##shape", Shapes.First(s => s.Shape == shape).Label))
        {
            foreach (var (s, label, tip) in Shapes)
            {
                if (ImGui.Selectable(label, s == shape) && s != shape)
                {
                    float big = MathF.Max(extent.X, extent.Y);
                    JsonObject next = s switch
                    {
                        Shape.Strip => new() { ["rect"] = ParamJson.Pair(new Vector2(MathF.Min(extent.X, 0.03f), MathF.Max(extent.Y, 0.05f))) },
                        Shape.Wedge => new() { ["wedge"] = ParamJson.Pair(new Vector2(big, big)) },
                        Shape.Quarter => new() { ["quarter"] = ParamJson.Num(big) },
                        Shape.Cove => new() { ["cove"] = ParamJson.Num(big) },
                        Shape.Round => new() { ["round"] = ParamJson.Num(big) },
                        // Custom starts from the current outline, so a preset can be reshaped corner by corner.
                        _ => new() { ["points"] = Points((loaded ?? Profile.Rect(extent.X, extent.Y)).Points) },
                    };
                    Set(spec.Name, next, spec.DisplayLabel);
                }
                Tip(tip);
            }
            ImGui.EndCombo();
        }
        float half = (fieldW - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
        void SetKey(string key, JsonNode value)
        {
            var next = (JsonObject)p.DeepClone();
            next[key] = value;
            Set(spec.Name, next, spec.DisplayLabel);
        }
        switch (shape)
        {
            case Shape.Strip or Shape.Wedge:
            {
                string key = shape == Shape.Strip ? "rect" : "wedge";
                // Replace only the side that changed, so the other keeps a shared value ("$baseHeight") if it uses one.
                var pair = _ws.Resolve(p[key]) as JsonArray;
                void SetSide(int i, double v) => SetKey(key, new JsonArray(i == 0 ? ParamJson.Num(v) : pair?[0]?.DeepClone() ?? ParamJson.Num(extent.X),
                    i == 1 ? ParamJson.Num(v) : pair?[1]?.DeepClone() ?? ParamJson.Num(extent.Y)));
                ImGui.SetNextItemWidth(half);
                if (NumberField("##d", extent.X, ParamUnit.Length, 0.001, 10, out var d, "depth")) SetSide(0, d);
                Tip("How far it stands out from the face");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(half);
                if (NumberField("##h", extent.Y, ParamUnit.Length, 0.001, 10, out var h, "height")) SetSide(1, h);
                Tip("How far it runs along the surface it sits on (up a wall from the floor)");
                break;
            }
            case Shape.Quarter or Shape.Cove or Shape.Round:
            {
                string key = shape switch { Shape.Quarter => "quarter", Shape.Cove => "cove", _ => "round" };
                ImGui.SetNextItemWidth(half);
                if (NumberField("##r", extent.X, ParamUnit.Length, 0.001, 10, out var r, shape == Shape.Round ? "Ø" : "r"))
                    SetKey(key, ParamJson.Num(r));
                Tip(shape == Shape.Round ? "Diameter" : "Radius");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(half);
                bool round = shape == Shape.Round;
                int sides = (int)(ParamJson.Number(p["sides"]) ?? (round ? 8 : 6));
                if (Drag("##sides", sides, out var s, 0.1f, round ? 3 : 2, round ? 64 : 32, round ? "%.0f sides" : "%.0f segments"))
                    SetKey("sides", JsonValue.Create((int)Math.Round(s)));
                Tip(round ? "Sides of the circle: more is smoother" : "Segments along the curve: more is smoother");
                break;
            }
            default:
                PointsEditor(spec, p, fieldW);
                break;
        }
        ImGui.EndGroup();
        float groupBottom = ImGui.GetItemRectMax().Y;
        float left = ImGui.GetItemRectMin().X;
        DrawProfile(previewAt, preview, loaded);
        // Carry on below whichever is taller: the fields (a long point list) or the preview.
        if (groupBottom > ImGui.GetItemRectMax().Y)
        {
            ImGui.SetCursorScreenPos(new Vector2(left, groupBottom - ImGui.GetFrameHeight()));
            ImGui.Dummy(new Vector2(0, ImGui.GetFrameHeight()));
        }
    }

    static JsonArray Points(IEnumerable<Vector2> points) =>
        new(points.Select(q => (JsonNode)new JsonArray(ParamJson.Num(q.X), ParamJson.Num(q.Y))).ToArray());

    /// <summary>A custom outline's corners in cm, one row each (out of the face, along the surface), with add and remove.</summary>
    void PointsEditor(ParamSpec spec, JsonObject p, float w)
    {
        // Work on the points as written, so a coordinate taken from a shared value ("$trimHeight") stays linked unless
        // that coordinate itself is edited.
        var raw = _ws.Resolve(p["points"]) as JsonArray ?? [];
        double Coord(int i, int axis) => ParamJson.Number(_ws.Resolve((raw[i] as JsonArray)?[axis])) ?? 0;
        float removeW = Theme.Px(24);
        float each = (w - removeW - ImGui.GetStyle().ItemSpacing.X * 2) * 0.5f;
        void Save(Func<JsonArray, JsonArray> change) =>
            Set(spec.Name, new JsonObject { ["points"] = change((JsonArray)raw.DeepClone()) }, spec.DisplayLabel);
        for (int i = 0; i < raw.Count; i++)
        {
            ImGui.PushID(i);
            int index = i;
            for (int axis = 0; axis < 2; axis++)
            {
                if (axis > 0) ImGui.SameLine();
                ImGui.SetNextItemWidth(each);
                int a = axis;
                if (NumberField(axis == 0 ? "##x" : "##y", Coord(i, axis), ParamUnit.Length, -10, 10, out var v, axis == 0 ? "out" : "up"))
                    Save(pts =>
                    {
                        var pt = pts[index] as JsonArray ?? new JsonArray(ParamJson.Num(0), ParamJson.Num(0));
                        pts[index] = new JsonArray(a == 0 ? ParamJson.Num(v) : pt[0]?.DeepClone(), a == 1 ? ParamJson.Num(v) : pt[1]?.DeepClone());
                        return pts;
                    });
                var shared = RulesWorkspace.VarName((raw[i] as JsonArray)?[axis]);
                Tip($"Corner {i + 1}: {(axis == 0 ? "out from the face" : "along the surface it sits on")}{(shared != null ? $"\nFrom the shared value \"{shared}\"; editing it gives this corner its own value." : "")}");
            }
            ImGui.SameLine();
            if (raw.Count <= 3) ImGui.BeginDisabled();
            if (ImGui.Button("×", new Vector2(removeW, 0))) Save(pts => { pts.RemoveAt(index); return pts; });
            if (raw.Count <= 3) ImGui.EndDisabled();
            Tip(raw.Count <= 3 ? "An outline needs at least three corners" : "Remove this corner");
            ImGui.PopID();
        }
        if (ImGui.SmallButton("+ Corner"))
        {
            int n = raw.Count;
            var mid = n > 0 ? new Vector2((float)(Coord(n - 1, 0) + Coord(0, 0)), (float)(Coord(n - 1, 1) + Coord(0, 1))) * 0.5f : Vector2.Zero;
            Save(pts => { pts.Add(new JsonArray(ParamJson.Num(mid.X), ParamJson.Num(mid.Y))); return pts; }); // halfway back to the first
        }
        Tip("Add a corner between the last and the first");
    }

    /// <summary>The cross-section as the loader builds it: the face it stands out from on the left, the surface below.</summary>
    void DrawProfile(Vector2 at, float size, Profile? profile)
    {
        var dl = ImGui.GetWindowDrawList();
        var box = new Vector2(size, MathF.Max(size * 0.8f, ImGui.GetFrameHeight() * 2 + ImGui.GetStyle().ItemSpacing.Y));
        uint guide = ImGui.ColorConvertFloat4ToU32(Theme.TextDisabled);
        dl.AddRectFilled(at, at + box, ImGui.ColorConvertFloat4ToU32(Theme.Track), Theme.Px(3));
        ImGui.SetCursorScreenPos(at);
        ImGui.Dummy(box); // reserves the room and carries the tooltip
        float pad = Theme.Px(8);
        // Fit the outline and the corner it sits in, wherever the points go (negative ones included).
        var pts = profile?.Points is { Length: >= 3 } p ? p : [];
        var min = pts.Aggregate(Vector2.Zero, Vector2.Min);
        var max = pts.Aggregate(Vector2.Zero, Vector2.Max);
        var span = Vector2.Max(max - min, new Vector2(1e-4f));
        float scale = MathF.Min((box.X - pad * 2) / span.X, (box.Y - pad * 2) / span.Y);
        if (pts.Length == 0) scale = 1;
        Vector2 Map(Vector2 q) => new(at.X + pad + (q.X - min.X) * scale, at.Y + box.Y - pad - (q.Y - min.Y) * scale);
        var o = Map(Vector2.Zero);
        dl.AddLine(new Vector2(o.X, at.Y + Theme.Px(3)), new Vector2(o.X, at.Y + box.Y - Theme.Px(3)), guide, Theme.Px(1)); // the face
        dl.AddLine(new Vector2(at.X + Theme.Px(3), o.Y), new Vector2(at.X + box.X - Theme.Px(3), o.Y), guide, Theme.Px(1)); // the surface
        if (pts.Length == 0) return;
        uint line = ImGui.ColorConvertFloat4ToU32(Theme.Text);
        for (int i = 0; i < pts.Length; i++) dl.AddLine(Map(pts[i]), Map(pts[(i + 1) % pts.Length]), line, Theme.Px(1.5f));
        Tip($"Cross-section, {span.X * 100:0.#} × {span.Y * 100:0.#} cm. The vertical line is the face it stands out from, the horizontal one the surface it runs along.");
    }

    // ---- Props ----

    enum PropKind { Box, Cylinder, File }

    /// <summary>One prop: a box or cylinder (sizes in cm) or a .glb file (relative to the rules file it's written in).</summary>
    void PropField(string id, JsonNode? value, float w, Action<JsonNode> set, double? weight = null, Action<double>? setWeight = null, Action? remove = null)
    {
        ImGui.PushID(id);
        var kind = value switch
        {
            JsonValue v when ParamJson.Text(v) is { } s && ParamJson.IsPropFile(s) => PropKind.File,
            JsonObject o when o.ContainsKey("cylinder") => PropKind.Cylinder,
            _ => PropKind.Box,
        };
        float sp = ImGui.GetStyle().ItemSpacing.X;
        float x = ImGui.GetCursorPosX();
        float kindW = Theme.Px(110), removeW = remove != null ? Theme.Px(24) + sp : 0;

        // First line: what it is (and the file), then remove.
        ImGui.SetNextItemWidth(kindW);
        if (ImGui.BeginCombo("##kind", kind switch { PropKind.File => "Model file", _ => kind.ToString() }))
        {
            if (ImGui.Selectable("Box", kind == PropKind.Box) && kind != PropKind.Box) set(new JsonObject { ["box"] = new JsonArray(ParamJson.Num(0.3), ParamJson.Num(0.3), ParamJson.Num(0.3)) });
            if (ImGui.Selectable("Cylinder", kind == PropKind.Cylinder) && kind != PropKind.Cylinder) set(new JsonObject { ["cylinder"] = new JsonArray(ParamJson.Num(0.2), ParamJson.Num(0.5)) });
            if (ImGui.Selectable("Model file...", kind == PropKind.File) && BrowseProp() is { } file) set(JsonValue.Create(file));
            ImGui.EndCombo();
        }
        float rest = w - kindW - sp - removeW;
        string? missing = null;
        if (kind == PropKind.File)
        {
            ImGui.SameLine();
            string browse = "Browse...";
            float browseW = ImGui.CalcTextSize(browse).X + ImGui.GetStyle().FramePadding.X * 2;
            string path = ParamJson.Text(value) ?? "";
            ImGui.SetNextItemWidth(rest - browseW - sp);
            if (TextField("##file", path, "props/thing.glb", out var t))
            {
                if (ParamJson.IsPropFile(t)) set(JsonValue.Create(t));
                else _ctx.Status($"\"{t}\" isn't a .glb or .gltf file", error: true);
            }
            Tip($"{path}\nRelative to {Path.GetFileName(TargetFile)}. Props stand on their origin: +Y up, +X along the edge, +Z out of the face.");
            ImGui.SameLine();
            if (ImGui.Button(browse) && BrowseProp() is { } file) set(JsonValue.Create(file));
            Tip("Choose a .glb or .gltf file");
            if (path.Length > 0 && !File.Exists(RulePaths.Resolve(TargetFile, path))) missing = $"File not found: {RulePaths.Resolve(TargetFile, path)}";
        }
        if (remove != null)
        {
            ImGui.SameLine(x + w - Theme.Px(24));
            if (ImGui.Button("×##remove", new Vector2(Theme.Px(24), 0))) remove();
            Tip("Remove this prop");
        }
        if (missing != null)
        {
            ImGui.SetCursorPosX(x);
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + w);
            ImGui.TextColored(Theme.Error, missing);
            ImGui.PopTextWrapPos();
        }

        // Second line: its size, labelled, and how often it's picked.
        var fields = new List<(string Label, double Value, string Tip)>();
        string? key = kind switch { PropKind.Box => "box", PropKind.Cylinder => "cylinder", _ => null };
        double[] nums = key == null ? [] : ParamJson.Numbers(_ws.ResolveDeep((value as JsonObject)?[key])) ?? (kind == PropKind.Box ? [0.3, 0.3, 0.3] : [0.2, 0.5]);
        if (kind == PropKind.Box) fields.AddRange([("W", nums[0], "Width, along the edge"), ("H", nums[1], "Height"), ("D", nums[2], "Depth, out of the face")]);
        if (kind == PropKind.Cylinder) fields.AddRange([("Ø", nums[0], "Diameter"), ("H", nums[1], "Height")]);
        int count = fields.Count + (kind == PropKind.Cylinder ? 1 : 0) + (weight != null ? 1 : 0);
        if (count == 0) { ImGui.PopID(); return; }
        ImGui.SetCursorPosX(x);
        float each = (w - sp * (count - 1)) / count;
        bool changed = false;
        var next = (double[])nums.Clone();
        for (int i = 0; i < fields.Count; i++)
        {
            if (i > 0) ImGui.SameLine();
            ImGui.SetNextItemWidth(each);
            if (NumberField($"##n{i}", fields[i].Value, ParamUnit.Length, 0.001, 100, out var n, fields[i].Label)) { next[i] = n; changed = true; }
            Tip(fields[i].Tip);
        }
        if (changed && key != null)
        {
            var obj = value is JsonObject o ? (JsonObject)o.DeepClone() : new JsonObject();
            obj[key] = new JsonArray(next.Select(n => (JsonNode)ParamJson.Num(n)).ToArray());
            set(obj);
        }
        if (kind == PropKind.Cylinder)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(each);
            int sides = (int)(ParamJson.Number((value as JsonObject)?["sides"]) ?? 12);
            if (Drag("##sides", sides, out var s, 0.1f, 3, 64, "%.0f sides"))
            {
                var obj = (JsonObject)value!.DeepClone();
                obj["sides"] = JsonValue.Create((int)Math.Round(s));
                set(obj);
            }
            Tip("Sides of the cylinder");
        }
        if (weight is { } wv)
        {
            if (fields.Count > 0 || kind == PropKind.Cylinder) ImGui.SameLine();
            ImGui.SetNextItemWidth(each);
            if (Drag("##weight", (float)wv, out var nw, 0.05f, 0, 100, "×%.1f")) setWeight?.Invoke(nw);
            Tip("Weight: how often this prop is picked, relative to the others (×2 is twice as often as ×1)");
        }
        ImGui.PopID();
    }

    string? BrowseProp()
    {
        var result = Dialog.FileOpen("glb,gltf", Path.GetDirectoryName(Path.GetFullPath(TargetFile)));
        return result.IsOk ? RulesWorkspace.Relative(TargetFile, result.Path) : null;
    }

    void PropsField(ParamSpec spec)
    {
        var raw = Value(spec.Name);
        var props = raw is JsonArray a ? a.Select(i => i?.DeepClone()).ToList() : raw != null ? [raw.DeepClone()] : new List<JsonNode?>();
        var weights = ParamJson.Numbers(Value("weights"));
        float x = ImGui.GetCursorPosX();
        float w = Avail();
        for (int i = 0; i < props.Count; i++)
        {
            if (i > 0) { ImGui.Dummy(Theme.Px(0, 2)); ImGui.SetCursorPosX(x); }
            ImGui.PushID(i);
            int index = i;
            double weight = weights != null && i < weights.Length ? weights[i] : 1;
            PropField("prop", props[i], w,
                v => Set(spec.Name, new JsonArray(props.Select((p, j) => j == index ? v : p?.DeepClone()).ToArray()), "props"),
                props.Count > 1 ? weight : null,
                nw =>
                {
                    var all = Enumerable.Range(0, props.Count).Select(j => j == index ? nw : weights != null && j < weights.Length ? weights[j] : 1).ToArray();
                    Set("weights", all.All(v => Math.Abs(v - all[0]) < 1e-9) ? null : new JsonArray(all.Select(v => (JsonNode)ParamJson.Num(v)).ToArray()), "weights");
                },
                props.Count > 1 ? () =>
                {
                    var list = new JsonArray(props.Where((_, j) => j != index).Select(p => p?.DeepClone()).ToArray());
                    var rest = weights?.Where((_, j) => j != index).ToArray();
                    SetMany("remove prop", (spec.Name, list), ("weights", rest is { Length: > 0 } && props.Count - 1 == rest.Length ? new JsonArray(rest.Select(v => (JsonNode)ParamJson.Num(v)).ToArray()) : null));
                } : null);
            ImGui.PopID();
        }
        ImGui.SetCursorPosX(x);
        if (ImGui.SmallButton("+ Add prop"))
        {
            var list = new JsonArray([.. props.Select(p => p?.DeepClone()), new JsonObject { ["box"] = new JsonArray(ParamJson.Num(0.3), ParamJson.Num(0.2), ParamJson.Num(0.3)) }]);
            SetMany("add prop", (spec.Name, list), ("weights", weights == null ? null : new JsonArray([.. weights.Select(v => (JsonNode)ParamJson.Num(v)), ParamJson.Num(1)])));
        }
    }
}
