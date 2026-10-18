using System.Numerics;
using Chip.Editor.UI;
using ImGuiNET;

namespace Chip.Editor;

/// <summary>Right panel: every visible registered inspector section, in order, separated by hairlines.</summary>
public sealed partial class EditorApp
{
    void DrawInspector(Vector2 pos, Vector2 size)
    {
        ImGui.SetNextWindowPos(pos);
        ImGui.SetNextWindowSize(size);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.Px(12, 10));
        ImGui.Begin("##inspector", FixedPanel);
        ImGui.PushItemWidth(-1);
        bool first = true;
        foreach (var section in Ctx.Sections)
        {
            if (!section.Visible(Ctx)) continue;
            if (!first) Widgets.SectionDivider();
            first = false;
            ImGui.PushID(section.Id);
            section.Draw(Ctx);
            ImGui.PopID();
        }
        ImGui.PopItemWidth();
        ImGui.End();
        ImGui.PopStyleVar();
    }
}
