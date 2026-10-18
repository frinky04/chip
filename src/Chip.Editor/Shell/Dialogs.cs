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
            ImGui.PushStyleColor(ImGuiCol.Button, Theme.ActiveFill);
            bool save = ImGui.Button("Save", w);
            ImGui.PopStyleColor();
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

    static readonly string[] SheetOrder = ["Camera", "Select", "Tools", "Edit", "Mesh", "Texture", "View", "File", "Help"];

    /// <summary>Every hotkeyed command with a menu or category, plus registered references (mouse gestures), grouped.</summary>
    List<(string Category, List<(string Keys, string Action)> Rows)> ShortcutGroups()
    {
        var rows = new List<(string Category, string Keys, string Action)>();
        foreach (var c in Ctx.Commands.All)
        {
            string? category = c.Category ?? c.Menu;
            if (category == null || c.HotkeyText == null) continue;
            rows.Add((category, string.Join(" / ", c.Hotkeys.Select(h => h.ToString())), c.Label));
        }
        rows.AddRange(Ctx.Commands.References.Select(r => (r.Category, r.Keys, r.Action)));
        return rows.GroupBy(r => r.Category)
            .OrderBy(g => Array.IndexOf(SheetOrder, g.Key) is var i && i >= 0 ? i : SheetOrder.Length).ThenBy(g => g.Key)
            .Select(g => (g.Key, g.Select(r => (r.Keys, r.Action)).ToList()))
            .ToList();
    }

    void DrawShortcutsSheet()
    {
        if (Ctx.ShowShortcuts) { ImGui.OpenPopup("Keyboard shortcuts"); Ctx.ShowShortcuts = false; }
        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(vp.GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f));
        ImGui.SetNextWindowSize(new Vector2(MathF.Min(Theme.Px(1180), vp.WorkSize.X - Theme.Px(80)), 0));
        ImGui.SetNextWindowSizeConstraints(Vector2.Zero, new Vector2(float.MaxValue, vp.WorkSize.Y - Theme.Px(80)));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.Px(20, 16));
        bool open = true;
        if (ImGui.BeginPopupModal("Keyboard shortcuts", ref open, ImGuiWindowFlags.NoMove))
        {
            var groups = ShortcutGroups();
            if (ImGui.BeginTable("##groups", 3, ImGuiTableFlags.SizingStretchSame))
            {
                for (int g = 0; g < groups.Count; g++)
                {
                    if (g % 3 == 0) ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    var (category, rows) = groups[g];
                    ImGui.PushFont(Fonts.Strong);
                    ImGui.Text(category);
                    ImGui.PopFont();
                    ImGui.Dummy(Theme.Px(0, 2));
                    float keyW = rows.Max(r => ImGui.CalcTextSize(r.Keys).X) + Theme.Px(16); // per group, so actions never clip
                    foreach (var (keys, action) in rows)
                    {
                        float x0 = ImGui.GetCursorPosX();
                        ImGui.Text(keys);
                        ImGui.SameLine();
                        ImGui.SetCursorPosX(x0 + keyW);
                        ImGui.TextColored(Theme.TextDim, action);
                    }
                    ImGui.Dummy(Theme.Px(0, 12));
                }
                ImGui.EndTable();
            }
            if (ImGui.IsKeyPressed(ImGuiKey.Escape) || ImGui.IsKeyPressed(ImGuiKey.F1, false)) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        ImGui.PopStyleVar();
    }
}
