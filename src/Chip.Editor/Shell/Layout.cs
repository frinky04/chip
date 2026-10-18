using System.Numerics;
using Chip.Editor.Settings;
using Chip.Editor.UI;
using ImGuiNET;

namespace Chip.Editor;

/// <summary>
/// Fixed layout (menu, tool strip, viewport, inspector, status) with draggable splitters for the tool strip and
/// inspector widths, persisted in settings as logical pixels. Double-click a splitter to restore its default.
/// </summary>
/// <remarks>
/// Docking was evaluated and left out: ImGui.NET 1.90 exposes DockSpaceOverViewport but not DockBuilder, so a default
/// layout would rely on a hand-written imgui.ini keyed to window names and sizes. Every panel here is a registry host
/// (sections, tools, commands) rather than a free window, so splitters give the useful part (sizing) while keeping
/// captures deterministic and features independent of where panels sit.
/// </remarks>
public sealed partial class EditorApp
{
    /// <summary>Tool strip and inspector widths in physical pixels, clamped so the viewport keeps at least 240 px.</summary>
    (float Tool, float Inspector) PanelWidths(float total)
    {
        var s = _settings.Current;
        float tool = Theme.Px(AppSettings.Width(s.ToolStripWidth, AppSettings.DefaultToolStripWidth, 40, 96));
        float inspector = Theme.Px(AppSettings.Width(s.InspectorWidth, AppSettings.DefaultInspectorWidth, 240, 720));
        inspector = MathF.Max(MathF.Min(inspector, total - tool - Theme.Px(240)), Theme.Px(160));
        return (tool, inspector);
    }

    void DrawSplitters(Vector2 pos, Vector2 size, float height, float toolW, float inspectorW)
    {
        float scale = Theme.Scale;
        if (Splitter("##splitTools", pos.X + toolW, pos.Y, height, out float mouseX, out bool reset))
            _settings.Current.ToolStripWidth = Math.Clamp((mouseX - pos.X) / scale, 40, 96);
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
        _settings.Save();
        Ctx.Status("Panel sizes reset");
    }
}
