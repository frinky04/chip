using System.Numerics;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;

namespace Chip.Editor;

/// <summary>Status bar: reports only. Selection, then a message or the active tool's hints, then scene counts.</summary>
public sealed partial class EditorApp
{
    void DrawStatusBar(Vector2 pos, Vector2 size)
    {
        ImGui.SetNextWindowPos(pos);
        ImGui.SetNextWindowSize(size);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Theme.Px(6, 2));
        float rowH = ImGui.GetTextLineHeight() + Theme.Px(2) * 2;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Theme.Px(12), MathF.Floor((size.Y - rowH) * 0.5f)));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Theme.Bg);
        ImGui.Begin("##status", FixedPanel | ImGuiWindowFlags.NoScrollbar);
        ImGui.AlignTextToFramePadding();

        var sel = Ctx.Sel;
        ImGui.Text(sel.Mode.Name());
        ImGui.SameLine(0, Theme.Px(6));
        ImGui.TextColored(Theme.TextDim, sel.IsEmpty ? "nothing selected" : $"{sel.Count} selected");

        int faces = Ctx.Doc.Meshes.Sum(m => m.Faces.Count);
        string counts = $"{Ctx.Doc.Meshes.Count} meshes   {faces} faces";
        float rightX = size.X - ImGui.GetStyle().WindowPadding.X - ImGui.CalcTextSize(counts).X;

        ImGui.SameLine(Theme.Px(210));
        bool showMessage = Ctx.StatusMessage.Length > 0 && Ctx.Now < Ctx.StatusUntil;
        ImGui.PushClipRect(ImGui.GetCursorScreenPos(), new Vector2(pos.X + rightX - Theme.Px(16), pos.Y + size.Y), true);
        if (showMessage)
        {
            ImGui.TextColored(Ctx.StatusIsError ? Theme.Error : Theme.Text, Esc(Ctx.StatusMessage)); // messages carry file names
            if (Ctx.StatusIsError)
            {
                ImGui.SameLine();
                if (ImGui.Button("Dismiss")) Ctx.ClearStatus();
            }
        }
        else if (Ctx.ActiveTool != null) Widgets.KeyHints(Ctx.ActiveTool.Hints);
        ImGui.PopClipRect();

        ImGui.SameLine(rightX);
        ImGui.TextColored(Theme.TextDim, counts);

        ImGui.End();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(2);
    }
}
