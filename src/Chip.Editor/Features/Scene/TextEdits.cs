using ImGuiNET;

namespace Chip.Editor.Features.Scene;

/// <summary>
/// Text fields that commit once: the field edits a private buffer and reports the new text when it loses focus after an
/// edit (Enter or click away), so each edit becomes one undo step. Esc reverts. One instance serves many fields.
/// </summary>
public sealed class TextEdits
{
    string? _activeId;
    string _buffer = "";

    /// <summary>Draws the field; true (with the new text) when an edit was committed and differs from <paramref name="current"/>.</summary>
    public bool Field(string id, string current, out string committed, string? hint = null, float width = -1)
    {
        bool mine = _activeId == id;
        string buf = mine ? _buffer : current;
        ImGui.SetNextItemWidth(width);
        if (hint != null) ImGui.InputTextWithHint(id, hint, ref buf, 256);
        else ImGui.InputText(id, ref buf, 256);
        if (ImGui.IsItemActive()) { _activeId = id; _buffer = buf; }
        else if (mine) _activeId = null;
        committed = buf;
        return ImGui.IsItemDeactivatedAfterEdit() && buf != current;
    }
}
