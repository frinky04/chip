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
    readonly ValueFields _fields = new();
    string _newKey = "", _newValue = "", _classFilter = "";

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
            _fields.Draw(ctx, e, spec, value, spec.Name, fieldW, Commit);
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
            _fields.Draw(ctx, e, item, items[i], $"{spec.Name}[{i}]", fieldW, v => commit(EntityValues.List(items.Select((x, j) => j == at ? v : x))));
            ImGui.SameLine();
            if (IconButton("remove", Icon.Trash, $"Remove item {i + 1}", buttonW))
                commit(EntityValues.List(items.Where((_, j) => j != at)));
            ImGui.PopID();
        }
        ImGui.SetCursorPosX(labelW);
        if (ImGui.Button("Add item", new Vector2(fieldW, 0)))
            commit(EntityValues.List([.. items, items.Count > 0 && item.Type != PropType.Node ? items[^1] : ValueFields.NewItem(item)]));
        Tip(item.Type == PropType.Node || items.Count == 0 ? $"Add a {EntityValues.TypeName(item.Type)}" : "Add a copy of the last item");
    }

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
