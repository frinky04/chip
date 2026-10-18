using System.Numerics;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.SelectionTools;

/// <summary>
/// Named selection sets for this session. The .chip schema is frozen for Wave 1, so sets aren't saved with the document;
/// they refer to elements by mesh id and index and drop whatever no longer resolves when restored.
/// </summary>
public sealed class SelectionSets
{
    readonly List<SelectionSet> _sets = new();
    int _counter;

    public IReadOnlyList<SelectionSet> All => _sets;
    /// <summary>The set saved or restored most recently (what "Restore selection set" brings back).</summary>
    public SelectionSet? Last { get; private set; }

    public string NextName => $"Set {_counter + 1}";

    public void Save(EditorContext ctx, string? name)
    {
        if (ctx.Sel.IsEmpty) { ctx.Status("Select something first, then save it as a set"); return; }
        _counter++;
        name = string.IsNullOrWhiteSpace(name) ? $"Set {_counter}" : name.Trim();
        var set = SelectionSet.Capture(ctx.Sel, name);
        int existing = _sets.FindIndex(s => s.Name == name);
        if (existing >= 0) _sets[existing] = set; else _sets.Add(set);
        Last = set;
        ctx.Status($"Saved {name} ({set.Mode.Count(set.Items.Length)})");
    }

    public void Restore(EditorContext ctx, SelectionSet set)
    {
        Last = set;
        int n = set.RestoreInto(ctx.Sel, ctx.Doc);
        if (n == 0) ctx.Status($"{set.Name} no longer matches anything: its elements were deleted. Update it from a new selection or delete it.", error: true);
        else ctx.Status(n < set.Items.Length ? $"Restored {set.Name} ({n} of {set.Items.Length}; the rest were deleted)" : $"Restored {set.Name}");
    }

    public void Update(EditorContext ctx, SelectionSet set)
    {
        int i = _sets.IndexOf(set);
        if (i < 0 || ctx.Sel.IsEmpty) return;
        _sets[i] = Last = SelectionSet.Capture(ctx.Sel, set.Name);
        ctx.Status($"Updated {set.Name} ({ctx.Sel.Mode.Count(ctx.Sel.Count)})");
    }

    public void Delete(SelectionSet set)
    {
        _sets.Remove(set);
        if (Last == set) Last = _sets.Count > 0 ? _sets[^1] : null;
    }
}

/// <summary>Inspector block: name and save the selection, click a set to restore it, right-click to update or delete.</summary>
public sealed class SelectionSetsSection(SelectionSets sets) : IInspectorSection
{
    string _name = "";

    public string Id => "selectionSets";
    public int Order => 150;
    public bool Visible(EditorContext ctx) => sets.All.Count > 0 || !ctx.Sel.IsEmpty;

    public void Draw(EditorContext ctx)
    {
        Heading("Selection sets", sets.All.Count > 0 ? sets.All.Count.ToString() : null);
        if (!ctx.Sel.IsEmpty)
        {
            var style = ImGui.GetStyle();
            float button = ImGui.CalcTextSize("Save").X + style.FramePadding.X * 2;
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - button - style.ItemSpacing.X);
            bool enter = ImGui.InputTextWithHint("##setName", sets.NextName, ref _name, 64, ImGuiInputTextFlags.EnterReturnsTrue);
            Tip("Name for the current selection. Sets last until you close Chip.");
            ImGui.SameLine();
            if (ImGui.Button("Save") || enter)
            {
                sets.Save(ctx, _name);
                _name = "";
            }
            Tip("Save the current selection as a set");
        }
        else if (sets.All.Count == 0) return;

        uint dim = ImGui.ColorConvertFloat4ToU32(Theme.TextDim);
        for (int i = 0; i < sets.All.Count; i++)
        {
            var set = sets.All[i];
            ImGui.PushID(i);
            if (ImGui.Selectable(set.Name)) sets.Restore(ctx, set);
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            string count = set.Mode.Count(set.Items.Length);
            float w = ImGui.CalcTextSize(count).X;
            ImGui.GetWindowDrawList().AddText(new Vector2(max.X - w, min.Y), dim, count);
            Tip("Click to restore. Right-click to update or delete.");
            if (ImGui.BeginPopupContextItem("##setMenu"))
            {
                if (ImGui.MenuItem("Update from selection", null, false, !ctx.Sel.IsEmpty)) sets.Update(ctx, set);
                if (ImGui.MenuItem("Delete")) sets.Delete(set);
                ImGui.EndPopup();
            }
            ImGui.PopID();
        }
    }
}
