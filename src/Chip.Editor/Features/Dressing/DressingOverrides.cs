using System.Numerics;
using System.Text.Json.Nodes;
using Chip.Core.Dressing;
using Chip.Core.Geometry;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.Dressing;

/// <summary>
/// The Dressing section's per-mesh controls, for one selected mesh: every rule that takes it, with a switch to turn the
/// rule off just here and its simple parameters (numbers, choices, on/off, text, pairs) as fields. A value set here
/// overrides the rules file for this mesh only; it shows in orange with a reset button, and "Apply to rule" moves it into
/// the rule for every mesh. Each change is one undo step (drags commit when released, so the level re-dresses once).
/// </summary>
sealed class DressingOverrides(DressingFeature feature)
{
    (int Mesh, string Rule, string Param)? _draftKey;
    Vector2 _draft;
    string _textDraft = "";
    (DressedLevel Level, PolyMesh Mesh, MeshDressing Dressing, IReadOnlyList<(Rule Rule, bool Skipped)> Rules)? _cache;

    static bool Editable(ParamType t) => t is ParamType.Number or ParamType.Int or ParamType.Bool or ParamType.Choice or ParamType.Text or ParamType.Pair;

    public void Draw(EditorContext ctx, DressedLevel level, PolyMesh mesh)
    {
        if (_cache is not { } c || !ReferenceEquals(c.Level, level) || !ReferenceEquals(c.Mesh, mesh) || !ReferenceEquals(c.Dressing, mesh.Dressing))
            _cache = c = (level, mesh, mesh.Dressing, level.Applicable(mesh));
        SectionDivider();
        if (c.Rules.Count == 0)
        {
            ImGui.TextColored(Theme.TextDim, $"No rule takes \"{mesh.Name}\". Tag it (Ctrl+T) to dress it.");
            return;
        }
        ImGui.TextColored(Theme.TextDim, $"For \"{mesh.Name}\" only:");
        foreach (var (rule, skipped) in c.Rules)
        {
            ImGui.PushID(rule.Id);
            Header(ctx, mesh, rule, skipped);
            if (!skipped)
            {
                var over = mesh.Dressing.OverridesFor(rule.Id) is { } json ? JsonNode.Parse(json)?.AsObject() : null;
                // As in the rules window: an angle only matters for corner edges.
                string edges = over?["edges"]?.GetValue<string>() ?? rule.Text("edges") ?? "";
                foreach (var spec in rule.Kind.Params.Where(p => Editable(p.Type)))
                    if (spec.Name != "minDeg" || edges is "convex" or "concave")
                        Field(ctx, mesh, rule, spec, over?[spec.Name]);
            }
            ImGui.PopID();
            ImGui.Dummy(Theme.Px(0, 3));
        }
    }

    void Header(EditorContext ctx, PolyMesh mesh, Rule rule, bool skipped)
    {
        var overrides = mesh.Dressing.OverridesFor(rule.Id);
        bool overridden = overrides != null;

        // The rule's name and kind (cut to fit), then a switch for this mesh and a way to the rule itself.
        float w = ImGui.GetFrameHeight();
        float controls = ImGui.GetContentRegionMax().X - w * 2 - ImGui.GetStyle().ItemSpacing.X;
        float room = controls - ImGui.GetCursorPosX() - Theme.Px(8);
        ImGui.AlignTextToFramePadding();
        string name = Ellipsize(rule.Id, room);
        ImGui.Text(name);
        if (name != rule.Id) Tip(rule.Id);
        float kindRoom = room - ImGui.CalcTextSize(name).X - ImGui.GetStyle().ItemSpacing.X;
        if (name == rule.Id && Ellipsize(rule.Kind.DisplayName, kindRoom) is { Length: > 0 } kind)
        {
            ImGui.SameLine();
            ImGui.TextColored(Theme.TextDim, kind);
        }
        ImGui.SameLine(controls);
        bool on = !skipped;
        if (ImGui.Checkbox("##on", ref on)) Edit(ctx, mesh.Id, on ? "Switch dressing rule on" : "Switch dressing rule off", d => d.WithSkip(rule.Id, !on));
        Tip(skipped ? $"Off for this mesh: let {rule.Id} dress it again" : $"On: untick to stop {rule.Id} dressing this mesh (other meshes keep it)");
        ImGui.SameLine();
        if (IconButton("edit", Icon.ChevronRight, $"Open {rule.Id} in the Dressing tab, to change it for every mesh", w)) feature.Window.ShowRule(rule.Id);

        // Values set for this mesh: put them into the rule for everyone, or drop them.
        if (overridden && !skipped)
        {
            RowLabel("Differs", "This mesh has its own values for this rule (orange below)");
            int act = ButtonRow("differs",
                new RowButton("Use for all", $"Put this mesh's values into {rule.Id} itself, so every mesh it dresses gets them"),
                new RowButton("Reset", $"Go back to the rule's values for {rule.Id} on this mesh"));
            if (act == 0) ApplyToRule(ctx, mesh, rule, overrides!);
            if (act == 1) Edit(ctx, mesh.Id, "Reset dressing overrides", d => d.WithoutOverrides(rule.Id));
        }
    }

    void Field(EditorContext ctx, PolyMesh mesh, Rule rule, ParamSpec spec, JsonNode? over)
    {
        bool overridden = over != null;
        float resetW = ImGui.GetFrameHeight();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(overridden ? Theme.Accent : Theme.TextDim, Ellipsize(spec.DisplayLabel, PropertyColumn - ImGui.GetCursorPosX() - Theme.Px(4)));
        Tip(spec.Description + (overridden ? "\nSet for this mesh only." : ""));
        ImGui.SameLine(PropertyColumn);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - resetW - ImGui.GetStyle().ItemSpacing.X);
        var key = (mesh.Id, rule.Id, spec.Name);
        string id = $"##{spec.Name}";
        void Set(JsonNode? value) => Edit(ctx, mesh.Id, $"Dressing: {rule.Id} {spec.DisplayLabel}", d => d.WithParam(rule.Id, spec.Name, value));
        // Lengths show in centimetres, as in the Dressing tab; the file keeps metres.
        float scale = spec.Unit == ParamUnit.Length ? 100 : 1;
        string fmt = spec.Unit switch { ParamUnit.Length => "%.1f cm", ParamUnit.Degrees => "%.0f°", ParamUnit.PerM2 => "%.2f per m²", ParamUnit.Scale => "%.2f×", _ => "%.3f" };

        switch (spec.Type)
        {
            case ParamType.Number:
            {
                float v = _draftKey == key ? _draft.X : (over is JsonValue jv ? (float)jv.GetValue<double>() : rule.Values[spec.Name] is float f ? f : 0) * scale;
                float min = (float)Math.Max(spec.Min * scale, -1e6), max = (float)Math.Min(spec.Max * scale, 1e6);
                if (ImGui.DragFloat(id, ref v, MathF.Max(0.001f, MathF.Abs(v) * 0.01f), min, max, fmt)) { _draftKey = key; _draft.X = v; }
                if (ImGui.IsItemDeactivatedAfterEdit() && _draftKey == key) { _draftKey = null; Set(JsonValue.Create(Math.Round(_draft.X / scale, 5))); }
                break;
            }
            case ParamType.Int:
            {
                int v = _draftKey == key ? (int)_draft.X : over is JsonValue jv ? (int)jv.GetValue<double>() : rule.Values[spec.Name] is int i ? i : 0;
                int min = (int)Math.Max(spec.Min, int.MinValue), max = (int)Math.Min(spec.Max, int.MaxValue);
                if (ImGui.DragInt(id, ref v, 0.1f, min, max)) { _draftKey = key; _draft.X = v; }
                if (ImGui.IsItemDeactivatedAfterEdit() && _draftKey == key) { _draftKey = null; Set(JsonValue.Create((int)_draft.X)); }
                break;
            }
            case ParamType.Pair:
            {
                var v = _draftKey == key ? _draft : (over is JsonArray { Count: 2 } ja ? new Vector2((float)ja[0]!.GetValue<double>(), (float)ja[1]!.GetValue<double>())
                    : rule.Values[spec.Name] is Vector2 p ? p : Vector2.Zero) * scale;
                if (ImGui.DragFloat2(id, ref v, 0.01f * scale, -1e4f, 1e4f, fmt.Replace("%.3f", "%.2f"))) { _draftKey = key; _draft = v; }
                if (ImGui.IsItemDeactivatedAfterEdit() && _draftKey == key) { _draftKey = null; Set(new JsonArray(Math.Round(_draft.X / scale, 5), Math.Round(_draft.Y / scale, 5))); }
                break;
            }
            case ParamType.Bool:
            {
                bool v = over is JsonValue jv ? jv.GetValue<bool>() : rule.Values[spec.Name] is true;
                if (ImGui.Checkbox(id, ref v)) Set(JsonValue.Create(v));
                break;
            }
            case ParamType.Choice:
            {
                string v = over is JsonValue jv ? jv.GetValue<string>() : rule.Values[spec.Name] as string ?? "";
                if (ImGui.BeginCombo(id, spec.ChoiceLabel(v)))
                {
                    foreach (var choice in spec.Choices!)
                        if (ImGui.Selectable(spec.ChoiceLabel(choice), choice == v) && choice != v) Set(JsonValue.Create(choice));
                    ImGui.EndCombo();
                }
                break;
            }
            case ParamType.Text:
            {
                string current = over is JsonValue jv ? jv.GetValue<string>() : rule.Values[spec.Name] as string ?? "";
                string v = _draftKey == key ? _textDraft : current;
                if (ImGui.InputTextWithHint(id, "default", ref v, 128)) { _draftKey = key; _textDraft = v; }
                if (ImGui.IsItemDeactivatedAfterEdit() && _draftKey == key)
                {
                    _draftKey = null;
                    Set(_textDraft.Trim().Length == 0 ? null : JsonValue.Create(_textDraft.Trim()));
                }
                break;
            }
        }
        ImGui.SameLine();
        if (overridden)
        {
            if (ImGui.Button("×", new Vector2(resetW, 0))) Set(null);
            Tip("Back to the rules file's value");
        }
        else ImGui.Dummy(new Vector2(resetW, 0));
    }

    /// <summary>Moves the mesh's overrides for <paramref name="rule"/> into the rule (this level's file), in one undo step.</summary>
    void ApplyToRule(EditorContext ctx, PolyMesh mesh, Rule rule, string overrides)
    {
        if (JsonNode.Parse(overrides) is not JsonObject values) return;
        int id = mesh.Id;
        if (feature.EditRules($"Apply {mesh.Name}'s values to {rule.Id}", w => values.All(kv => w.Set(rule.Id, kv.Key, kv.Value?.DeepClone())), [id],
                () => { if (ctx.Doc.Find(id) is { } m) m.Dressing = m.Dressing.WithoutOverrides(rule.Id); }))
            ctx.Status($"{rule.Id} now uses {mesh.Name}'s values everywhere");
    }

    /// <summary>One undo step changing the mesh's dressing overrides.</summary>
    static void Edit(EditorContext ctx, int meshId, string label, Func<MeshDressing, MeshDressing> change)
    {
        if (ctx.Doc.Find(meshId) is not { } mesh) return;
        var next = change(mesh.Dressing);
        if (next.Equals(mesh.Dressing)) return;
        ctx.Session.Do(label, [meshId], () => { if (ctx.Doc.Find(meshId) is { } m) m.Dressing = next; });
    }
}
