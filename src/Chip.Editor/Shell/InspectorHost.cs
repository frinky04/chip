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
            long started = _bench != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            if (!section.Visible(Ctx)) { _bench?.RecordSection(section.Id, started); continue; }
            if (!first) Widgets.SectionDivider();
            first = false;
            ImGui.PushID(section.Id);
            section.Draw(Ctx);
            _bench?.RecordSection(section.Id, started);
            ImGui.PopID();
        }
        ImGui.PopItemWidth();
        ImGui.End();
        ImGui.PopStyleVar();
    }
}
