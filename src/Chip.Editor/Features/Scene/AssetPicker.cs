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

    /// <summary>
    /// Draws the field at the width already set. True with the value to store (empty text for none) when the pick changed.
    /// A <paramref name="mixed"/> field stands for several entities' differing values: it reads "mixed" and any pick counts.
    /// </summary>
    public static bool Draw(string id, AssetKind kind, JsonElement value, out JsonElement picked, bool mixed = false)
    {
        picked = value;
        string current = mixed || EntityValues.IsNull(value) ? "" : EntityValues.Text(value);
        var entries = kind.List();
        var entry = current.Length > 0 ? entries.FirstOrDefault(e => e.Value.Equals(current, StringComparison.OrdinalIgnoreCase)) : null;
        bool missing = current.Length > 0 && entry == null;
        string preview = mixed ? "mixed" : current.Length == 0 ? "None" : entry != null ? Display(kind, entry) : current;

        if (missing) ImGui.PushStyleColor(ImGuiCol.Text, Theme.Error);
        bool open = ImGui.BeginCombo(id, preview, ImGuiComboFlags.HeightLarge);
        if (missing) ImGui.PopStyleColor();
        if (!open)
        {
            Tip(mixed ? $"The selected entities use different {Name(kind)} files. A pick sets all of them"
                : missing ? $"{current} isn't a {Name(kind)} under {Folder(kind)}. Pick one that exists"
                : current.Length == 0 ? $"No {Name(kind)}" : Display(kind, entry!) != current ? $"{Display(kind, entry!)}\n{current}" : current);
            return false;
        }

        if (ImGui.IsWindowAppearing()) { _filter = ""; ImGui.SetKeyboardFocusHere(); }
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##filter", $"Search {entries.Count} in {Folder(kind)}", ref _filter, 128);
        bool changed = false;
        // A kind's "none" is empty text (docs/logic.md), which the game reads as a string like any other value.
        if (ImGui.Selectable("None", !mixed && current.Length == 0) && (mixed || current.Length > 0)) { picked = EntityValues.Of(""); changed = true; }
        if (missing) ImGui.Selectable($"{current} (missing)##missing", true, ImGuiSelectableFlags.Disabled);

        var shown = _filter.Length == 0 ? entries
            : entries.Where(e => e.Value.Contains(_filter, StringComparison.OrdinalIgnoreCase) || Display(kind, e).Contains(_filter, StringComparison.OrdinalIgnoreCase)).ToList();
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
                    if (ImGui.Selectable($"{Display(kind, e)}##{e.Value}", selected) && !selected) { picked = EntityValues.Of(e.Value); changed = true; }
                    if (selected && ImGui.IsWindowAppearing()) ImGui.SetScrollHereY();
                    if (e.Label != e.Value)
                    {
                        // A label from the file: the stored value right-aligned and dim, so two entries with the same label stay apart.
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

    /// <summary>
    /// What a row and the closed field show: the file's label, else where it sits under the kind's folder without its
    /// extension (<c>door/main_door</c> for <c>assets/models/door/main_door.glb</c>), which stays readable in a narrow field.
    /// </summary>
    static string Display(AssetKind kind, AssetEntry e)
    {
        if (e.Label != e.Value) return e.Label;
        var relative = Path.GetRelativePath(kind.Root, e.FullPath).Replace('\\', '/');
        return kind.Folders ? relative : Path.ChangeExtension(relative, null);
    }

    static string Name(AssetKind kind) => kind.Folders ? $"{kind.Name.Replace('_', ' ')} folder" : kind.Name.Replace('_', ' ');

    static string Folder(AssetKind kind) =>
        (kind.ProjectRoot.Length > 0 ? Path.GetRelativePath(kind.ProjectRoot, kind.Root) : Path.GetFileName(kind.Root)).Replace('\\', '/');
}
