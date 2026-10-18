using System.Numerics;
using Chip.Editor.Settings;
using Chip.Editor.UI;
using ImGuiNET;

namespace Chip.Editor;

/// <summary>Startup crash recovery: offers each autosave that holds unsaved work, newest first.</summary>
public sealed partial class EditorApp
{
    const string RecoveryTitle = "Recover unsaved work";
    List<AutosaveInfo> _recovery = new();

    void DrawRecoveryModal()
    {
        if (_recovery.Count == 0) return;
        if (!ImGui.IsPopupOpen(RecoveryTitle) && !ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId)) ImGui.OpenPopup(RecoveryTitle);
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Always, new Vector2(0.5f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.Px(20, 16));
        if (ImGui.BeginPopupModal(RecoveryTitle, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove))
        {
            var info = _recovery[0];
            ImGui.Text(Esc($"Chip closed before {info.DisplayName} was saved."));
            var local = info.SavedUtc.ToLocalTime();
            string when = local.Date == DateTime.Today ? $"today at {local:HH:mm}" : $"{local:d MMM} at {local:HH:mm}";
            ImGui.TextColored(Theme.TextDim, $"An autosave from {when} has changes that aren't in the file.");
            if (info.OriginalPath != null) ImGui.TextColored(Theme.TextDim, Esc(info.OriginalPath));
            if (_recovery.Count > 1) ImGui.TextColored(Theme.TextDim, $"{_recovery.Count - 1} more autosave{(_recovery.Count > 2 ? "s" : "")} after this one.");
            ImGui.Dummy(Theme.Px(0, 8));
            var w = new Vector2(Theme.Px(150), 0);
            Widgets.PushPrimary();
            bool restore = ImGui.Button("Restore", w);
            Widgets.PopPrimary();
            Widgets.Tip("Open the autosave. Undo shows the file as it was last saved.");
            ImGui.SameLine();
            bool discard = ImGui.Button("Discard autosave", w);
            Widgets.Tip("Delete the autosave for good");
            if (restore) Restore(info);
            else if (discard)
            {
                _autosave.Delete(info.Key);
                _recovery.RemoveAt(0);
                if (_recovery.Count == 0) ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
        ImGui.PopStyleVar();
    }

    void Restore(AutosaveInfo info)
    {
        ImGui.CloseCurrentPopup();
        _recovery.Clear(); // one document at a time; any others are offered next start
        try
        {
            Ctx.CancelInteraction();
            _autosave.Restore(info, Ctx.Session);
            Ctx.FrameAll();
            Ctx.Status($"Restored {info.DisplayName} from its autosave. Save to keep it.");
        }
        catch (Exception e)
        {
            Ctx.Status($"Couldn't restore the autosave: {e.Message} It is still in {_autosave.Folder}.", error: true);
        }
    }
}
