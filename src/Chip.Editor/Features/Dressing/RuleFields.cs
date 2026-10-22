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
        var var = RulesWorkspace.VarName(_rule.Raw[key]);
        string tip = spec.Description + (spec.Required ? "\nRequired." : "")
            + (changed ? "\nChanged in this level (the style says otherwise)." : "")
            + (var != null ? $"\nUses the shared value \"{var}\". Changing it here gives this rule its own value." : "");
        Tip(tip);
        ImGui.SameLine(LabelW);
    }

    /// <summary>Reserves the reset button's room at the right of a row, returning the width left for the widget.</summary>
    float Avail(string key) => ImGui.GetContentRegionAvail().X - (Resettable(key) ? Theme.Px(24) + ImGui.GetStyle().ItemSpacing.X : 0);

    bool Resettable(string key) => _rule.LevelKeys.Contains(key) && !_style;

    void ResetButton(string key)
    {
        if (!Resettable(key)) return;
        ImGui.SameLine();
        if (ImGui.Button("×##reset", new Vector2(Theme.Px(24), 0)))
            feature.EditRules($"Dressing: reset {_rule.Id} {key}", w => { w.Reset(_rule.Id, key); return true; });
        Tip("Back to the style's value");
    }

    /// <summary>In style mode a value this level overrides can't be edited (the level's value would still win).</summary>
    bool Locked(string key) => _style && _rule.LevelKeys.Contains(key);

    void Row(ParamSpec spec)
    {
        Label(spec);
        bool locked = Locked(spec.Name);
        if (locked) ImGui.BeginDisabled();
        float w = Avail(spec.Name);
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
                PairField(spec, ParamJson.Pair(value) ?? Vector2.Zero, w);
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
            Tip("This level changes this value, so editing the style here wouldn't show. Turn off \"Edit the style\" and reset it first.");
        }
        ResetButton(spec.Name);
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

    /// <summary>A number shown in its unit (metres as cm), committed when the drag or typing ends.</summary>
    bool NumberField(string id, double value, ParamUnit unit, double min, double max, out double result)
    {
        var (scale, fmt, speed) = Display(unit);
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
        var (a, b) = spec.Name switch
        {
            "offset" => _rule.KindInfo is ScatterKind ? ("", "") : ("out", "up"),
            "scale" or "yawDeg" => ("min", "max"),
            _ => ("", ""),
        };
        float half = (w - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
        ImGui.SetNextItemWidth(half);
        bool changed = NumberField("##a", v.X, spec.Unit, double.NegativeInfinity, double.PositiveInfinity, out var x);
        Tip(a.Length > 0 ? a : spec.Description);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(half);
        changed |= NumberField("##b", v.Y, spec.Unit, double.NegativeInfinity, double.PositiveInfinity, out var y);
        Tip(b.Length > 0 ? b : spec.Description);
        if (changed) Set(spec.Name, ParamJson.Pair(new Vector2((float)x, (float)y)), spec.DisplayLabel);
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
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - (changed && !_style ? Theme.Px(24) + ImGui.GetStyle().ItemSpacing.X : 0));
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
        if (changed && !_style)
        {
            ImGui.SameLine();
            if (ImGui.Button("×##resetWhere", new Vector2(Theme.Px(24), 0)))
                feature.EditRules($"Dressing: reset {_rule.Id} where", w => { w.Reset(_rule.Id, "edges"); w.Reset(_rule.Id, "onto"); return true; });
            Tip("Back to the style's value");
        }
        if (place == null && ParamJson.Text(Value("edges")) is { } custom && custom != "contact")
        {
            ImGui.PushID("edgesRaw");
            Row(edgesSpec);
            ImGui.PopID();
        }
    }

    // ---- Selectors ----

    static readonly (string? Value, string Label)[] Facings = [(null, "Any way"), ("upright", "Walls (upright)"), ("up", "Floors (facing up)"), ("down", "Ceilings (facing down)")];

    void FacesField(ParamSpec spec, JsonObject sel)
    {
        float x = ImGui.GetCursorPosX();
        var tags = ParamJson.Names(_ws.Resolve(sel["tags"]));
        if (TagChips("tags", tags, mesh: false, out var newTags)) SetSelector(spec, sel, "tags", newTags.Length == 0 ? null : ParamJson.List(newTags));

        ImGui.SetCursorPosX(x);
        string? facing = ParamJson.Text(_ws.Resolve(sel["facing"]));
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - (Resettable(spec.Name) ? Theme.Px(24) + ImGui.GetStyle().ItemSpacing.X : 0));
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
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
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
        (Shape.Points, "Custom shape", "Any polygon, written as points in the rules file"),
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

        float preview = Theme.Px(64);
        float fieldW = MathF.Max(Theme.Px(90), w - preview - ImGui.GetStyle().ItemSpacing.X);
        var previewAt = ImGui.GetCursorScreenPos() + new Vector2(fieldW + ImGui.GetStyle().ItemSpacing.X, 0);
        ImGui.BeginGroup();
        ImGui.SetNextItemWidth(fieldW);
        if (ImGui.BeginCombo("##shape", Shapes.First(s => s.Shape == shape).Label))
        {
            foreach (var (s, label, tip) in Shapes)
            {
                if (ImGui.Selectable(label, s == shape) && s != shape && s != Shape.Points)
                {
                    float big = MathF.Max(extent.X, extent.Y);
                    JsonObject next = s switch
                    {
                        Shape.Strip => new() { ["rect"] = ParamJson.Pair(new Vector2(MathF.Min(extent.X, 0.03f), MathF.Max(extent.Y, 0.05f))) },
                        Shape.Wedge => new() { ["wedge"] = ParamJson.Pair(new Vector2(big, big)) },
                        Shape.Quarter => new() { ["quarter"] = ParamJson.Num(big) },
                        Shape.Cove => new() { ["cove"] = ParamJson.Num(big) },
                        _ => new() { ["round"] = ParamJson.Num(big) },
                    };
                    Set(spec.Name, next, spec.DisplayLabel);
                }
                Tip(tip);
            }
            ImGui.EndCombo();
        }
        float half = (fieldW - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
        switch (shape)
        {
            case Shape.Strip or Shape.Wedge:
            {
                string key = shape == Shape.Strip ? "rect" : "wedge";
                ImGui.SetNextItemWidth(half);
                bool c = NumberField("##d", extent.X, ParamUnit.Length, 0.001, 10, out var d);
                Tip("Depth: how far it stands out from the face");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(half);
                c |= NumberField("##h", extent.Y, ParamUnit.Length, 0.001, 10, out var h);
                Tip("Height: how far it runs along the surface it sits on");
                if (c) Set(spec.Name, new JsonObject { [key] = ParamJson.Pair(new Vector2((float)d, (float)h)) }, spec.DisplayLabel);
                break;
            }
            case Shape.Quarter or Shape.Cove or Shape.Round:
            {
                string key = shape switch { Shape.Quarter => "quarter", Shape.Cove => "cove", _ => "round" };
                ImGui.SetNextItemWidth(shape == Shape.Round ? half : fieldW);
                if (NumberField("##r", extent.X, ParamUnit.Length, 0.001, 10, out var r))
                {
                    var next = (JsonObject)p.DeepClone();
                    next[key] = ParamJson.Num(r);
                    Set(spec.Name, next, spec.DisplayLabel);
                }
                Tip(shape == Shape.Round ? "Diameter" : "Radius");
                if (shape == Shape.Round)
                {
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(half);
                    int sides = (int)(ParamJson.Number(p["sides"]) ?? 8);
                    if (Drag("##sides", sides, out var s, 0.1f, 3, 64, "%.0f sides"))
                    {
                        var next = (JsonObject)p.DeepClone();
                        next["sides"] = JsonValue.Create((int)Math.Round(s));
                        Set(spec.Name, next, spec.DisplayLabel);
                    }
                }
                break;
            }
            default:
                ImGui.TextColored(Theme.TextDim, $"{(p["points"] as JsonArray)?.Count ?? 0} points (Open JSON to edit)");
                break;
        }
        ImGui.EndGroup();
        DrawProfile(previewAt, preview, spec);
    }

    /// <summary>The cross-section as the loader builds it: the face it stands out from on the left, the surface below.</summary>
    void DrawProfile(Vector2 at, float size, ParamSpec spec)
    {
        var dl = ImGui.GetWindowDrawList();
        var rule = feature.Current()?.Rules.Rules.FirstOrDefault(r => r.Id == _rule.Id);
        var profile = rule?.Values.GetValueOrDefault(spec.Name) as Profile;
        float h = ImGui.GetFrameHeight() * 2 + ImGui.GetStyle().ItemSpacing.Y;
        var box = new Vector2(size, h);
        uint guide = ImGui.ColorConvertFloat4ToU32(Theme.TextDisabled);
        dl.AddRectFilled(at, at + box, ImGui.ColorConvertFloat4ToU32(Theme.Track), Theme.Px(3));
        float pad = Theme.Px(6);
        var origin = new Vector2(at.X + pad, at.Y + box.Y - pad);
        dl.AddLine(new Vector2(origin.X, at.Y + pad * 0.5f), origin, guide, Theme.Px(1));        // the face
        dl.AddLine(origin, new Vector2(at.X + box.X - pad * 0.5f, origin.Y), guide, Theme.Px(1)); // the surface
        if (profile == null || profile.Points.Length < 3) return;
        var max = profile.Points.Aggregate(Vector2.Max);
        var min = profile.Points.Aggregate(Vector2.Min);
        float extent = MathF.Max(MathF.Max(max.X, max.Y), MathF.Max(-min.X, -min.Y));
        float scale = MathF.Min(box.X, box.Y) - pad * 2;
        scale /= MathF.Max(extent, 1e-4f);
        var pts = profile.Points.Select(q => origin + new Vector2(q.X, -q.Y) * scale).ToArray();
        for (int i = 0; i < pts.Length; i++) dl.AddLine(pts[i], pts[(i + 1) % pts.Length], Theme.AccentU32, Theme.Px(1.5f));
        Tip($"Cross-section, {extent * 100:0.#} cm across. Left: the face it stands on; bottom: the surface it runs along.");
    }

    // ---- Props ----

    enum PropKind { Box, Cylinder, File }

    /// <summary>One prop: a box or cylinder (sizes in cm) or a .glb file (relative to the rules file it's written in).</summary>
    void PropField(string id, JsonNode? value, float w, Action<JsonNode> set)
    {
        ImGui.PushID(id);
        var kind = value switch
        {
            JsonValue v when ParamJson.Text(v) is { } s && ParamJson.IsPropFile(s) => PropKind.File,
            JsonObject o when o.ContainsKey("cylinder") => PropKind.Cylinder,
            _ => PropKind.Box,
        };
        float kindW = Theme.Px(84);
        ImGui.SetNextItemWidth(kindW);
        if (ImGui.BeginCombo("##kind", kind.ToString()))
        {
            if (ImGui.Selectable("Box", kind == PropKind.Box) && kind != PropKind.Box) set(new JsonObject { ["box"] = new JsonArray(ParamJson.Num(0.3), ParamJson.Num(0.3), ParamJson.Num(0.3)) });
            if (ImGui.Selectable("Cylinder", kind == PropKind.Cylinder) && kind != PropKind.Cylinder) set(new JsonObject { ["cylinder"] = new JsonArray(ParamJson.Num(0.2), ParamJson.Num(0.5)) });
            if (ImGui.Selectable("File...", false) && BrowseProp() is { } file) set(JsonValue.Create(file));
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        float rest = w - kindW - ImGui.GetStyle().ItemSpacing.X;
        switch (kind)
        {
            case PropKind.File:
            {
                float browse = ImGui.CalcTextSize("...").X + ImGui.GetStyle().FramePadding.X * 2;
                ImGui.SetNextItemWidth(rest - browse - ImGui.GetStyle().ItemSpacing.X);
                if (TextField("##file", ParamJson.Text(value) ?? "", "props/thing.glb", out var t) && ParamJson.IsPropFile(t)) set(JsonValue.Create(t));
                Tip($"Relative to {Path.GetFileName(TargetFile)}. Props stand on their origin: +Y up, +X along the edge, +Z out of the face.");
                ImGui.SameLine();
                if (ImGui.Button("...") && BrowseProp() is { } file) set(JsonValue.Create(file));
                Tip("Choose a .glb or .gltf file");
                break;
            }
            default:
            {
                string key = kind == PropKind.Box ? "box" : "cylinder";
                var nums = ParamJson.Numbers(_ws.Resolve((value as JsonObject)?[key])) ?? (kind == PropKind.Box ? [0.3, 0.3, 0.3] : [0.2, 0.5]);
                string[] tips = kind == PropKind.Box ? ["Width (along the edge)", "Height", "Depth (out of the face)"] : ["Diameter", "Height"];
                float each = (rest - ImGui.GetStyle().ItemSpacing.X * (nums.Length - 1)) / nums.Length;
                bool changed = false;
                var next = (double[])nums.Clone();
                for (int i = 0; i < nums.Length && i < tips.Length; i++)
                {
                    if (i > 0) ImGui.SameLine();
                    ImGui.SetNextItemWidth(each);
                    if (NumberField($"##n{i}", nums[i], ParamUnit.Length, 0.001, 100, out var n)) { next[i] = n; changed = true; }
                    Tip(tips[i]);
                }
                if (changed)
                {
                    var obj = value is JsonObject o ? (JsonObject)o.DeepClone() : new JsonObject();
                    obj[key] = new JsonArray(next.Select(n => (JsonNode)ParamJson.Num(n)).ToArray());
                    set(obj);
                }
                break;
            }
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
        float weightW = Theme.Px(70), removeW = Theme.Px(24);
        for (int i = 0; i < props.Count; i++)
        {
            if (i > 0) ImGui.SetCursorPosX(x);
            ImGui.PushID(i);
            int index = i;
            float w = ImGui.GetContentRegionAvail().X - weightW - removeW - ImGui.GetStyle().ItemSpacing.X * 2;
            PropField("prop", props[i], w, v =>
            {
                var list = new JsonArray(props.Select((p, j) => j == index ? v : p?.DeepClone()).ToArray());
                Set(spec.Name, list, "props");
            });
            ImGui.SameLine();
            ImGui.SetNextItemWidth(weightW);
            double weight = weights != null && i < weights.Length ? weights[i] : 1;
            if (Drag("##w", (float)weight, out var nw, 0.05f, 0, 100, "×%.2f"))
            {
                var all = Enumerable.Range(0, props.Count).Select(j => j == index ? nw : weights != null && j < weights.Length ? weights[j] : 1).ToArray();
                Set("weights", all.All(v => Math.Abs(v - all[0]) < 1e-9) ? null : new JsonArray(all.Select(v => (JsonNode)ParamJson.Num(v)).ToArray()), "weights");
            }
            Tip("How often this prop is chosen, relative to the others");
            ImGui.SameLine();
            if (props.Count > 1 && ImGui.Button("×", new Vector2(removeW, 0)))
            {
                var list = new JsonArray(props.Where((_, j) => j != index).Select(p => p?.DeepClone()).ToArray());
                var rest = weights?.Where((_, j) => j != index).ToArray();
                SetMany("remove prop", (spec.Name, list), ("weights", rest is { Length: > 0 } && props.Count - 1 == rest.Length ? new JsonArray(rest.Select(v => (JsonNode)ParamJson.Num(v)).ToArray()) : null));
            }
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
