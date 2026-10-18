using Chip.Editor.UI;
using ImGuiNET;

namespace Chip.Editor;

/// <summary>Menu bar generated from the command registry, plus document state on the right.</summary>
public sealed partial class EditorApp
{
    void DrawMenuBar()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Theme.Px(7, 5));
        if (!ImGui.BeginMainMenuBar()) { ImGui.PopStyleVar(); return; }
        ImGui.PushFont(Fonts.Strong);
        ImGui.Text("Chip");
        ImGui.PopFont();
        ImGui.SameLine(0, Theme.Px(10));

        var commands = Ctx.Commands;
        foreach (var menu in commands.Menus())
        {
            if (!ImGui.BeginMenu(menu)) continue;
            bool firstGroup = true;
            foreach (var group in commands.MenuGroups(menu))
            {
                if (!firstGroup) ImGui.Separator();
                firstGroup = false;
                foreach (var c in group)
                {
                    if (ImGui.MenuItem(c.CurrentLabel, c.HotkeyText, c.IsChecked?.Invoke() ?? false, c.Enabled)) c.Execute();
                    if (c.Description != null && ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip(c.Description);
                }
                if (menu == "File" && group.Key == "1-doc") DrawRecentFilesMenu();
            }
            ImGui.EndMenu();
        }

        // Document state, right-aligned: name, then whether there is anything to save.
        var session = Ctx.Session;
        string name = session.Path != null ? Path.GetFileName(session.Path) : "Untitled";
        string state = session.Dirty ? "unsaved" : "saved";
        float w = ImGui.CalcTextSize(name).X + Theme.Px(10) + ImGui.CalcTextSize(state).X;
        ImGui.SameLine(ImGui.GetWindowWidth() - w - Theme.Px(12));
        ImGui.Text(name);
        ImGui.SameLine(0, Theme.Px(10));
        ImGui.TextColored(session.Dirty ? Theme.Text : Theme.TextDim, state);
        ImGui.EndMainMenuBar();
        ImGui.PopStyleVar();
    }

    /// <summary>File > Open recent: file names, with the folder dimmed on the right (full path in the tooltip).</summary>
    void DrawRecentFilesMenu()
    {
        var files = _shell.RecentFiles;
        if (!ImGui.BeginMenu("Open recent", files.Count > 0)) return;
        string? open = null;
        for (int i = 0; i < files.Count; i++)
        {
            var path = files[i];
            if (ImGui.MenuItem($"{Path.GetFileName(path)}##{i}", ShortFolder(Path.GetDirectoryName(path) ?? ""))) open = path;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip(path);
        }
        ImGui.Separator();
        if (ImGui.MenuItem("Clear recent files")) Ctx.Commands.Execute("file.clearRecent");
        ImGui.EndMenu();
        if (open != null) _shell.OpenRecent(open);
    }

    /// <summary>"…\levels\act1" for long folders: the end of a path is the part that tells files apart.</summary>
    static string ShortFolder(string folder)
    {
        const int max = 36;
        return folder.Length <= max ? folder : "…" + folder[^(max - 1)..];
    }
}
