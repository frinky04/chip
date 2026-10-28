using System.Numerics;
using System.Text.Json;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.Scene;

/// <summary>
/// The selected entity: class, name, position, rotation and its properties. A class from the engine project lists its
/// properties with a field for each type (a list gets one per item; the engine's defaults shown until changed, × puts
/// one back); values the class
/// doesn't declare are listed as unused. A class the project doesn't define keeps free key-values. Every committed field
/// is one undo step. With several entities selected it says how many and asks for one.
/// </summary>
public sealed class EntitySection : IInspectorSection
{
    readonly TextEdits _text = new();
    string _newKey = "", _newValue = "", _classFilter = "", _refFilter = "";
    // A value being dragged or picked, committed as one step when the field lets go.
    (int Entity, string Prop)? _draftKey;
    Vector4 _draft;

    public string Id => "entity";
    public int Order => 110;
    public bool Visible(EditorContext ctx) => ctx.Sel.Entities(ctx.Doc).Count > 0;

    public void Draw(EditorContext ctx)
    {
        var entities = ctx.Sel.Entities(ctx.Doc);
        if (entities.Count != 1)
        {
            if (!Heading("Entities", $"{entities.Count} selected")) return;
            Dim("Select one entity to edit its class and properties");
            return;
        }
        var e = entities[0];
        int id = e.Id;
        var cls = EntityClasses.Get(e.ClassName);
        if (!Heading("Entity", cls.Label)) return;

        RowLabel("Class", ClassTip());
        float arrow = ImGui.GetFrameHeight();
        if (_text.Field($"##class{id}", e.ClassName, out var typed, width: ImGui.GetContentRegionAvail().X - arrow))
            SetClass(ctx, id, typed);
        ImGui.SameLine(0, 0);
        if (ImGui.ArrowButton($"##classes{id}", ImGuiDir.Down)) { _classFilter = ""; ImGui.OpenPopup("##classlist"); }
        Tip("Pick a class");
        if (ClassPicker.Popup("##classlist", e.ClassName, ref _classFilter, e.IsMeshEntity ? ClassPicker.Mesh : ClassPicker.Point) is { } picked)
            SetClass(ctx, id, picked);
        if (cls.Doc.Length > 0)
        {
            Dim(cls.Summary);
            if (cls.Summary != cls.Doc) Tip(cls.Doc);
        }
        if (!cls.Known)
            Warn(EntityClasses.Project is { } project
                ? project.IsGodot
                    ? $"{Path.GetFileName(project.Root)} has no {e.ClassName} class. Its values are kept and exported; Godot makes it a Marker3D. Pick a class, or add the scene in Godot"
                    : $"{Path.GetFileName(project.Root)} has no {e.ClassName} class. Its values are kept and exported. Pick a class, or add it to {project.Relative(project.Manifest)}"
                : $"{e.ClassName} isn't a built-in class. Save the level inside a game project to use its classes");

        RowLabel("Name", "Exported as the node name; leave empty to use the class");
        if (_text.Field($"##name{id}", e.Name, out var name, hint: e.ClassName))
            Edit(ctx, id, "Rename Entity", x => { x.Name = name.Trim(); return null; });

        bool mesh = e.IsMeshEntity;
        RowLabel("Origin", mesh
            ? "The entity's pivot in metres (a door's hinge). Its meshes stay put: this moves only the origin they turn around in the game."
            : "Position of the origin in metres. Type a value, then Enter.");
        var p = e.Position;
        if (TaggedFields($"##pos{id}", ["X", "Y", "Z"], Theme.AxisU32, [p.X, p.Y, p.Z], [false, false, false], "%.3f", out int pi, out float pv))
            Edit(ctx, id, mesh ? "Move Entity Origin" : "Move Entity", x => { x.Position = With(x.Position, pi, pv); return null; });

        RowLabel("Angles", mesh
            ? "The origin's axes in degrees: pitch (X), yaw (Y), roll (Z). Its meshes stay put; the game sees them in this frame."
            : "Rotation in degrees: pitch (X), yaw (Y), roll (Z). Yaw 0 faces -Z.");
        var r = e.Rotation;
        if (TaggedFields($"##rot{id}", ["X", "Y", "Z"], Theme.AxisU32, [r.X, r.Y, r.Z], [false, false, false], "%.1f", out int ri, out float rv))
            Edit(ctx, id, "Rotate Entity", x => { x.Rotation = With(x.Rotation, ri, rv); return null; });

        if (mesh)
        {
            RowLabel("Meshes", "The geometry this entity is made of. Selecting any of it selects the whole entity.");
            ImGui.AlignTextToFramePadding();
            ImGui.Text(e.Meshes.Count == 1 ? "1 mesh" : $"{e.Meshes.Count} meshes");
            ImGui.SameLine();
            if (ImGui.SmallButton("Untie")) ctx.Commands.Execute("scene.untie");
            Tip("Turn it back into plain meshes (Ctrl+Shift+W). The meshes stay where they are");
            if (cls.Known && !cls.IsMesh) Warn($"{cls.Label} is a point class, so the game may not expect geometry. Pick a mesh class");
        }
        else if (cls.IsMesh) Warn($"{cls.Label} is made of meshes: select some and use Tie to entity (Ctrl+Alt+T)");

        if (cls.EditorOnly) return; // its own section edits it (reference models)
        if (cls.Known)
        {
            DrawClassProperties(ctx, e, cls);
            var unused = e.Properties.Keys.Where(k => cls.Prop(k) == null).Order(StringComparer.Ordinal).ToList();
            if (unused.Count > 0)
            {
                SubHeading("Unused values");
                Dim($"{cls.Label} has no property called {string.Join(", ", unused)}, so {EntityClasses.Engine} ignores {(unused.Count == 1 ? "it" : "them")}. Remove {(unused.Count == 1 ? "it" : "them")}, or add the property in {EntityClasses.Engine}");
                DrawFreeProperties(ctx, e, unused, allowAdd: false);
            }
        }
        else
        {
            SubHeading("Properties");
            DrawFreeProperties(ctx, e, e.Properties.Keys.Order(StringComparer.Ordinal).ToList(), allowAdd: true);
        }
    }

    static string ClassTip() => EntityClasses.Current.Source is { } source
        ? EntityClasses.Project is { IsGodot: true, File: null }
            ? $"Entity classes are the scenes in your Godot project's entity folders ({EntityClasses.ProjectRoot})"
            : $"Entity classes come from {source}"
        : EntityClasses.Problem ?? "Built-in classes. Save the level inside a game project (a Godot project with the Chip import plugin, or a folder with chip.project.json) to use its classes";

    /// <summary>The resource field's hint: the project's asset prefix ("res://…"), else a plain path.</summary>
    static string AssetHint() => (EntityClasses.Project?.AssetPrefix ?? "res://") is { Length: > 0 } prefix ? prefix + "…" : "path…";

    // ---- Class properties ----

    void DrawClassProperties(EditorContext ctx, Entity e, EntityClass cls)
    {
        if (cls.Props.Count == 0) return;
        string group = "";
        SubHeading("Properties");
        foreach (var spec in cls.Props)
        {
            if (spec.Group != group)
            {
                group = spec.Group;
                if (group.Length > 0) SubHeading(group, dim: true);
            }
            ImGui.PushID(spec.Name);
            Field(ctx, e, spec);
            ImGui.PopID();
        }
    }

    void Field(EditorContext ctx, Entity e, PropSpec spec)
    {
        int id = e.Id;
        bool set = e.Properties.TryGetValue(spec.Name, out var own);
        string? error = null;
        bool readable = !set || EntityValues.Coerce(spec, own, out error) != null;
        var value = EntityValues.Effective(e, spec);
        float labelW = PropertyColumn;
        float resetW = ImGui.GetFrameHeight();

        ImGui.AlignTextToFramePadding();
        var labelCol = !readable ? Theme.Error : set ? Theme.Accent : Theme.TextDim;
        ImGui.TextColored(labelCol, Ellipsize(spec.Label, labelW - ImGui.GetCursorPosX() - Theme.Px(4)));
        Tip(FieldTip(spec, set, error));
        ImGui.SameLine(labelW);
        // Every field stops short of the × column, whatever kind it is.
        float fieldW = ImGui.GetContentRegionAvail().X - resetW - ImGui.GetStyle().ItemSpacing.X;
        string label = $"Set {spec.Label}";
        void Commit(JsonElement v) => Edit(ctx, id, label, x => EntityOps.SetProperty(x, spec, v));

        if (spec.Type == PropType.Array)
        {
            var items = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToList() : [];
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.TextDim, items.Count == 0 ? "Empty" : items.Count == 1 ? "1 item" : $"{items.Count} items");
            ImGui.SameLine(labelW + fieldW + ImGui.GetStyle().ItemSpacing.X);
        }
        else
        {
            ImGui.SetNextItemWidth(fieldW);
            ValueField(ctx, e, spec, value, spec.Name, fieldW, Commit);
            ImGui.SameLine();
        }

        if (set)
        {
            if (ImGui.Button("×", new Vector2(resetW, 0)))
                Edit(ctx, id, $"Reset {spec.Label}", x => x.Properties.Remove(spec.Name) ? null : $"{spec.Label} already has its default");
            Tip($"Back to the class default ({Show(spec, spec.Default)})");
        }
        else ImGui.Dummy(new Vector2(resetW, 0));

        if (spec.Type == PropType.Array) ListItems(ctx, e, spec, value, labelW, fieldW, resetW, Commit);
    }

    /// <summary>
    /// A list's items under its row: the number in the label column, a field of the item type, and a remove button in
    /// the × column; then a button that adds an item (the last one again, so a run of similar items is quick).
    /// </summary>
    void ListItems(EditorContext ctx, Entity e, PropSpec spec, JsonElement value, float labelW, float fieldW, float buttonW, Action<JsonElement> commit)
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
            ValueField(ctx, e, item, items[i], $"{spec.Name}[{i}]", fieldW, v => commit(EntityValues.List(items.Select((x, j) => j == at ? v : x))));
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
    static JsonElement NewItem(PropSpec item) => item.Type switch
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

    /// <summary>The field for one value of a single-valued type, its width already set. <paramref name="name"/> tells it from the entity's other fields.</summary>
    void ValueField(EditorContext ctx, Entity e, PropSpec spec, JsonElement value, string name, float fieldW, Action<JsonElement> Commit)
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

    static (double Min, double Max) Limits(PropSpec spec, double lo, double hi) => spec.Range is { } r
        ? (spec.OrLess ? lo : r.Min, spec.OrGreater ? hi : r.Max)
        : (lo, hi);

    /// <summary>Hard ranges clamp what you type; open ones (or_greater / or_less) only guide the drag.</summary>
    static ImGuiSliderFlags Clamp(PropSpec spec) =>
        spec.Range != null && !spec.OrGreater && !spec.OrLess ? ImGuiSliderFlags.AlwaysClamp : ImGuiSliderFlags.None;

    static string Suffix(PropSpec spec) => spec.Suffix.Length > 0 ? " " + spec.Suffix.Replace("%", "%%") : "";

    static string FieldTip(PropSpec spec, bool set, string? error)
    {
        var lines = new List<string>();
        if (spec.Doc.Length > 0) lines.Add(spec.Doc);
        lines.Add($"{spec.Name}: {EntityValues.TypeName(spec)}{(spec.Class != null ? $" ({spec.Class})" : "")}");
        if (error != null) lines.Add($"{error}. Set a new value, or × to go back to the default");
        else lines.Add(set ? $"Set on this entity. Class default: {Show(spec, spec.Default)}" : "The class default");
        return string.Join("\n", lines);
    }

    static string Show(PropSpec spec, JsonElement v) => spec.Type switch
    {
        _ when EntityValues.IsNull(v) => "none",
        PropType.Array when v.ValueKind == JsonValueKind.Array =>
            v.GetArrayLength() == 0 ? "empty" : string.Join(", ", v.EnumerateArray().Select(x => Show(spec.Item, x))),
        PropType.Enum => spec.Options.FirstOrDefault(o => EntityValues.Same(o.Value, v))?.Label ?? EntityValues.Text(v),
        _ => EntityValues.Text(v),
    };

    // ---- Free key-values (classes the project doesn't define, and unused values) ----

    void DrawFreeProperties(EditorContext ctx, Entity e, List<string> keys, bool allowAdd)
    {
        int id = e.Id;
        float gap = Theme.Px(4);
        float button = ImGui.GetFrameHeight();
        float keyW = MathF.Floor((ImGui.GetContentRegionAvail().X - button - gap * 2) * 0.42f);
        float valueW = ImGui.GetContentRegionAvail().X - keyW - button - gap * 2;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(gap, Theme.Px(4)));

        foreach (var key in keys)
        {
            ImGui.PushID(key);
            if (_text.Field($"##k{id}/{key}", key, out var newKey, width: keyW))
                Edit(ctx, id, "Rename Property", x => EntityOps.RenameProperty(x, key, newKey));
            ImGui.SameLine();
            if (_text.Field($"##v{id}/{key}", EntityValues.Text(e.Properties[key]), out var value, width: valueW))
                Edit(ctx, id, "Edit Property", x => EntityOps.SetProperty(x, key, Guess(value)));
            ImGui.SameLine();
            if (IconButton($"remove{key}", Icon.Trash, $"Remove {key}", button))
                Edit(ctx, id, "Remove Property", x => x.Properties.Remove(key) ? null : $"There is no property called {key}");
            ImGui.PopID();
        }

        if (allowAdd)
        {
            // New key-value: Enter in either field, or the plus button, adds it.
            ImGui.SetNextItemWidth(keyW);
            bool add = ImGui.InputTextWithHint($"##newkey{id}", "key", ref _newKey, 128, ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(valueW);
            add |= ImGui.InputTextWithHint($"##newvalue{id}", "value", ref _newValue, 256, ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();
            add |= IconButton($"add{id}", Icon.Plus, "Add the property", button);
            if (add && _newKey.Trim().Length > 0)
            {
                string k = _newKey.Trim(), v = _newValue;
                bool existed = e.Properties.ContainsKey(k);
                if (Edit(ctx, id, "Add Property", x => EntityOps.SetProperty(x, k, Guess(v))))
                {
                    if (existed) ctx.Status($"{k} already existed; its value is now {v}");
                    _newKey = _newValue = "";
                }
            }
            else if (add) ctx.Status("Type a key for the new property, then press Enter", error: true);
        }
        ImGui.PopStyleVar();
    }

    /// <summary>Typed text without a class to say what it is: a JSON number, bool, array or object as such, else a string.</summary>
    public static JsonElement Guess(string text)
    {
        var t = text.Trim();
        if (t.Length > 0 && (char.IsDigit(t[0]) || t[0] is '-' or '[' or '{' || t is "true" or "false"))
            try { return JsonDocument.Parse(t).RootElement.Clone(); }
            catch (JsonException) { }
        return EntityValues.Of(text);
    }

    // ---- Shared ----

    static void SubHeading(string text, bool dim = false)
    {
        ImGui.Dummy(Theme.Px(0, 2));
        if (dim) ImGui.TextColored(Theme.TextDim, text);
        else
        {
            ImGui.PushFont(Fonts.Strong);
            ImGui.Text(text);
            ImGui.PopFont();
        }
    }

    static void Dim(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    static void Warn(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Error);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    static Vector3 With(Vector3 v, int axis, float value) => axis == 0 ? v with { X = value } : axis == 1 ? v with { Y = value } : v with { Z = value };

    static void SetClass(EditorContext ctx, int id, string className)
    {
        if (className.Trim().Length == 0) { ctx.Status("An entity needs a class name", error: true); return; }
        Edit(ctx, id, "Change Entity Class", x => { EntityOps.SetClass(x, className); return null; });
    }

    /// <summary>
    /// One undo step on the entity. The edit returns an error message to abandon the step (nothing changes) and report why.
    /// </summary>
    public static bool Edit(EditorContext ctx, int entityId, string label, Func<Entity, string?> edit)
    {
        string? error = null;
        ctx.Session.Begin(label);
        if (ctx.Doc.FindEntity(entityId) is { } e) error = edit(e);
        else error = "That entity no longer exists";
        if (error != null)
        {
            ctx.Session.Cancel();
            ctx.Status(error, error: true);
            return false;
        }
        ctx.Session.End();
        return true;
    }
}
