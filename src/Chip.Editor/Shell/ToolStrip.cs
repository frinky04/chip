using System.Numerics;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor;

/// <summary>Left strip: registered tools, then the gizmo modes of the default tool.</summary>
public sealed partial class EditorApp
{
    static readonly (string Command, Icon Icon)[] TransformButtons =
        [("tool.gizmoTranslate", Icon.Move), ("tool.gizmoRotate", Icon.Rotate), ("tool.gizmoScale", Icon.Scale)];

    void DrawToolStrip(Vector2 pos, Vector2 size)
    {
        ImGui.SetNextWindowPos(pos);
        ImGui.SetNextWindowSize(size);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.Px(4, 6));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Theme.Px(0, 2));
        ImGui.Begin("##tools", FixedPanel);
        var btn = new Vector2(size.X - Theme.Px(8), Theme.Px(36));

        foreach (var tool in Ctx.Tools)
        {
            string key = tool.Hotkey is { Length: 1 } k ? k : "";
            string tip = tool.Hotkey != null ? $"{tool.Name}  ({tool.Hotkey})" : tool.Name;
            if (ToolButton(tool.Icon, key, tip, Ctx.ActiveTool == tool, btn)) Ctx.ToggleTool(tool);
        }
        StripGap();
        foreach (var (id, icon) in TransformButtons)
        {
            if (Ctx.Commands.Find(id) is not { } c) continue;
            string tip = c.Description != null ? $"{c.Label}\n{c.Description}" : c.Label;
            if (ToolButton(icon, c.HotkeyText ?? "", tip, c.IsChecked?.Invoke() ?? false, btn)) c.Execute();
        }
        ImGui.End();
        ImGui.PopStyleVar(2);
    }
}
