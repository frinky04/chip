using System.Numerics;
using System.Text.Json;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.Scene;

/// <summary>
/// The selected entities: class, name, position, rotation and properties. A class from the engine project lists its
/// properties with a field for each type (a list gets one per item; the engine's defaults shown until changed, × puts
/// one back); values the class doesn't declare are listed as unused. A class the project doesn't define keeps free
/// key-values. Every committed field is one undo step.
/// </summary>
/// <remarks>
/// Several entities edit together: each field shows the shared value, or "mixed" where they differ, and a change sets
/// every selected entity in one step. Position, rotation and vector fields are mixed per axis, and typing one axis sets
/// only that axis. Only the properties all their classes share are listed; when the classes differ, a row of class chips
/// narrows the selection to one class.
/// </remarks>
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
        if (entities.Count == 0) return;
        int n = entities.Count;
        var e = entities[0]; // stands in wherever they all agree
        int[] ids = entities.Select(x => x.Id).ToArray();
        // Field ids name the selection, so an edit in progress never carries over to other entities.
        string key = n == 1 ? $"{e.Id}" : $"m{n}x{ids.Aggregate(17, (h, id) => h * 31 + id)}";
        var classNames = entities.Select(x => x.ClassName).Distinct().ToList();
        bool oneClass = classNames.Count == 1;
        var cls = EntityClasses.Get(e.ClassName);
        if (!Heading(n == 1 ? "Entity" : "Entities", n == 1 ? cls.Label : oneClass ? $"{n} × {cls.Label}" : $"{n} selected")) return;

        RowLabel("Class", ClassTip());
        float arrow = ImGui.GetFrameHeight();
        if (_text.Field($"##class{key}", oneClass ? e.ClassName : "", out var typed, hint: oneClass ? null : $"mixed ({classNames.Count} classes)",
                width: ImGui.GetContentRegionAvail().X - arrow, mixed: !oneClass))
            SetClass(ctx, ids, typed);
        ImGui.SameLine(0, 0);
        if (ImGui.ArrowButton($"##classes{key}", ImGuiDir.Down)) { _classFilter = ""; ImGui.OpenPopup("##classlist"); }
        Tip(n == 1 ? "Pick a class" : $"Pick a class for all {n}");
        bool allMesh = entities.All(x => x.IsMeshEntity), anyMesh = entities.Any(x => x.IsMeshEntity);
        if (ClassPicker.Popup("##classlist", oneClass ? e.ClassName : null, ref _classFilter,
                allMesh ? ClassPicker.Mesh : !anyMesh ? ClassPicker.Point : null) is { } picked)
            SetClass(ctx, ids, picked);
        if (!oneClass) ClassChips(ctx, entities);
        else if (cls.Doc.Length > 0)
        {
            Dim(cls.Summary);
            if (cls.Summary != cls.Doc) Tip(cls.Doc);
        }
        if (classNames.Where(c => !EntityClasses.IsKnown(c)).ToList() is { Count: > 0 } unknown) Warn(UnknownClasses(unknown));

        var names = entities.Select(x => x.Name).Distinct().ToList();
        bool mixedName = names.Count > 1;
        RowLabel("Name", n == 1 ? "Exported as the node name; leave empty to use the class"
            : "Exported as the node name. Typing one gives it to all of them; leave empty to use each one's class");
        if (_text.Field($"##name{key}", mixedName ? "" : e.Name, out var name, hint: mixedName ? "mixed" : oneClass ? e.ClassName : "class name", mixed: mixedName))
            Edit(ctx, ids, Plural("Rename", n), x => { x.Name = name.Trim(); return null; });

        RowLabel("Origin", allMesh
            ? "The entity's pivot in metres (a door's hinge). Its meshes stay put: this moves only the origin they turn around in the game."
            : "Position of the origin in metres. Type a value, then Enter." + (n > 1 ? " A typed axis moves each one to it; the other axes stay as they are" : ""));
        var p = e.Position;
        if (TaggedFields($"##pos{key}", ["X", "Y", "Z"], Theme.AxisU32, [p.X, p.Y, p.Z], EntityMultiEdit.MixedAxes(entities.Select(x => x.Position).ToList()),
                "%.3f", out int pi, out float pv))
            Edit(ctx, ids, n > 1 ? Plural("Move", n) : allMesh ? "Move Entity Origin" : "Move Entity", x => { x.Position = EntityMultiEdit.WithAxis(x.Position, pi, pv); return null; });

        RowLabel("Angles", allMesh
            ? "The origin's axes in degrees: pitch (X), yaw (Y), roll (Z). Its meshes stay put; the game sees them in this frame."
            : "Rotation in degrees: pitch (X), yaw (Y), roll (Z). Yaw 0 faces -Z." + (n > 1 ? " A typed axis turns each one to it; the other axes stay as they are" : ""));
        var r = e.Rotation;
        if (TaggedFields($"##rot{key}", ["X", "Y", "Z"], Theme.AxisU32, [r.X, r.Y, r.Z], EntityMultiEdit.MixedAxes(entities.Select(x => x.Rotation).ToList()),
                "%.1f", out int ri, out float rv))
            Edit(ctx, ids, Plural("Rotate", n), x => { x.Rotation = EntityMultiEdit.WithAxis(x.Rotation, ri, rv); return null; });

        if (anyMesh) MeshRow(ctx, entities);
        else if (oneClass && cls.IsMesh) Warn($"{cls.Label} is made of meshes: select some and use Tie to entity (Ctrl+Alt+T)");

        // An editor-only class has its own section (reference models), and only for one entity.
        if (entities.Any(x => EntityClasses.Get(x.ClassName).EditorOnly)) return;
        bool allKnown = classNames.All(EntityClasses.IsKnown), allUnknown = classNames.All(c => !EntityClasses.IsKnown(c));
        var common = EntityMultiEdit.CommonProps(entities);
        if (common.Count > 0) DrawClassProperties(ctx, entities, common);
        else if (allKnown && !oneClass)
        {
            SubHeading("Properties");
            Dim("These classes share no properties. Pick a class above to edit only its entities");
        }

        var free = EntityMultiEdit.FreeKeys(entities);
        if (allUnknown)
        {
            SubHeading("Properties");
            DrawFreeProperties(ctx, entities, free, allowAdd: true);
        }
        else if (free.Count > 0)
        {
            SubHeading(allKnown ? "Unused values" : "Other values");
            string them = free.Count == 1 ? "it" : "them";
            Dim(!allKnown ? "Values of classes the project doesn't define, and ones a class doesn't declare. They're kept and exported"
                : oneClass ? $"{cls.Label} has no property called {string.Join(", ", free)}, so {EntityClasses.Engine} ignores {them}. Remove {them}, or add the property in {EntityClasses.Engine}"
                : $"Their classes have no property called {string.Join(", ", free)}, so {EntityClasses.Engine} ignores {them}. Remove {them}, or add the property in {EntityClasses.Engine}");
            DrawFreeProperties(ctx, entities, free, allowAdd: false);
        }
    }

    static string ClassTip() => EntityClasses.Current.Source is { } source
        ? EntityClasses.Project is { IsGodot: true, File: null }
            ? $"Entity classes are the scenes in your Godot project's entity folders ({EntityClasses.ProjectRoot})"
            : $"Entity classes come from {source}"
        : EntityClasses.Problem ?? "Built-in classes. Save the level inside a game project (a Godot project with the Chip import plugin, or a folder with chip.project.json) to use its classes";

    static string UnknownClasses(List<string> names)
    {
        string list = And(names);
        string noun = names.Count == 1 ? $"no {list} class" : $"no {list} classes";
        return EntityClasses.Project is { } project
            ? project.IsGodot
                ? $"{Path.GetFileName(project.Root)} has {noun}. Values are kept and exported; Godot makes {(names.Count == 1 ? "it a Marker3D" : "them Marker3Ds")}. Pick a class, or add the scene in Godot"
                : $"{Path.GetFileName(project.Root)} has {noun}. Values are kept and exported. Pick a class, or add {(names.Count == 1 ? "it" : "them")} to {project.Relative(project.Manifest)}"
            : names.Count == 1
                ? $"{list} isn't a built-in class. Save the level inside a game project to use its classes"
                : $"{list} aren't built-in classes. Save the level inside a game project to use its classes";
    }

    /// <summary>
    /// One chip per class in a mixed selection, most common first ("Omni light 3"): clicking one keeps only those
    /// entities selected, the quick way to edit one class's own properties.
    /// </summary>
    static void ClassChips(EditorContext ctx, IReadOnlyList<Entity> entities)
    {
        RowLabel("Classes", "The selection's classes. Click one to select only its entities");
        float left = ImGui.GetCursorPosX(), right = ImGui.GetWindowContentRegionMax().X;
        float gap = Theme.Px(4);
        bool first = true;
        foreach (var group in entities.GroupBy(x => x.ClassName).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            var members = group.ToList();
            string label = EntityClasses.Get(group.Key).Label;
            string text = $"{label}  {members.Count}";
            float w = ImGui.CalcTextSize(text).X + ImGui.GetStyle().FramePadding.X * 2;
            if (!first)
            {
                ImGui.SameLine(0, gap);
                if (ImGui.GetCursorPosX() + w > right) { ImGui.NewLine(); ImGui.SetCursorPosX(left); }
            }
            first = false;
            if (ImGui.Button($"{text}##chip{group.Key}"))
            {
                ctx.Sel.Clear();
                foreach (var m in members) ctx.Sel.Add(ElementRef.Object(m.Id));
                ctx.Status(members.Count == 1 ? $"Selected {SceneOps.DisplayName(members[0])}" : $"Selected the {members.Count} {label} entities");
            }
            Tip(members.Count == 1 ? $"Select only {SceneOps.DisplayName(members[0])}" : $"Select only the {members.Count} {label} entities");
        }
    }

    static void MeshRow(EditorContext ctx, IReadOnlyList<Entity> entities)
    {
        var tied = entities.Where(x => x.IsMeshEntity).ToList();
        int meshes = tied.Sum(x => x.Meshes.Count);
        RowLabel("Meshes", "The geometry these entities are made of. Selecting any of it selects the whole entity.");
        ImGui.AlignTextToFramePadding();
        string count = meshes == 1 ? "1 mesh" : $"{meshes} meshes";
        ImGui.Text(entities.Count == 1 ? count : tied.Count == 1 ? $"{count} in 1 entity" : $"{count} in {tied.Count} entities");
        ImGui.SameLine();
        if (ImGui.SmallButton("Untie")) ctx.Commands.Execute("scene.untie");
        Tip(tied.Count == 1 ? "Turn it back into plain meshes (Ctrl+Shift+W). The meshes stay where they are"
            : "Turn them back into plain meshes (Ctrl+Shift+W). The meshes stay where they are");
        var pointClasses = tied.Select(x => EntityClasses.Get(x.ClassName)).Where(c => c.Known && !c.IsMesh).DistinctBy(c => c.Name).ToList();
        if (pointClasses.Count > 0)
            Warn(pointClasses.Count == 1
                ? $"{pointClasses[0].Label} is a point class, so the game may not expect geometry. Pick a mesh class"
                : $"{And(pointClasses.Select(c => c.Label).ToList())} are point classes, so the game may not expect geometry. Pick a mesh class");
    }

    // ---- Class properties ----

    void DrawClassProperties(EditorContext ctx, IReadOnlyList<Entity> entities, List<PropSpec> props)
    {
        string group = "";
        SubHeading("Properties");
        foreach (var spec in props)
        {
            if (spec.Group != group)
            {
                group = spec.Group;
                if (group.Length > 0) SubHeading(group, dim: true);
            }
            ImGui.PushID(spec.Name);
            Field(ctx, entities, spec);
            ImGui.PopID();
        }
    }

    void Field(EditorContext ctx, IReadOnlyList<Entity> entities, PropSpec spec)
    {
        int n = entities.Count;
        int[] ids = entities.Select(x => x.Id).ToArray();
        // Each entity reads the property against its own class (its own default and choices).
        var specs = entities.Select(x => EntityMultiEdit.SpecFor(x, spec)).ToList();
        int setCount = 0;
        string? error = null;
        for (int i = 0; i < n; i++)
            if (entities[i].Properties.TryGetValue(spec.Name, out var own))
            {
                setCount++;
                if (EntityValues.Coerce(specs[i], own, out var bad) == null) error ??= n == 1 ? bad : $"{SceneOps.DisplayName(entities[i])}: {bad}";
            }
        var values = entities.Select((x, i) => EntityValues.Effective(x, specs[i])).ToList();
        bool mixed = EntityMultiEdit.Mixed(values);
        float labelW = PropertyColumn;
        float resetW = ImGui.GetFrameHeight();

        ImGui.AlignTextToFramePadding();
        var labelCol = error != null ? Theme.Error : setCount > 0 ? Theme.Accent : Theme.TextDim;
        ImGui.TextColored(labelCol, Ellipsize(spec.Label, labelW - ImGui.GetCursorPosX() - Theme.Px(4)));
        Tip(FieldTip(spec, n, setCount, mixed, error));
        ImGui.SameLine(labelW);
        // Every field stops short of the × column, whatever kind it is.
        float fieldW = ImGui.GetContentRegionAvail().X - resetW - ImGui.GetStyle().ItemSpacing.X;
        string label = n == 1 ? $"Set {spec.Label}" : $"Set {spec.Label} on {n} Entities";
        void Apply(Func<JsonElement, JsonElement> change) => Edit(ctx, ids, label, x =>
        {
            var own = EntityMultiEdit.SpecFor(x, spec);
            return EntityOps.SetProperty(x, own, change(EntityValues.Effective(x, own)));
        });
        void Commit(JsonElement v) => Apply(_ => v);

        if (spec.Type == PropType.Array)
        {
            if (mixed) ValueFields.MixedList(n);
            else ValueFields.ListCount(values[0]);
            ImGui.SameLine(labelW + fieldW + ImGui.GetStyle().ItemSpacing.X);
        }
        else
        {
            ImGui.SetNextItemWidth(fieldW);
            _fields.Draw(ctx, entities, spec, values, spec.Name, fieldW, Apply);
            ImGui.SameLine();
        }

        if (setCount > 0)
        {
            if (ImGui.Button("×", new Vector2(resetW, 0)))
                Edit(ctx, ids, n == 1 ? $"Reset {spec.Label}" : $"Reset {spec.Label} on {n} Entities",
                    x => x.Properties.Remove(spec.Name) || n > 1 ? null : $"{spec.Label} already has its default");
            Tip(n == 1 ? $"Back to the class default ({Show(spec, spec.Default)})"
                : setCount == n ? $"Back to the class default on all {n}" : $"Back to the class default on the {setCount} that set it");
        }
        else ImGui.Dummy(new Vector2(resetW, 0));

        if (spec.Type == PropType.Array && !mixed) _fields.ListItems(ctx, entities, spec, values[0], spec.Name, labelW, fieldW, resetW, Commit);
    }

    static string FieldTip(PropSpec spec, int count, int setCount, bool mixed, string? error)
    {
        var lines = new List<string>();
        if (spec.Doc.Length > 0) lines.Add(spec.Doc);
        lines.Add($"{spec.Name}: {EntityValues.TypeName(spec)}{(spec.Class != null ? $" ({spec.Class})" : "")}");
        if (error != null) lines.Add($"{error}. Set a new value, or × to go back to the default");
        else if (count == 1) lines.Add(setCount > 0 ? $"Set on this entity. Class default: {Show(spec, spec.Default)}" : "The class default");
        else
        {
            lines.Add(mixed ? $"Differs across the {count} selected. A new value sets all of them" : $"The same on all {count} selected");
            lines.Add(setCount == 0 ? "The class default on all of them" : setCount == count ? "Set on all of them" : $"Set on {setCount} of {count}; the others have the class default");
        }
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

    /// <summary>
    /// One row per key: its name, value and a remove button. With several entities a key some of them lack is dimmed
    /// (its tip says how many have it) and a value that differs reads "mixed". Renaming and removing touch the entities
    /// that have the key; a new value goes to all of them when <paramref name="allowAdd"/> (free key-values), else to
    /// those that have it (unused values stay unused rather than spread).
    /// </summary>
    void DrawFreeProperties(EditorContext ctx, IReadOnlyList<Entity> entities, List<string> keys, bool allowAdd)
    {
        int n = entities.Count;
        int[] all = entities.Select(x => x.Id).ToArray();
        string sel = n == 1 ? $"{all[0]}" : $"m{n}x{all.Aggregate(17, (h, id) => h * 31 + id)}";
        float gap = Theme.Px(4);
        float button = ImGui.GetFrameHeight();
        float keyW = MathF.Floor((ImGui.GetContentRegionAvail().X - button - gap * 2) * 0.42f);
        float valueW = ImGui.GetContentRegionAvail().X - keyW - button - gap * 2;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(gap, Theme.Px(4)));

        foreach (var key in keys)
        {
            var holders = entities.Where(x => x.Properties.ContainsKey(key)).ToList();
            int[] have = holders.Select(x => x.Id).ToArray();
            var values = holders.Select(x => x.Properties[key]).ToList();
            bool mixed = EntityMultiEdit.Mixed(values);
            bool partial = holders.Count < n;
            ImGui.PushID(key);
            if (partial) ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
            if (_text.Field($"##k{sel}/{key}", key, out var newKey, width: keyW))
                Edit(ctx, have, Many("Rename Property", have.Length), x => EntityOps.RenameProperty(x, key, newKey));
            if (partial) ImGui.PopStyleColor();
            if (partial) Tip($"Set on {holders.Count} of the {n} selected");
            ImGui.SameLine();
            var targets = allowAdd ? all : have;
            if (_text.Field($"##v{sel}/{key}", mixed ? "" : EntityValues.Text(values[0]), out var value, hint: mixed ? "mixed" : null, width: valueW, mixed: mixed || partial && allowAdd))
                Edit(ctx, targets, Many("Edit Property", targets.Length), x => EntityOps.SetProperty(x, key, Guess(value)));
            if (mixed) Tip($"Differs across the {holders.Count} that set it. A new value sets {(allowAdd ? $"all {n}" : "each of them")}");
            else if (partial && allowAdd) Tip($"Set on {holders.Count} of the {n} selected. A new value sets all of them");
            ImGui.SameLine();
            if (IconButton($"remove{key}", Icon.Trash, have.Length == 1 ? $"Remove {key}" : $"Remove {key} from {have.Length} entities", button))
                Edit(ctx, have, Many("Remove Property", have.Length), x => x.Properties.Remove(key) ? null : $"There is no property called {key}");
            ImGui.PopID();
        }

        if (allowAdd)
        {
            // New key-value: Enter in either field, or the plus button, adds it.
            ImGui.SetNextItemWidth(keyW);
            bool add = ImGui.InputTextWithHint($"##newkey{sel}", "key", ref _newKey, 128, ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(valueW);
            add |= ImGui.InputTextWithHint($"##newvalue{sel}", "value", ref _newValue, 256, ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();
            add |= IconButton($"add{sel}", Icon.Plus, n == 1 ? "Add the property" : $"Add the property to all {n}", button);
            if (add && _newKey.Trim().Length > 0)
            {
                string k = _newKey.Trim(), v = _newValue;
                bool existed = entities.Any(x => x.Properties.ContainsKey(k));
                if (Edit(ctx, all, Many("Add Property", n), x => EntityOps.SetProperty(x, k, Guess(v))))
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

    /// <summary>"a", "a and b", "a, b and c".</summary>
    static string And(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Join("", items) : $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}";

    /// <summary>"Rotate Entity", "Rotate 3 Entities".</summary>
    static string Plural(string verb, int n) => n == 1 ? $"{verb} Entity" : $"{verb} {n} Entities";

    /// <summary>"Edit Property", "Edit Property on 3 Entities".</summary>
    static string Many(string label, int n) => n <= 1 ? label : $"{label} on {n} Entities";

    static void SetClass(EditorContext ctx, int[] ids, string className)
    {
        if (className.Trim().Length == 0) { ctx.Status(ids.Length == 1 ? "An entity needs a class name" : "Entities need a class name", error: true); return; }
        Edit(ctx, ids, ids.Length == 1 ? "Change Entity Class" : $"Change Class of {ids.Length} Entities", x => { EntityOps.SetClass(x, className); return null; });
    }

    /// <summary>
    /// One undo step on the entity. The edit returns an error message to abandon the step (nothing changes) and report why.
    /// </summary>
    public static bool Edit(EditorContext ctx, int entityId, string label, Func<Entity, string?> edit) => Edit(ctx, [entityId], label, edit);

    /// <summary>
    /// One undo step on several entities, the edit run on each. An error from any of them abandons the whole step
    /// (nothing changes on any) and is reported, naming the entity when there are several.
    /// </summary>
    public static bool Edit(EditorContext ctx, IReadOnlyList<int> entityIds, string label, Func<Entity, string?> edit)
    {
        string? error = null;
        ctx.Session.Begin(label);
        foreach (int id in entityIds)
        {
            if (ctx.Doc.FindEntity(id) is not { } e) { error = "That entity no longer exists"; break; }
            if (edit(e) is { } failed) { error = entityIds.Count == 1 ? failed : $"{SceneOps.DisplayName(e)}: {failed}"; break; }
        }
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
