using System.Numerics;
using Chip.Editor.UI;
using ImGuiNET;

namespace Chip.Editor;

/// <summary>
/// Right panel: every visible registered inspector section, in order, separated by hairlines. A section's title bar
/// folds it; folded sections are remembered in settings.
/// </summary>
public sealed partial class EditorApp
{
    void DrawInspector(Vector2 pos, Vector2 size)
    {
        ImGui.SetNextWindowPos(pos);
        ImGui.SetNextWindowSize(size);
        // No top padding: the first section's dock title sits flush with the top, like Krita's dockers.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.Px(12, 0));
        ImGui.Begin("##inspector", FixedPanel);
        ImGui.PushItemWidth(-1);
        Widgets.Folded = _settings.Current.CollapsedSections;
        bool first = true;
        foreach (var section in Ctx.Sections)
        {
            long started = _bench != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            if (!section.Visible(Ctx)) { _bench?.RecordSection(section.Id, started); continue; }
            if (!first) Widgets.SectionDivider();
            first = false;
            ImGui.PushID(section.Id);
            Widgets.BeginDockSection(section.Id);
            section.Draw(Ctx);
            Widgets.EndDockSection();
            _bench?.RecordSection(section.Id, started);
            ImGui.PopID();
        }
        Widgets.SectionDivider();
        ImGui.PopItemWidth();
        ImGui.End();
        ImGui.PopStyleVar();
        if (Widgets.FoldChanged)
        {
            Widgets.FoldChanged = false;
            _settings.Save();
        }
    }
}
