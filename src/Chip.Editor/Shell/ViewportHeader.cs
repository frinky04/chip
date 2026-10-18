using System.Numerics;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor;

/// <summary>Viewport header: how you look and what you select (mode, view, grid, snap, backfaces).</summary>
public sealed partial class EditorApp
{
    static float HeaderHeight => Theme.Px(38);

    static readonly SelectMode[] HeaderModes = [SelectMode.Vertex, SelectMode.Edge, SelectMode.Face, SelectMode.Object];

    static readonly (Icon, string, string)[] ModeItems =
    [
        (Icon.Vertex, "Vertex", "Select vertices  (1)"), (Icon.Edge, "Edge", "Select edges  (2)"),
        (Icon.Face, "Face", "Select faces  (3)"), (Icon.Object, "Object", "Select objects  (4)"),
    ];

    void DrawViewportHeader(float width)
    {
        float h = HeaderHeight;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.Panel);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Theme.Px(8), MathF.Floor((h - ImGui.GetFrameHeight()) * 0.5f)));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Theme.Px(2, 0));
        ImGui.BeginChild("##vpheader", new Vector2(width, h), ImGuiChildFlags.AlwaysUseWindowPadding, ImGuiWindowFlags.NoScrollbar);

        int picked = SegmentedControl("mode", ModeItems, Array.IndexOf(HeaderModes, Ctx.Sel.Mode));
        if (picked >= 0) Ctx.SetMode(HeaderModes[picked]);
        VSeparator();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextDim, "Perspective");

        // Right group: grid, snap, backfaces. Measured so it right-aligns exactly.
        var style = ImGui.GetStyle();
        string gridText = Labels.Length(Ctx.Grid);
        float slot = ImGui.CalcTextSize("3.13 cm").X;
        float Btn(string t) => ImGui.CalcTextSize(t).X + style.FramePadding.X * 2;
        float ToggleW(string label) => Theme.Px(8) * 2 + Fonts.IconSize + Theme.Px(6) + ImGui.CalcTextSize(label).X;
        float gap = Theme.Px(6);
        float rightW = ImGui.CalcTextSize("Grid").X + gap + Btn("-") + gap + slot + gap + Btn("+") + Theme.Px(12)
            + ToggleW("Snap") + Theme.Px(2) + ToggleW("Backfaces");
        ImGui.SameLine(width - style.WindowPadding.X - rightW);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextDim, "Grid");
        ImGui.SameLine(0, gap);
        if (ImGui.Button("-")) Ctx.StepGrid(-1);
        Tip("Smaller grid  ([)");
        ImGui.SameLine(0, gap);
        float pad = (slot - ImGui.CalcTextSize(gridText).X) * 0.5f;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + pad);
        ImGui.Text(gridText);
        ImGui.SameLine(0, gap + pad);
        if (ImGui.Button("+")) Ctx.StepGrid(1);
        Tip("Larger grid  (])");
        ImGui.SameLine(0, Theme.Px(12));
        if (Toggle("snap", Icon.Snap, "Snap", Ctx.Snap ? "Snap to grid is on" : "Snap to grid is off", Ctx.Snap)) Ctx.Snap = !Ctx.Snap;
        ImGui.SameLine();
        if (Toggle("backfaces", Icon.Backfaces, "Backfaces", "Select faces that point away from you  (Alt+B)", Ctx.Backfaces)) Ctx.Commands.Execute("view.backfaces");

        ImGui.EndChild();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();

        // Hairline under the header so the 3D view reads as a separate surface.
        var p = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddLine(p, p + new Vector2(width, 0), ImGui.ColorConvertFloat4ToU32(Theme.Border));
    }
}
