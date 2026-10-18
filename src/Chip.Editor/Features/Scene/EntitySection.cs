using System.Numerics;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.Scene;

/// <summary>
/// The selected entity: class, name, position, rotation and its key-values (add, rename, edit, remove). Every committed
/// field is one undo step. With several entities selected it says how many and asks for one.
/// </summary>
public sealed class EntitySection : IInspectorSection
{
    readonly TextEdits _text = new();
    string _newKey = "", _newValue = "";

    public string Id => "entity";
    public int Order => 110;
    public bool Visible(EditorContext ctx) => ctx.Sel.Entities(ctx.Doc).Count > 0;

    public void Draw(EditorContext ctx)
    {
        var entities = ctx.Sel.Entities(ctx.Doc);
        if (entities.Count != 1)
        {
            Heading("Entities", $"{entities.Count} selected");
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
            ImGui.TextWrapped("Select one entity to edit its class and properties");
            ImGui.PopStyleColor();
            return;
        }
        var e = entities[0];
        int id = e.Id;
        Heading("Entity", EntityClasses.Get(e.ClassName).Label);

        RowLabel("Class", "Built-in classes are listed; any other name is a generic point entity");
        float arrow = ImGui.GetFrameHeight();
        if (_text.Field($"##class{id}", e.ClassName, out var cls, width: ImGui.GetContentRegionAvail().X - arrow))
            SetClass(ctx, id, cls);
        ImGui.SameLine(0, 0);
        if (ImGui.ArrowButton($"##classes{id}", ImGuiDir.Down)) ImGui.OpenPopup("##classlist");
        Tip("Pick a built-in class");
        if (ImGui.BeginPopup("##classlist"))
        {
            foreach (var c in EntityClasses.BuiltIn)
                if (ImGui.Selectable($"{c.Label}  ({c.Name})", c.Name == e.ClassName)) SetClass(ctx, id, c.Name);
            ImGui.EndPopup();
        }

        RowLabel("Name", "Exported as the node name; leave empty to use the class");
        if (_text.Field($"##name{id}", e.Name, out var name, hint: e.ClassName))
            Edit(ctx, id, "Rename Entity", x => { x.Name = name.Trim(); return null; });

        RowLabel("Origin", "Position of the origin in metres. Type a value, then Enter.");
        var p = e.Position;
        if (TaggedFields($"##pos{id}", ["X", "Y", "Z"], Theme.AxisU32, [p.X, p.Y, p.Z], [false, false, false], "%.3f", out int pi, out float pv))
            Edit(ctx, id, "Move Entity", x => { x.Position = With(x.Position, pi, pv); return null; });

        RowLabel("Angles", "Rotation in degrees: pitch (X), yaw (Y), roll (Z). Yaw 0 faces -Z.");
        var r = e.Rotation;
        if (TaggedFields($"##rot{id}", ["X", "Y", "Z"], Theme.AxisU32, [r.X, r.Y, r.Z], [false, false, false], "%.1f", out int ri, out float rv))
            Edit(ctx, id, "Rotate Entity", x => { x.Rotation = With(x.Rotation, ri, rv); return null; });

        DrawProperties(ctx, e);
    }

    void DrawProperties(EditorContext ctx, Entity e)
    {
        int id = e.Id;
        ImGui.Dummy(Theme.Px(0, 2));
        ImGui.PushFont(Fonts.Strong);
        ImGui.Text("Properties");
        ImGui.PopFont();
        float gap = Theme.Px(4);
        float button = ImGui.GetFrameHeight();
        float keyW = MathF.Floor((ImGui.GetContentRegionAvail().X - button - gap * 2) * 0.42f);
        float valueW = ImGui.GetContentRegionAvail().X - keyW - button - gap * 2;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(gap, Theme.Px(4)));

        foreach (var key in e.Properties.Keys.Order(StringComparer.Ordinal).ToList())
        {
            ImGui.PushID(key);
            if (_text.Field($"##k{id}/{key}", key, out var newKey, width: keyW))
                Edit(ctx, id, "Rename Property", x => EntityOps.RenameProperty(x, key, newKey));
            ImGui.SameLine();
            if (_text.Field($"##v{id}/{key}", e.Properties[key], out var value, width: valueW))
                Edit(ctx, id, "Edit Property", x => EntityOps.SetProperty(x, key, value));
            ImGui.SameLine();
            if (IconButton($"remove{key}", Icon.Trash, $"Remove {key}", button))
                Edit(ctx, id, "Remove Property", x => x.Properties.Remove(key) ? null : $"There is no property called {key}");
            ImGui.PopID();
        }

        // New key-value: Enter in either field, or the plus button, adds it.
        ImGui.SetNextItemWidth(keyW);
        bool add = ImGui.InputTextWithHint($"##newkey{id}", "key", ref _newKey, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(valueW);
        add |= ImGui.InputTextWithHint($"##newvalue{id}", "value", ref _newValue, 256, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        add |= IconButton($"add{id}", Icon.Plus, "Add the property", button);
        ImGui.PopStyleVar();
        if (add && _newKey.Trim().Length > 0)
        {
            string k = _newKey.Trim(), v = _newValue;
            bool existed = e.Properties.ContainsKey(k);
            if (Edit(ctx, id, "Add Property", x => EntityOps.SetProperty(x, k, v)))
            {
                if (existed) ctx.Status($"{k} already existed; its value is now {v}");
                _newKey = _newValue = "";
            }
        }
        else if (add) ctx.Status("Type a key for the new property, then press Enter", error: true);
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
