using System.Numerics;
using Chip.Editor.Settings;
using Chip.Editor.UI;
using ImGuiNET;

namespace Chip.Editor;

/// <summary>
/// Fixed layout: menu, then left to right the dock (library tabs, or its icon rail when collapsed), the tool strip, the
/// viewport and the inspector, then the status bar. Draggable splitters size the dock, tool strip and inspector,
/// persisted in settings as logical pixels. Double-click a splitter to restore its default.
/// </summary>
/// <remarks>
/// Docking was evaluated and left out: ImGui.NET 1.90 exposes DockSpaceOverViewport but not DockBuilder, so a default
/// layout would rely on a hand-written imgui.ini keyed to window names and sizes. Every panel here is a registry host
/// (dock tabs, sections, tools, commands) rather than a free window, so splitters give the useful part (sizing) while
/// keeping captures deterministic and features independent of where panels sit.
/// </remarks>
public sealed partial class EditorApp
{
    /// <summary>Logical width the viewport keeps whatever the panels want: room for its header at its most compact.</summary>
    const float MinViewportWidth = 400;

    /// <summary>
    /// Dock, tool strip and inspector widths in physical pixels. The viewport keeps <see cref="MinViewportWidth"/>. When the window is
    /// narrower than the panels want, the dock and inspector shrink towards their minimum (240 px each) in proportion to
    /// how far each is above it; when even that doesn't fit, the dock shows as its rail (<paramref name="squeezed"/>)
    /// without forgetting that it's open, and only then does the inspector go below its minimum.
    /// </summary>
    (float Dock, float Tool, float Inspector) PanelWidths(float total, out bool squeezed)
    {
        var s = _settings.Current;
        float tool = Theme.Px(AppSettings.Width(s.ToolStripWidth, AppSettings.DefaultToolStripWidth, 40, 96));
        float dock = Theme.Px(AppSettings.Width(s.DockWidth, AppSettings.DefaultDockWidth, AppSettings.MinDockWidth, AppSettings.MaxDockWidth));
        float inspector = Theme.Px(AppSettings.Width(s.InspectorWidth, AppSettings.DefaultInspectorWidth, 240, 720));
        float minViewport = Theme.Px(MinViewportWidth), minDock = Theme.Px(AppSettings.MinDockWidth), minInspector = Theme.Px(240);
        float room = total - tool - minViewport; // for the dock and inspector together
        squeezed = Ctx.DockOpen && room < minDock + minInspector;
        if (Ctx.DockOpen && !squeezed)
        {
            float over = dock + inspector - room;
            if (over > 0)
            {
                float dockSlack = dock - minDock, inspectorSlack = inspector - minInspector;
                float k = over / MathF.Max(dockSlack + inspectorSlack, 1);
                dock -= dockSlack * k;
                inspector -= inspectorSlack * k;
            }
            return (MathF.Round(dock), tool, MathF.Round(inspector));
        }
        dock = DockRailWidth;
        inspector = MathF.Max(MathF.Min(inspector, room - dock), MathF.Min(Theme.Px(160), MathF.Max(total - tool - dock - Theme.Px(80), 0)));
        return (dock, tool, inspector);
    }

    void DrawSplitters(Vector2 pos, Vector2 size, float height, float dockW, float toolW, float inspectorW, bool dockSqueezed)
    {
        float scale = Theme.Scale;
        if (Ctx.DockOpen && !dockSqueezed)
        {
            if (Splitter("##splitDock", pos.X + dockW, pos.Y, height, out float dockX, out bool dockReset))
                _settings.Current.DockWidth = Math.Clamp((dockX - pos.X) / scale, AppSettings.MinDockWidth, AppSettings.MaxDockWidth);
            if (dockReset) { _settings.Current.DockWidth = AppSettings.DefaultDockWidth; _settings.Save(); }
        }

        float toolX = pos.X + dockW;
        if (Splitter("##splitTools", toolX + toolW, pos.Y, height, out float mouseX, out bool reset))
            _settings.Current.ToolStripWidth = Math.Clamp((mouseX - toolX) / scale, 40, 96);
        if (reset) { _settings.Current.ToolStripWidth = AppSettings.DefaultToolStripWidth; _settings.Save(); }

        if (Splitter("##splitInspector", pos.X + size.X - inspectorW, pos.Y, height, out mouseX, out reset))
            _settings.Current.InspectorWidth = Math.Clamp((pos.X + size.X - mouseX) / scale, 240, 720);
        if (reset) { _settings.Current.InspectorWidth = AppSettings.DefaultInspectorWidth; _settings.Save(); }
    }

    /// <summary>
    /// A thin always-on-top strip centred on a panel edge. Returns true while dragging (with the mouse x);
    /// saves settings when the drag ends; <paramref name="reset"/> is set on double-click.
    /// </summary>
    bool Splitter(string id, float x, float y, float height, out float mouseX, out bool reset)
    {
        float w = Theme.Px(6);
        ImGui.SetNextWindowPos(new Vector2(MathF.Round(x - w * 0.5f), y));
        ImGui.SetNextWindowSize(new Vector2(w, height));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.Begin(id, ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav);
        ImGui.InvisibleButton("##drag", new Vector2(w, height));
        bool active = ImGui.IsItemActive();
        bool hovered = ImGui.IsItemHovered();
        reset = hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
        if (ImGui.IsItemDeactivated()) _settings.Save();
        if (active || hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
            var p = ImGui.GetItemRectMin();
            float cx = MathF.Round(p.X + w * 0.5f);
            // Dragging is the thing being edited, so it may be orange; hover stays neutral.
            uint col = active ? Theme.AccentU32 : ImGui.ColorConvertFloat4ToU32(Theme.TextDisabled);
            ImGui.GetWindowDrawList().AddLine(new Vector2(cx, p.Y), new Vector2(cx, p.Y + height), col, Theme.Px(2));
        }
        ImGui.End();
        ImGui.PopStyleVar(3);
        mouseX = ImGui.GetIO().MousePos.X;
        return active && !reset;
    }

    void ResetLayout()
    {
        _settings.Current.InspectorWidth = AppSettings.DefaultInspectorWidth;
        _settings.Current.ToolStripWidth = AppSettings.DefaultToolStripWidth;
        _settings.Current.DockWidth = AppSettings.DefaultDockWidth;
        _settings.Save();
        Ctx.Status("Panel sizes reset");
    }
}
