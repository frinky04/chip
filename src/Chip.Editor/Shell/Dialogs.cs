using System.Numerics;
using Chip.Editor.Features.Base;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;

namespace Chip.Editor;

/// <summary>Unsaved-changes confirmation and the generated keyboard-shortcuts sheet.</summary>
public sealed partial class EditorApp
{
    Action? _afterConfirm;
    bool _openConfirm, _allowClose;

    void ConfirmDiscard(Action then)
    {
        if (!Ctx.Session.Dirty) { then(); return; }
        _afterConfirm = then;
        _openConfirm = true;
    }

    void OnClosing()
    {
        if (_allowClose || !Ctx.Session.Dirty) return;
        _window.IsClosing = false;
        ConfirmDiscard(() => { _allowClose = true; _window.Close(); });
    }

    void DrawConfirmModal()
    {
        if (_openConfirm) { ImGui.OpenPopup("Unsaved changes"); _openConfirm = false; }
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.Px(20, 16));
        if (ImGui.BeginPopupModal("Unsaved changes", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove))
        {
            string name = Ctx.Session.Path != null ? Path.GetFileName(Ctx.Session.Path) : "this level";
            ImGui.Text($"Save changes to {name} first?");
            ImGui.TextColored(Theme.TextDim, "Unsaved changes are lost if you don't save.");
            ImGui.Dummy(Theme.Px(0, 8));
            var w = new Vector2(Theme.Px(104), 0);
            Widgets.PushPrimary();
            bool save = ImGui.Button("Save", w);
            Widgets.PopPrimary();
            ImGui.SameLine();
            bool discard = ImGui.Button("Don't save", w);
            ImGui.SameLine();
            bool cancel = ImGui.Button("Cancel", w) || ImGui.IsKeyPressed(ImGuiKey.Escape);
            if (save || discard || cancel)
            {
                ImGui.CloseCurrentPopup();
                var then = _afterConfirm;
                _afterConfirm = null;
                if (discard || (save && FileFeature.Save(Ctx, saveAs: false))) then?.Invoke();
            }
            ImGui.EndPopup();
        }
        ImGui.PopStyleVar();
    }
}
