using System.Numerics;
using System.Text.Json;
using Chip.Core.Scene;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.Scene;

/// <summary>
/// The dropdown for an asset property with a kind (<c>"kind": "sound"</c>): the project's files of that kind, searchable,
/// with their labels; a value naming no file shows in red. The list follows the folder as files come and go.
/// </summary>
public static class AssetPicker
{
    static string _filter = "";

    /// <summary>Draws the field at the width already set. True with the value to store (empty text for none) when the pick changed.</summary>
    public static bool Draw(string id, AssetKind kind, JsonElement value, out JsonElement picked)
    {
        picked = value;
        string current = EntityValues.IsNull(value) ? "" : EntityValues.Text(value);
        var entries = kind.List();
        var entry = current.Length > 0 ? entries.FirstOrDefault(e => e.Value.Equals(current, StringComparison.OrdinalIgnoreCase)) : null;
        bool missing = current.Length > 0 && entry == null;
        string preview = current.Length == 0 ? "None" : entry?.Label ?? current;

        if (missing) ImGui.PushStyleColor(ImGuiCol.Text, Theme.Error);
        bool open = ImGui.BeginCombo(id, preview, ImGuiComboFlags.HeightLarge);
        if (missing) ImGui.PopStyleColor();
        if (!open)
        {
            Tip(missing ? $"{current} isn't a {Name(kind)} under {Folder(kind)}. Pick one that exists"
                : current.Length == 0 ? $"No {Name(kind)}" : entry!.Label != current ? $"{entry.Label}\n{current}" : current);
            return false;
        }

        if (ImGui.IsWindowAppearing()) { _filter = ""; ImGui.SetKeyboardFocusHere(); }
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##filter", $"Search {entries.Count} in {Folder(kind)}", ref _filter, 128);
        bool changed = false;
        // A kind's "none" is empty text (docs/logic.md), which the game reads as a string like any other value.
        if (ImGui.Selectable("None", current.Length == 0) && current.Length > 0) { picked = EntityValues.Of(""); changed = true; }
        if (missing) ImGui.Selectable($"{current} (missing)##missing", true, ImGuiSelectableFlags.Disabled);

        var shown = _filter.Length == 0 ? entries
            : entries.Where(e => e.Value.Contains(_filter, StringComparison.OrdinalIgnoreCase) || e.Label.Contains(_filter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (shown.Count == 0) ImGui.TextColored(Theme.TextDisabled, entries.Count == 0 ? $"{Folder(kind)} has no {Name(kind)} files" : "No match");
        uint dim = ImGui.ColorConvertFloat4ToU32(Theme.TextDim);
        // Sound folders run to hundreds of files: draw only the rows in view.
        var clipper = new ImGuiListClipper();
        unsafe
        {
            var clip = new ImGuiListClipperPtr(&clipper);
            clip.Begin(shown.Count);
            while (clip.Step())
                for (int i = clip.DisplayStart; i < clip.DisplayEnd; i++)
                {
                    var e = shown[i];
                    bool selected = e == entry;
                    var start = ImGui.GetCursorScreenPos();
                    if (ImGui.Selectable($"{e.Label}##{e.Value}", selected) && !selected) { picked = EntityValues.Of(e.Value); changed = true; }
                    if (selected && ImGui.IsWindowAppearing()) ImGui.SetScrollHereY();
                    if (e.Label != e.Value)
                    {
                        // The stored value, right-aligned and dim, so two entries with the same label stay apart.
                        float w = ImGui.GetItemRectSize().X;
                        string v = Ellipsize(e.Value, w * 0.5f);
                        ImGui.GetWindowDrawList().AddText(new Vector2(start.X + w - ImGui.CalcTextSize(v).X, start.Y), dim, v);
                    }
                }
            clip.End();
        }
        ImGui.EndCombo();
        return changed;
    }

    static string Name(AssetKind kind) => kind.Folders ? $"{kind.Name.Replace('_', ' ')} folder" : kind.Name.Replace('_', ' ');

    static string Folder(AssetKind kind) =>
        (kind.ProjectRoot.Length > 0 ? Path.GetRelativePath(kind.ProjectRoot, kind.Root) : Path.GetFileName(kind.Root)).Replace('\\', '/');
}
