using Chip.Core.Scene;
using Chip.Editor.UI;
using ImGuiNET;
using NativeFileDialogSharp;

namespace Chip.Editor.Features.References;

/// <summary>The model chooser shared by the entity tool and the reference inspector: built-ins, then a file.</summary>
public static class ReferencePicker
{
    /// <summary>A combo of the built-in models plus "Choose file…". True when the user picked a different model.</summary>
    public static bool Draw(string id, string current, out string chosen)
    {
        chosen = current;
        bool changed = false;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (ImGui.BeginCombo(id, ReferenceModels.DisplayName(current)))
        {
            foreach (var name in ReferenceModels.BuiltIns())
                if (ImGui.Selectable(name, string.Equals(name, current, StringComparison.OrdinalIgnoreCase)) && name != current)
                {
                    chosen = name;
                    changed = true;
                }
            if (!ReferenceModels.IsBuiltInName(current) && current.Length > 0) ImGui.Selectable(current, true);
            ImGui.Separator();
            if (ImGui.Selectable("Choose file…") && ChooseFile() is { } file && file != current)
            {
                chosen = file;
                changed = true;
            }
            ImGui.EndCombo();
        }
        Widgets.Tip(ReferenceModels.IsBuiltInName(current) ? "Built-in model" : current);
        return changed;
    }

    /// <summary>Asks for a .glb/.gltf; returns the value to store (relative to a saved level), or null when cancelled.</summary>
    public static string? ChooseFile()
    {
        var result = Dialog.FileOpen("glb,gltf");
        return result.IsOk ? ReferenceModels.StoredPath(result.Path) : null;
    }

    /// <summary>One dim line under the picker: the model's size, or in red why it can't load.</summary>
    public static void Summary(string model)
    {
        var (loaded, error) = ReferenceModels.Get(model);
        ImGui.PushStyleColor(ImGuiCol.Text, loaded == null ? Theme.Error : Theme.TextDim);
        ImGui.TextWrapped(loaded == null ? error ?? "Can't load the model" : SizeText(loaded));
        ImGui.PopStyleColor();
    }

    /// <summary>"1.4 wide, 2.171 tall, 0.125 deep (m)".</summary>
    public static string SizeText(ReferenceModel model)
    {
        var s = model.Size;
        return $"{s.X:0.###} wide, {s.Y:0.###} tall, {s.Z:0.###} deep (m)";
    }
}
