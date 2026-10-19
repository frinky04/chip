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
/// overrides the rules file for this mesh only; it shows in orange with a reset button. Each change is one undo step
/// (drags commit when released, so the level re-dresses once).
/// </summary>
sealed class DressingOverrides
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
                foreach (var spec in rule.Kind.Params.Where(p => Editable(p.Type)))
                    Field(ctx, mesh, rule, spec, over?[spec.Name]);
            }
            ImGui.PopID();
            ImGui.Dummy(Theme.Px(0, 3));
        }
    }

    static void Header(EditorContext ctx, PolyMesh mesh, Rule rule, bool skipped)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text(rule.Id);
        ImGui.SameLine();
        ImGui.TextColored(Theme.TextDim, rule.Kind.Name);
        bool overridden = mesh.Dressing.OverridesFor(rule.Id) != null;
        string toggle = skipped ? "Switch on" : "Switch off";
        float w = ImGui.CalcTextSize(toggle).X + ImGui.GetStyle().FramePadding.X * 2;
        float resetW = overridden ? ImGui.CalcTextSize("Reset").X + ImGui.GetStyle().FramePadding.X * 2 + ImGui.GetStyle().ItemSpacing.X : 0;
        ImGui.SameLine(ImGui.GetContentRegionMax().X - w - resetW);
        if (overridden)
        {
            if (ImGui.Button("Reset")) Edit(ctx, mesh.Id, "Reset dressing overrides", d => d.WithoutOverrides(rule.Id));
            Tip($"Go back to the rules file's values for {rule.Id} on this mesh");
            ImGui.SameLine();
        }
        if (ImGui.Button(toggle)) Edit(ctx, mesh.Id, skipped ? "Switch dressing rule on" : "Switch dressing rule off", d => d.WithSkip(rule.Id, !skipped));
        Tip(skipped ? $"Let {rule.Id} dress this mesh again" : $"Stop {rule.Id} dressing this mesh (other meshes keep it)");
    }

    void Field(EditorContext ctx, PolyMesh mesh, Rule rule, ParamSpec spec, JsonNode? over)
    {
        bool overridden = over != null;
        float avail = ImGui.GetContentRegionAvail().X;
        float labelW = MathF.Min(avail * 0.45f, ImGui.CalcTextSize(spec.Name).X + Theme.Px(10));
        float resetW = Theme.Px(22);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(overridden ? Theme.Accent : Theme.TextDim, spec.Name);
        Tip(spec.Description + (overridden ? "\nSet for this mesh only." : ""));
        ImGui.SameLine(MathF.Max(labelW, avail * 0.45f));
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - resetW - ImGui.GetStyle().ItemSpacing.X);
        var key = (mesh.Id, rule.Id, spec.Name);
        string id = $"##{spec.Name}";
        void Set(JsonNode? value) => Edit(ctx, mesh.Id, $"Dressing: {rule.Id} {spec.Name}", d => d.WithParam(rule.Id, spec.Name, value));

        switch (spec.Type)
        {
            case ParamType.Number:
            {
                float v = _draftKey == key ? _draft.X : over is JsonValue jv ? (float)jv.GetValue<double>() : rule.Values[spec.Name] is float f ? f : 0;
                float min = (float)Math.Max(spec.Min, -1e6), max = (float)Math.Min(spec.Max, 1e6);
                if (ImGui.DragFloat(id, ref v, MathF.Max(0.001f, MathF.Abs(v) * 0.01f), min, max, "%.3f")) { _draftKey = key; _draft.X = v; }
                if (ImGui.IsItemDeactivatedAfterEdit() && _draftKey == key) { _draftKey = null; Set(JsonValue.Create(Math.Round(_draft.X, 4))); }
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
                var v = _draftKey == key ? _draft : over is JsonArray { Count: 2 } ja ? new Vector2((float)ja[0]!.GetValue<double>(), (float)ja[1]!.GetValue<double>())
                    : rule.Values[spec.Name] is Vector2 p ? p : Vector2.Zero;
                if (ImGui.DragFloat2(id, ref v, 0.01f, -1e4f, 1e4f, "%.2f")) { _draftKey = key; _draft = v; }
                if (ImGui.IsItemDeactivatedAfterEdit() && _draftKey == key) { _draftKey = null; Set(new JsonArray(Math.Round(_draft.X, 4), Math.Round(_draft.Y, 4))); }
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
                if (ImGui.BeginCombo(id, v))
                {
                    foreach (var choice in spec.Choices!)
                        if (ImGui.Selectable(choice, choice == v) && choice != v) Set(JsonValue.Create(choice));
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

    /// <summary>One undo step changing the mesh's dressing overrides.</summary>
    static void Edit(EditorContext ctx, int meshId, string label, Func<MeshDressing, MeshDressing> change)
    {
        if (ctx.Doc.Find(meshId) is not { } mesh) return;
        var next = change(mesh.Dressing);
        if (next.Equals(mesh.Dressing)) return;
        ctx.Session.Do(label, [meshId], () => { if (ctx.Doc.Find(meshId) is { } m) m.Dressing = next; });
    }
}
