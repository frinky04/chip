using System.Numerics;
using Chip.Core.Scene;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.Scene;

/// <summary>The entity classes as rows (icon, label, class name), filtered by name or label; shared by the entity tool and inspector.</summary>
public static class ClassPicker
{
    /// <summary>Draws the popup when it's open. Returns the class clicked, if any.</summary>
    public static string? Popup(string id, string current, ref string filter)
    {
        if (!ImGui.BeginPopup(id)) return null;
        if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(Theme.Px(240));
        ImGui.InputTextWithHint("##filter", "Filter classes", ref filter, 64);
        ImGui.BeginChild("##classes", new Vector2(Theme.Px(240), MathF.Min(Theme.Px(320), Rows(filter) * ImGui.GetFrameHeight() + Theme.Px(4))));
        var picked = Draw(current, filter);
        ImGui.EndChild();
        if (picked != null) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
        return picked;
    }

    static int Rows(string filter) => Math.Max(1, EntityClasses.All.Count(c => Matches(c, filter)));

    static bool Matches(EntityClass c, string filter) => filter.Trim() is not { Length: > 0 } f
        || c.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || c.Label.Contains(f, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One row per class matching <paramref name="filter"/>, the current one selected (none when <paramref name="current"/>
    /// is null). <paramref name="references"/> false leaves out the reference model class. Returns the class clicked.
    /// </summary>
    public static string? Draw(string? current, string filter, bool references = true)
    {
        string? picked = null;
        bool any = false;
        foreach (var c in EntityClasses.All)
        {
            if (!Matches(c, filter) || !references && c.Name == ReferenceModels.ClassName) continue;
            any = true;
            var (icon, col) = EntityDraw.Style(c.Name);
            var p = ImGui.GetCursorScreenPos();
            float h = ImGui.GetFrameHeight();
            if (ImGui.Selectable($"##{c.Name}", current == c.Name, ImGuiSelectableFlags.None, new Vector2(0, h))) picked = c.Name;
            Tip(c.Doc.Length > 0 ? $"{c.Name}\n{c.Summary}" : c.Name); // the full doc is in the inspector
            var dl = ImGui.GetWindowDrawList();
            Icons.Draw(dl, icon, p + new Vector2(Theme.Px(4) + Fonts.IconSize * 0.5f, h * 0.5f), col);
            float textY = MathF.Round(p.Y + (h - ImGui.GetTextLineHeight()) * 0.5f);
            float x = p.X + Theme.Px(10) + Fonts.IconSize;
            dl.AddText(new Vector2(x, textY), ImGui.ColorConvertFloat4ToU32(Theme.Text), c.Label);
            // The class name when the label doesn't already say it ("enemies/grunt" beside "Grunt").
            if (!string.Equals(EntityClass.Humanize(c.Name), c.Label, StringComparison.OrdinalIgnoreCase) || c.Name.Contains('/'))
                dl.AddText(new Vector2(x + ImGui.CalcTextSize(c.Label).X + Theme.Px(8), textY), ImGui.ColorConvertFloat4ToU32(Theme.TextDim), c.Name);
        }
        if (!any) ImGui.TextColored(Theme.TextDim, "No class matches");
        return picked;
    }
}
