using System.Numerics;
using Chip.Editor.UI;
using ImGuiNET;

namespace Chip.Editor;

/// <summary>Menu bar generated from the command registry; on the right command search, document state and Export.</summary>
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

        // Right-aligned: command search (the palette), the document's name and state, then Export with its options.
        var session = Ctx.Session;
        var style = ImGui.GetStyle();
        string name = session.Path != null ? Path.GetFileName(session.Path) : "Untitled";
        string state = session.Dirty ? "unsaved" : "saved";
        string searchKey = Ctx.Commands.Find("shell.palette")?.HotkeyText ?? "";
        float searchW = Theme.Px(220);
        float exportW = ImGui.CalcTextSize("Export").X + style.FramePadding.X * 2, moreW = ImGui.GetFrameHeight();
        // Short of room, give up in order: the search field (Ctrl+P still works), then the end of the file name, then the
        // saved/unsaved word (the title bar still has a *).
        float left = ImGui.GetCursorPosX() + Theme.Px(24);
        float room = ImGui.GetWindowWidth() - Theme.Px(10) - left - (Theme.Px(14) + exportW + Theme.Px(1) + moreW);
        float stateW = Theme.Px(8) + ImGui.CalcTextSize(state).X;
        if (searchW + Theme.Px(16) + ImGui.CalcTextSize(name).X + stateW > room) searchW = 0;
        float searchRoom = searchW > 0 ? searchW + Theme.Px(16) : 0;
        if (ImGui.CalcTextSize(name).X + stateW + searchRoom > room && room - stateW < Theme.Px(80)) state = "";
        if (state.Length == 0) stateW = 0;
        name = Features.Materials.MaterialTiles.Fit(name, MathF.Max(room - stateW - searchRoom, ImGui.CalcTextSize("…").X));
        float docW = ImGui.CalcTextSize(name).X + stateW;
        ImGui.SameLine(ImGui.GetWindowWidth() - Theme.Px(10) - (Theme.Px(14) + exportW + Theme.Px(1) + moreW) - docW - searchRoom);
        if (searchW > 0) SearchField(searchW, searchKey);
        ImGui.Text(name);
        if (session.Path != null && ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip(session.Path);
        if (state.Length > 0)
        {
            ImGui.SameLine(0, Theme.Px(8));
            ImGui.TextColored(session.Dirty ? Theme.Text : Theme.TextDim, state);
        }
        ImGui.SameLine(0, Theme.Px(14));
        DrawExportButton(exportW, moreW);
        ImGui.EndMainMenuBar();
        ImGui.PopStyleVar();
    }

    /// <summary>Looks like a search field; clicking it opens the command palette, where the typing happens.</summary>
    void SearchField(float width, string key)
    {
        var p = ImGui.GetCursorScreenPos();
        float h = ImGui.GetFrameHeight();
        if (ImGui.InvisibleButton("##commandSearch", new Vector2(width, h))) Ctx.Commands.Execute("shell.palette");
        bool hovered = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(p, p + new Vector2(width, h), ImGui.ColorConvertFloat4ToU32(hovered ? Theme.FrameHover : Theme.Frame), Theme.Px(3));
        Icons.Draw(dl, Icon.Search, p + new Vector2(Theme.Px(6) + Fonts.IconSize * 0.5f, h * 0.5f), ImGui.ColorConvertFloat4ToU32(Theme.TextDim));
        float ty = MathF.Round(p.Y + (h - ImGui.GetTextLineHeight()) * 0.5f);
        dl.AddText(new Vector2(p.X + Theme.Px(12) + Fonts.IconSize, ty), ImGui.ColorConvertFloat4ToU32(Theme.TextDim), "Search commands");
        float kw = ImGui.CalcTextSize(key).X;
        dl.AddText(new Vector2(p.X + width - kw - Theme.Px(8), ty), ImGui.ColorConvertFloat4ToU32(Theme.TextDisabled), key);
        Widgets.Tip("Find any command by name and run it");
        ImGui.SameLine(0, Theme.Px(16));
    }

    /// <summary>Export (Ctrl+E), and beside it a menu of the other ways out: export as, OBJ, export on save, settings.</summary>
    void DrawExportButton(float exportW, float moreW)
    {
        var doc = Ctx.Doc;
        Widgets.PushPrimary();
        if (ImGui.Button("Export", new Vector2(exportW, 0))) Ctx.Commands.Execute("file.export");
        Widgets.Tip(doc.ExportPath != null ? $"Write {doc.ExportPath}  (Ctrl+E)" : "Choose where to write the .glb  (Ctrl+E)");
        ImGui.SameLine(0, Theme.Px(1));
        if (ImGui.Button("##exportMore", new Vector2(moreW, 0))) ImGui.OpenPopup("##exportMenu");
        var (a, b) = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
        Widgets.PopPrimary();
        Chevron(ImGui.GetWindowDrawList(), (a + b) * 0.5f, Theme.OnAccentU32);
        Widgets.Tip("Export options");
        ImGui.SetNextWindowPos(new Vector2(b.X, b.Y + Theme.Px(2)), ImGuiCond.Appearing, new Vector2(1, 0));
        if (!ImGui.BeginPopup("##exportMenu")) return;
        if (doc.ExportPath != null) ImGui.TextColored(Theme.TextDim, Path.GetFileName(doc.ExportPath));
        else ImGui.TextColored(Theme.TextDim, "No export file yet");
        ImGui.Separator();
        foreach (var id in new[] { "file.export", "file.exportAs", "pipeline.exportObj", "pipeline.exportOnSave", "pipeline.exportSettings" })
            if (Ctx.Commands.Find(id) is { } c && ImGui.MenuItem(c.CurrentLabel, c.HotkeyText, c.IsChecked?.Invoke() ?? false, c.Enabled)) c.Execute();
        ImGui.EndPopup();
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
