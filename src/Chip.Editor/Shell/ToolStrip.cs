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
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Theme.Bg); // chrome, like Krita's toolbox
        ImGui.Begin("##tools", FixedPanel);
        // Square tiles, centred in the strip whatever its width.
        float side = MathF.Min(size.X - Theme.Px(8), Theme.Px(36));
        var btn = new Vector2(side, side);
        float left = MathF.Round((size.X - side) * 0.5f);

        foreach (var tool in Ctx.Tools)
        {
            // The command's current binding (tooltip only), so a remapped tool shows its new key.
            string hotkey = Ctx.Commands.Find($"tool.{tool.Id}")?.HotkeyText ?? "";
            ImGui.SetCursorPosX(left);
            if (ToolButton(tool.Icon, hotkey, tool.Name, Ctx.ActiveTool == tool, btn)) Ctx.ToggleTool(tool);
        }
        StripGap();
        foreach (var (id, icon) in TransformButtons)
        {
            if (Ctx.Commands.Find(id) is not { } c) continue;
            string tip = c.Description != null ? $"{c.Label}\n{c.Description}" : c.Label;
            ImGui.SetCursorPosX(left);
            if (ToolButton(icon, c.HotkeyText ?? "", tip, c.IsChecked?.Invoke() ?? false, btn)) c.Execute();
        }
        ImGui.End();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(2);
    }
}
