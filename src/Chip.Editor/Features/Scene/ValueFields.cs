using System.Numerics;
using System.Text.Json;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.Scene;

/// <summary>
/// The field for one typed value (a class property, an item of a list, a connection's argument): a checkbox, a drag
/// field, a dropdown, a colour, a reference or an asset. Drags and pickers commit once, when the field lets go. One
/// instance serves many fields; each call names its field so two never share an edit.
/// </summary>
public sealed class ValueFields
{
    readonly TextEdits _text = new();
    // A value being dragged or picked, committed as one step when the field lets go.
    (int Entity, string Field)? _draftKey;
    Vector4 _draft;
    string _refFilter = "";

    /// <summary>
    /// Draws the field, its width already set. <paramref name="e"/> is the entity the value belongs to (a reference never
    /// offers it); <paramref name="name"/> tells the field from the entity's others.
    /// </summary>
    public void Draw(EditorContext ctx, Entity e, PropSpec spec, JsonElement value, string name, float fieldW, Action<JsonElement> Commit)
    {
        int id = e.Id;
        var key = (id, name);
        switch (spec.Type)
        {
            case PropType.Bool:
            {
                bool v = EntityValues.Bool(value) == true;
                if (ImGui.Checkbox("##v", ref v)) Commit(EntityValues.Of(v));
                break;
            }
            case PropType.Int:
            {
                int v = _draftKey == key ? (int)_draft.X : (int)(EntityValues.Number(value) ?? 0);
                var (min, max) = Limits(spec, int.MinValue, int.MaxValue);
                if (ImGui.DragInt("##v", ref v, 0.1f, (int)min, (int)max, "%d" + Suffix(spec), Clamp(spec))) { _draftKey = key; _draft.X = v; }
                if (ImGui.IsItemDeactivatedAfterEdit() && _draftKey == key) { _draftKey = null; Commit(EntityValues.Of((long)_draft.X)); }
                break;
            }
            case PropType.Float:
            {
                float v = _draftKey == key ? _draft.X : (float)(EntityValues.Number(value) ?? 0);
                var (min, max) = Limits(spec, -1e9, 1e9);
                float speed = spec.Range is { } rg && rg.Max > rg.Min ? (float)(rg.Max - rg.Min) / 300 : MathF.Max(0.001f, MathF.Abs(v) * 0.01f);
                if (ImGui.DragFloat("##v", ref v, speed, (float)min, (float)max, "%.3g" + Suffix(spec), Clamp(spec))) { _draftKey = key; _draft.X = v; }
                if (ImGui.IsItemDeactivatedAfterEdit() && _draftKey == key) { _draftKey = null; Commit(EntityValues.Of(_draft.X)); }
                break;
            }
            case PropType.String:
                if (_text.Field($"##s{id}/{name}", EntityValues.Text(value), out var text, width: fieldW)) Commit(EntityValues.Of(text));
                break;
            case PropType.Enum:
            {
                var current = spec.Options.FirstOrDefault(o => EntityValues.Same(o.Value, value));
                if (ImGui.BeginCombo("##v", current?.Label ?? EntityValues.Text(value)))
                {
                    foreach (var o in spec.Options)
                        if (ImGui.Selectable(o.Label, o == current) && o != current) Commit(o.Value);
                    ImGui.EndCombo();
                }
                break;
            }
            case PropType.Color:
            {
                var v = _draftKey == key ? _draft : EntityValues.Color(value) ?? Vector4.One;
                if (ImGui.ColorEdit4("##v", ref v, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf)) { _draftKey = key; _draft = v; }
                // The picker popup keeps editing after the field lets go: commit once nothing is being edited.
                if (_draftKey == key && !ImGui.IsAnyItemActive() && !ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel))
                {
                    _draftKey = null;
                    Commit(EntityValues.Color(_draft));
                }
                break;
            }
            case PropType.Vector2 or PropType.Vector3:
            {
                int n = spec.Type == PropType.Vector2 ? 2 : 3;
                var v = EntityValues.Vector(value, n) ?? new float[n];
                string[] tags = n == 2 ? ["X", "Y"] : ["X", "Y", "Z"];
                if (TaggedFields("##v", tags, Theme.AxisU32, v, new bool[n], spec.Integer ? "%.0f" : "%.3f", out int axis, out float av, fieldW))
                {
                    v[axis] = av;
                    Commit(EntityValues.Array(v));
                }
                break;
            }
            case PropType.Node:
                ReferenceField(ctx, e, spec, value, name, fieldW, Commit);
                break;
            case PropType.Resource when spec.Kind != null && EntityClasses.Project?.Kinds.GetValueOrDefault(spec.Kind) is { } kind:
                if (AssetPicker.Draw("##v", kind, value, out var asset)) Commit(asset);
                break;
            case PropType.Resource:
                if (_text.Field($"##r{id}/{name}", EntityValues.Text(value), out var path, hint: AssetHint(), width: fieldW))
                    Commit(path.Trim().Length == 0 ? EntityValues.Null : EntityValues.Of(path.Trim()));
                break;
            default:
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(Theme.TextDisabled, $"Set in {EntityClasses.Engine} ({spec.EngineType})");
                break;
        }
    }

    /// <summary>"Empty", "1 item", "3 items": a list's row, its items following under it (<see cref="ListItems"/>).</summary>
    public static void ListCount(JsonElement value)
    {
        int n = value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : 0;
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextDim, n == 0 ? "Empty" : n == 1 ? "1 item" : $"{n} items");
    }

    /// <summary>
    /// A list's items under its row: the number in the label column (ending at <paramref name="labelW"/>), a field of the
    /// item type, and a remove button <paramref name="buttonW"/> wide; then a button that adds an item (the last one again,
    /// so a run of similar items is quick). Every change commits the whole list.
    /// </summary>
    public void ListItems(EditorContext ctx, Entity e, PropSpec spec, JsonElement value, string name, float labelW, float fieldW, float buttonW, Action<JsonElement> commit)
    {
        var items = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToList() : [];
        var item = spec.Item;
        for (int i = 0; i < items.Count; i++)
        {
            int at = i;
            ImGui.PushID(i);
            ImGui.AlignTextToFramePadding();
            ImGui.SetCursorPosX(labelW - ImGui.CalcTextSize($"{i + 1}").X - Theme.Px(8));
            ImGui.TextColored(Theme.TextDisabled, $"{i + 1}");
            ImGui.SameLine(labelW);
            ImGui.SetNextItemWidth(fieldW);
            Draw(ctx, e, item, items[i], $"{name}[{i}]", fieldW, v => commit(EntityValues.List(items.Select((x, j) => j == at ? v : x))));
            ImGui.SameLine();
            if (IconButton("remove", Icon.Trash, $"Remove item {i + 1}", buttonW))
                commit(EntityValues.List(items.Where((_, j) => j != at)));
            ImGui.PopID();
        }
        ImGui.SetCursorPosX(labelW);
        if (ImGui.Button("Add item", new Vector2(fieldW, 0)))
            commit(EntityValues.List([.. items, items.Count > 0 && item.Type != PropType.Node ? items[^1] : NewItem(item)]));
        Tip(item.Type == PropType.Node || items.Count == 0 ? $"Add a {EntityValues.TypeName(item.Type)}" : "Add a copy of the last item");
    }

    /// <summary>A fresh item: the first choice, white, zero, empty text, or none.</summary>
    public static JsonElement NewItem(PropSpec item) => item.Type switch
    {
        PropType.Bool => EntityValues.Of(false),
        PropType.Int => EntityValues.Of(0L),
        PropType.Float => EntityValues.Of(0.0),
        PropType.String => EntityValues.Of(""),
        PropType.Enum when item.Options.Count > 0 => item.Options[0].Value,
        PropType.Color => EntityValues.Color(Vector4.One),
        PropType.Vector2 => EntityValues.Array(0, 0),
        PropType.Vector3 => EntityValues.Array(0, 0, 0),
        _ => EntityValues.Null,
    };

    /// <summary>
    /// Picks another entity (or none) for a reference: a searchable list of the entities the property accepts, an
    /// eyedropper to click one in the viewport, and a button that selects the current target.
    /// </summary>
    void ReferenceField(EditorContext ctx, Entity e, PropSpec spec, JsonElement value, string name, float fieldW, Action<JsonElement> commit)
    {
        float button = ImGui.GetFrameHeight();
        float gap = ImGui.GetStyle().ItemSpacing.X;
        var target = EntityValues.RefId(value) is { } tid ? ctx.Doc.FindEntity(tid) : null;
        bool fits = target == null || Logic.EntityRefs.Accepts(spec, target);
        string owner = $"{e.Id}/{name}";
        bool armed = Logic.LogicFeature.Picker.Armed(ctx, owner);
        string preview = armed ? "Click one in the viewport…"
            : target != null ? SceneOps.DisplayName(target)
            : EntityValues.RefId(value) is { } missing ? $"Missing entity {missing}" : "None";

        ImGui.SetNextItemWidth(fieldW - (button + gap) * 2);
        if (!fits) ImGui.PushStyleColor(ImGuiCol.Text, Theme.Error);
        bool open = ImGui.BeginCombo("##v", preview, ImGuiComboFlags.HeightLarge);
        if (!fits) ImGui.PopStyleColor();
        if (!open)
        {
            if (ImGui.IsItemHovered() && target != null) Logic.LinkDraw.Highlight = target.Id;
            if (!fits) Tip($"{SceneOps.DisplayName(target!)} isn't {Logic.EntityRefs.Wants(spec)}. Pick another");
        }
        else
        {
            if (ImGui.IsWindowAppearing()) { _refFilter = ""; ImGui.SetKeyboardFocusHere(); }
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##filter", "Search", ref _refFilter, 64);
            if (ImGui.Selectable("None", target == null)) commit(EntityValues.Null);
            int shown = 0;
            foreach (var other in ctx.Doc.Entities)
            {
                if (other.Id == e.Id || !Logic.EntityRefs.Accepts(spec, other)) continue;
                string label = SceneOps.DisplayName(other);
                if (label != other.ClassName) label += $"  ({EntityClasses.Get(other.ClassName).Label})";
                if (_refFilter.Length > 0 && !label.Contains(_refFilter, StringComparison.OrdinalIgnoreCase)) continue;
                shown++;
                if (ImGui.Selectable($"{label}##{other.Id}", other == target) && other != target) commit(EntityValues.Ref(other.Id));
                if (ImGui.IsItemHovered()) Logic.LinkDraw.Highlight = other.Id;
            }
            if (shown == 0) ImGui.TextColored(Theme.TextDisabled, _refFilter.Length > 0 ? "No match" : $"The level has no {Logic.EntityRefs.Wants(spec, plural: true)}");
            ImGui.EndCombo();
        }

        ImGui.SameLine(0, gap);
        if (IconButton("pick", Icon.Target, $"Pick {Logic.EntityRefs.Wants(spec)} in the viewport", button))
        {
            if (armed) Logic.LogicFeature.Picker.Cancel(ctx);
            else
            {
                int id = e.Id;
                Logic.LogicFeature.Picker.Start(ctx, owner, Logic.EntityRefs.Wants(spec),
                    other => other.Id != id && Logic.EntityRefs.Accepts(spec, other),
                    other => commit(EntityValues.Ref(other.Id)));
            }
        }
        ImGui.SameLine(0, gap);
        ImGui.BeginDisabled(target == null);
        if (IconButton("goto", Icon.ChevronRight, target != null ? $"Select {SceneOps.DisplayName(target)}" : "Nothing to select", button) && target != null)
            Logic.EntityRefs.Select(ctx, target);
        ImGui.EndDisabled();
    }

    /// <summary>The resource field's hint: the project's asset prefix ("res://…"), else a plain path.</summary>
    static string AssetHint() => (EntityClasses.Project?.AssetPrefix ?? "res://") is { Length: > 0 } prefix ? prefix + "…" : "path…";

    static (double Min, double Max) Limits(PropSpec spec, double lo, double hi) => spec.Range is { } r
        ? (spec.OrLess ? lo : r.Min, spec.OrGreater ? hi : r.Max)
        : (lo, hi);

    /// <summary>Hard ranges clamp what you type; open ones (or_greater / or_less) only guide the drag.</summary>
    static ImGuiSliderFlags Clamp(PropSpec spec) =>
        spec.Range != null && !spec.OrGreater && !spec.OrLess ? ImGuiSliderFlags.AlwaysClamp : ImGuiSliderFlags.None;

    static string Suffix(PropSpec spec) => spec.Suffix.Length > 0 ? " " + spec.Suffix.Replace("%", "%%") : "";
}
