using System.Numerics;
using Chip.Core.Scene;
using Chip.Editor.UI;
using ImGuiNET;

namespace Chip.Editor.Features.Logic;

public enum IoColumn { Source, Output, Target, Input, Value, Delay, Limits }

/// <summary>
/// Connections as a table, Hammer's Outputs list made sortable: one row per connection, the columns asked for, a row in
/// red when it can't work (amber when it may not), the reason and the whole wire on hover. Hovering a row rings the
/// other end in the viewport. Click selects a row; double-click activates it (the caller decides what that means).
/// </summary>
public static class IoTable
{
    static string Header(IoColumn c) => c switch
    {
        IoColumn.Source => "Source",
        IoColumn.Output => "Output",
        IoColumn.Target => "Target",
        IoColumn.Input => "Input",
        IoColumn.Value => "Value",
        IoColumn.Delay => "Delay",
        _ => "When",
    };

    static string Cell(IoRow r, IoColumn c) => c switch
    {
        IoColumn.Source => r.SourceText,
        IoColumn.Output => r.Output,
        IoColumn.Target => r.Target,
        IoColumn.Input => r.Input,
        IoColumn.Value => r.Value,
        IoColumn.Delay => r.Delay,
        _ => r.Limits,
    };

    /// <summary>
    /// Text columns share the width, the target's the widest; delay is fixed, wide enough for its header and the sort
    /// arrow a sortable header keeps room for.
    /// </summary>
    static (ImGuiTableColumnFlags Flags, float Width) Size(IoColumn c) => c switch
    {
        IoColumn.Delay => (ImGuiTableColumnFlags.WidthFixed, MathF.Max(ImGui.CalcTextSize("Delay").X, ImGui.CalcTextSize("0.55 s").X) + ImGui.GetFontSize()),
        IoColumn.Target => (ImGuiTableColumnFlags.WidthStretch, 1.3f),
        IoColumn.Value or IoColumn.Limits => (ImGuiTableColumnFlags.WidthStretch, 0.9f),
        _ => (ImGuiTableColumnFlags.WidthStretch, 1f),
    };

    /// <summary>
    /// Draws the table in <paramref name="height"/> (0 fits up to a dozen rows), and returns the row clicked this frame,
    /// with <paramref name="activated"/> true for a double-click. <paramref name="other"/> names the entity a row's hover
    /// rings: its target in an outgoing list, its source in an incoming one.
    /// </summary>
    public static IoRow? Draw(string id, IReadOnlyList<IoRow> rows, IoColumn[] columns, (int Entity, int Index)? selected,
        Func<IoRow, int?> other, out bool activated, float height = 0)
    {
        activated = false;
        IoRow? clicked = null;
        float rowH = ImGui.GetTextLineHeight() + ImGui.GetStyle().CellPadding.Y * 2;
        if (height <= 0) height = rowH * (Math.Clamp(rows.Count, 1, 12) + 1) + Theme.Px(4);
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.BordersOuterH | ImGuiTableFlags.Resizable
            | ImGuiTableFlags.Sortable | ImGuiTableFlags.SortTristate | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable(id, columns.Length, flags, new Vector2(0, height))) return null;

        ImGui.TableSetupScrollFreeze(0, 1);
        foreach (var c in columns)
        {
            var (sizing, width) = Size(c);
            ImGui.TableSetupColumn(Header(c), sizing, width);
        }
        ImGui.TableHeadersRow();

        var shown = Sorted(rows, columns);
        if (shown.Count == 0)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(Theme.TextDisabled, "None");
        }

        uint error = ImGui.ColorConvertFloat4ToU32(Theme.Error), warning = Theme.HoverU32;
        uint selectedBg = ImGui.ColorConvertFloat4ToU32(Theme.AccentDim with { W = 0.55f });
        var clipper = new ImGuiListClipper();
        unsafe
        {
            var clip = new ImGuiListClipperPtr(&clipper);
            clip.Begin(shown.Count);
            while (clip.Step())
                for (int i = clip.DisplayStart; i < clip.DisplayEnd; i++)
                {
                    var r = shown[i];
                    ImGui.TableNextRow();
                    bool isSelected = selected == (r.Source.Id, r.Index);
                    ImGui.PushID($"{r.Source.Id}/{r.Index}");
                    bool tint = r.HasError || r.HasWarning;
                    if (tint) ImGui.PushStyleColor(ImGuiCol.Text, r.HasError ? error : warning);
                    for (int c = 0; c < columns.Length; c++)
                    {
                        ImGui.TableNextColumn();
                        string text = Cell(r, columns[c]);
                        if (c > 0) { ImGui.TextUnformatted(text); continue; }
                        // The first cell's selectable spans the row, so the whole row clicks and highlights. The theme's
                        // selected colour is the frame grey, which reads as one more row stripe: selected rows get the accent.
                        if (isSelected) ImGui.PushStyleColor(ImGuiCol.Header, selectedBg);
                        if (ImGui.Selectable($"{text}##row", isSelected, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick))
                        {
                            clicked = r;
                            activated = ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
                        }
                        if (isSelected) ImGui.PopStyleColor();
                        if (ImGui.IsItemHovered())
                        {
                            if (other(r) is { } o) LinkDraw.Highlight = o;
                            if (tint) ImGui.PopStyleColor();
                            ImGui.SetTooltip(Describe(r));
                            if (tint) ImGui.PushStyleColor(ImGuiCol.Text, r.HasError ? error : warning);
                        }
                    }
                    if (tint) ImGui.PopStyleColor();
                    ImGui.PopID();
                }
            clip.End();
        }
        ImGui.EndTable();
        return clicked;
    }

    /// <summary>The whole wire in a sentence, then what's wrong with it.</summary>
    static string Describe(IoRow r)
    {
        var line = $"{r.SourceText}: {r.Output.ToLowerInvariant()} → {r.Target}: {r.Input.ToLowerInvariant()}";
        if (r.Value.Length > 0) line += $" ({r.Value})";
        if (r.Delay.Length > 0) line += $" after {r.Delay}";
        if (r.Limits.Length > 0) line += $", {r.Limits}";
        return r.Problems.Count > 0 ? $"{line}\n\n{r.ProblemText}" : line;
    }

    /// <summary>The rows in the table's sort order; unsorted, as the level holds them (source order, then each entity's connection order).</summary>
    static List<IoRow> Sorted(IReadOnlyList<IoRow> rows, IoColumn[] columns)
    {
        var specs = ImGui.TableGetSortSpecs();
        unsafe
        {
            if (specs.NativePtr == null || specs.SpecsCount == 0) return rows.ToList();
        }
        var spec = specs.Specs;
        var column = columns[spec.ColumnIndex];
        bool down = spec.SortDirection == ImGuiSortDirection.Descending;
        // Stable sorts: ties keep the level's order, so sorting by output still reads in firing order.
        if (column == IoColumn.Delay)
            return (down ? rows.OrderByDescending(r => r.Connection.Delay) : rows.OrderBy(r => r.Connection.Delay)).ToList();
        return (down ? rows.OrderByDescending(r => Cell(r, column), StringComparer.OrdinalIgnoreCase)
            : rows.OrderBy(r => Cell(r, column), StringComparer.OrdinalIgnoreCase)).ToList();
    }
}
