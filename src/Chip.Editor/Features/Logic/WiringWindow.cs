using System.Numerics;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.Logic;

/// <summary>
/// View > Wiring (Ctrl+Shift+I): every connection in the level in one sortable table, so the whole of a level's logic
/// can be read and checked at once. Filter by text, show only what touches the selection, or only what's broken. Clicking
/// a row selects its source entity with that connection open; double-clicking also frames it. The selected connection
/// can be edited here too.
/// </summary>
public sealed class WiringWindow
{
    public bool Open;
    bool _wasOpen;
    string _filter = "";
    int _scope; // 0 all, 1 touching the selection, 2 with problems
    (int Entity, int Index)? _selected;
    readonly ConnectionEditor _editor = new();

    public void Draw(EditorContext ctx)
    {
        if (!Open) { _wasOpen = false; return; }
        var vp = ctx.ViewportSize;
        ImGui.SetNextWindowPos(ctx.ViewportOrigin + new Vector2(MathF.Max(Theme.Px(10), (vp.X - Theme.Px(760)) * 0.5f), Theme.Px(56)), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(Theme.Px(760, 520), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(Theme.Px(480, 260), new Vector2(float.MaxValue));
        if (!_wasOpen) ImGui.SetNextWindowFocus();
        _wasOpen = true;
        bool open = true;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.Px(12, 10));
        bool visible = ImGui.Begin("Wiring###wiring", ref open, ImGuiWindowFlags.NoCollapse);
        ImGui.PopStyleVar();
        if (visible) Contents(ctx);
        ImGui.End();
        if (!open) Open = false;
    }

    void Contents(EditorContext ctx)
    {
        var all = IoRows.All(ctx);
        var selection = ctx.Sel.Entities(ctx.Doc).Select(e => e.Id).ToHashSet();
        int errors = all.Count(r => r.HasError), warnings = all.Count(r => r.HasWarning);

        float searchW = Theme.Px(220);
        ImGui.SetNextItemWidth(searchW);
        ImGui.InputTextWithHint("##filter", "Filter: an entity, output, input, value…", ref _filter, 128);
        ImGui.SameLine();
        if (TextSegments("##scope", [
            ("All", "Every connection in the level"),
            ("Selection", "Connections from or to the selected entities"),
            (errors + warnings > 0 ? $"Problems  {errors + warnings}" : "Problems", "Connections that can't work (red) or may not (amber)"),
        ], _scope) is >= 0 and var scope) _scope = scope;
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        string summary = $"{all.Count} connection{(all.Count == 1 ? "" : "s")}";
        if (errors > 0) summary += $", {errors} broken";
        if (warnings > 0) summary += $", {warnings} to check";
        ImGui.TextColored(errors > 0 ? Theme.Error : Theme.TextDim, summary);

        var rows = all.Where(r => r.Matches(_filter.Trim()) && _scope switch
        {
            1 => selection.Contains(r.Source.Id) || r.Targets.Any(t => selection.Contains(t.Id)),
            2 => r.Problems.Count > 0,
            _ => true,
        }).ToList();
        if (_selected is { } s && IoRows.Find(ctx, s.Entity, s.Index) == null) _selected = null;

        // The table takes what the editor below leaves.
        float editorH = _selected != null ? Theme.Px(200) : 0;
        float tableH = MathF.Max(Theme.Px(120), ImGui.GetContentRegionAvail().Y - editorH);
        var clicked = IoTable.Draw("##wiring", rows,
            [IoColumn.Source, IoColumn.Output, IoColumn.Target, IoColumn.Input, IoColumn.Value, IoColumn.Delay, IoColumn.Limits],
            _selected, r => r.Source.Id, out bool activated, tableH);
        if (all.Count == 0) Dim("No connections yet. Select an entity and add one under Entity IO in the inspector.");
        else if (rows.Count == 0) Dim(_scope == 1 && selection.Count == 0 ? "Select entities to see their wiring." : "Nothing matches.");

        if (clicked != null)
        {
            _selected = (clicked.Source.Id, clicked.Index);
            IoFocus.Show(ctx, clicked.Source, clicked.Index);
            if (activated) ctx.Commands.Execute("view.focus");
        }

        if (_selected is { } sel && ctx.Doc.FindEntity(sel.Entity) is { } source)
        {
            ImGui.BeginChild("##edit", Vector2.Zero);
            _editor.Draw(ctx, source, sel.Index);
            ImGui.EndChild();
        }
    }

    static void Dim(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }
}
