using System.Numerics;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;

namespace Chip.Editor;

/// <summary>
/// Left dock: the registered <see cref="IDockPanel"/> tabs (materials, scene, place, dressing) over the showing tab's
/// content. Collapsed, it is an icon rail: click an icon to open that tab. Ctrl+\ toggles it.
/// </summary>
public sealed partial class EditorApp
{
    float DockRailWidth => Theme.Px(40);
    /// <summary>The tab the dock drew last frame, or null for the rail (the benchmark checks its scenario drew).</summary>
    string? _dockDrawn;
    float DockTabHeight => Theme.Px(34);

    /// <summary><paramref name="squeezed"/>: the dock is open but the window is too narrow for it, so it shows as its rail.</summary>
    void DrawDock(Vector2 pos, Vector2 size, bool squeezed)
    {
        ImGui.SetNextWindowPos(pos);
        ImGui.SetNextWindowSize(size);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Theme.Px(0, 2));
        ImGui.Begin("##dock", FixedPanel | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.PopStyleVar(2);
        _dockDrawn = null;
        if (Ctx.DockOpen && !squeezed && Ctx.DockPanels.FirstOrDefault(p => p.Id == Ctx.DockTab) is { } panel)
        {
            DrawDockTabs(size.X);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.Px(10, 8));
            if (ImGui.BeginChild("##dockBody", Vector2.Zero, ImGuiChildFlags.AlwaysUseWindowPadding))
            {
                ImGui.PushID(panel.Id);
                long started = _bench != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                panel.Draw(Ctx);
                _dockDrawn = panel.Id;
                _bench?.RecordSection("dock." + panel.Id, started);
                ImGui.PopID();
            }
            ImGui.EndChild();
            ImGui.PopStyleVar();
        }
        else DrawDockRail(size, squeezed);
        ImGui.End();
        SyncDockSettings();
    }

    /// <summary>
    /// Icon and title per tab, the showing one underlined, collapse on the right. When the titles don't all fit, only the
    /// showing tab keeps its title (then none, in a very narrow dock); the others' titles are in their tooltips.
    /// </summary>
    void DrawDockTabs(float width)
    {
        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        float h = DockTabHeight, pad = Theme.Px(10), gap = Theme.Px(6);
        float collapseW = h, iconTab = Theme.Px(40);
        dl.AddRectFilled(origin, origin + new Vector2(width, h), U32(Theme.Bg));
        dl.AddLine(origin + new Vector2(0, h - Theme.Px(1)), origin + new Vector2(width, h - Theme.Px(1)), U32(Theme.Border), Theme.Px(1));

        var panels = Ctx.DockPanels;
        float room = width - collapseW;
        float Labelled(IDockPanel p) => pad * 2 + Fonts.IconSize + gap + ImGui.CalcTextSize(p.Title).X;
        bool all = panels.Sum(Labelled) <= room;
        bool LabelOf(IDockPanel p) => all || p.Id == Ctx.DockTab && Labelled(p) + iconTab * (panels.Count - 1) <= room;
        float x = origin.X;
        foreach (var p in panels)
        {
            bool labels = LabelOf(p);
            float w = labels ? Labelled(p) : MathF.Min(iconTab, room / panels.Count);
            var a = new Vector2(x, origin.Y);
            ImGui.SetCursorScreenPos(a);
            if (ImGui.InvisibleButton("##tab" + p.Id, new Vector2(w, h))) Ctx.ToggleDock(p.Id); // the showing tab collapses the dock
            bool hovered = ImGui.IsItemHovered(), on = Ctx.DockTab == p.Id;
            if (hovered && !on) dl.AddRectFilled(a, a + new Vector2(w, h - Theme.Px(1)), U32(Theme.Panel));
            if (on)
            {
                dl.AddRectFilled(a, a + new Vector2(w, h), U32(Theme.Panel));
                dl.AddRectFilled(new Vector2(a.X, a.Y + h - Theme.Px(2)), a + new Vector2(w, h), Theme.AccentU32);
            }
            var col = U32(on || hovered ? Theme.Text : Theme.TextDim);
            float cy = MathF.Round(a.Y + h * 0.5f);
            float iconX = labels ? a.X + pad + Fonts.IconSize * 0.5f : a.X + w * 0.5f;
            Icons.Draw(dl, p.Icon, new Vector2(iconX, cy), on ? Theme.AccentU32 : col);
            if (labels) dl.AddText(new Vector2(a.X + pad + Fonts.IconSize + gap, MathF.Round(cy - ImGui.GetTextLineHeight() * 0.5f)), col, p.Title);
            if (hovered) Widgets.Tip(TabTip(p));
            x += w;
        }

        var c = new Vector2(origin.X + width - collapseW, origin.Y);
        ImGui.SetCursorScreenPos(c);
        if (ImGui.InvisibleButton("##collapseDock", new Vector2(collapseW, h - Theme.Px(1)))) Ctx.DockOpen = false;
        bool ch = ImGui.IsItemHovered();
        Icons.Draw(dl, Icon.SidebarCollapse, c + new Vector2(collapseW, h) * 0.5f, U32(ch ? Theme.Text : Theme.TextDim));
        Widgets.Tip(WithKey("Collapse the side panel", "shell.toggleDock"));
        ImGui.SetCursorScreenPos(origin + new Vector2(0, h));
    }

    /// <summary>
    /// The collapsed dock: expand, then one button per tab. Squeezed (open, but the window is too narrow), the buttons
    /// say so instead of seeming to do nothing.
    /// </summary>
    void DrawDockRail(Vector2 size, bool squeezed)
    {
        float side = MathF.Min(size.X - Theme.Px(8), Theme.Px(32));
        var btn = new Vector2(side);
        float left = MathF.Round((size.X - side) * 0.5f);
        const string TooNarrow = "\nThe window is too narrow for the side panel: widen it, or narrow the inspector";
        ImGui.SetCursorPos(new Vector2(left, Theme.Px(6)));
        if (Widgets.ToolButton(Icon.SidebarExpand, Ctx.Commands.Find("shell.toggleDock")?.HotkeyText ?? "",
                squeezed ? "Show the side panel" + TooNarrow : "Show the side panel", false, btn))
            Ctx.DockOpen = true;
        Widgets.StripGap();
        foreach (var p in Ctx.DockPanels)
        {
            ImGui.SetCursorPosX(left);
            if (Widgets.ToolButton(p.Icon, p.Command is { } id ? Ctx.Commands.Find(id)?.HotkeyText ?? "" : "",
                    squeezed ? p.Title + TooNarrow : p.Title, squeezed && Ctx.DockTab == p.Id, btn))
                Ctx.ShowDock(p.Id);
        }
    }

    string TabTip(IDockPanel p) => p.Command is { } id ? WithKey(p.Title, id) : p.Title;

    string WithKey(string text, string command) => Ctx.Commands.Find(command)?.HotkeyText is { } key ? $"{text}  ({key})" : text;

    /// <summary>Saves the dock's tab and open state when they change (hotkeys and tabs both change them).</summary>
    void SyncDockSettings()
    {
        var s = _settings.Current;
        if (s.DockTab == Ctx.DockTab && s.DockOpen == Ctx.DockOpen) return;
        s.DockTab = Ctx.DockTab;
        s.DockOpen = Ctx.DockOpen;
        _settings.Save();
    }

    static uint U32(Vector4 c) => ImGui.ColorConvertFloat4ToU32(c);
}
