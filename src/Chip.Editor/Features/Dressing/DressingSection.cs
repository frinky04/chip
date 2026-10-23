using System.Numerics;
using Chip.Core.Dressing;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.Dressing;

/// <summary>
/// Inspector block for the level's dressing: which rules touch the selection (Dress with, Bake, the Dressing tab), what
/// each rule made, and every problem with its fix (the selection's first). Shown when the level has a rules file. The
/// dressed / blockout switch is in the viewport header and the Dressing tab.
/// </summary>
public sealed class DressingSection(DressingFeature feature) : IInspectorSection
{
    const int MaxProblems = 6;
    readonly DressingOverrides _overrides = new(feature);
    bool _whyOpen;

    public string Id => "dressing";
    public int Order => 710; // last, right after Tags: what the rules select by, then what they do
    /// <summary>Shown for dressed levels, and (as a way in) for saved levels without rules while something is selected.</summary>
    public bool Visible(EditorContext ctx) => ctx.Dressed() != null || ctx.Session.Path != null && !ctx.Sel.IsEmpty && TagOps.CanTag(ctx.Sel.Mode);

    public void Draw(EditorContext ctx)
    {
        if (ctx.Dressed() is not { } level)
        {
            if (!Heading("Dressing", "none yet")) return;
            RowLabel("Rules", "Dressing turns the blockout into the shipped level: trims, bevels, railings and props from rules");
            ImGui.TextColored(Theme.TextDim, "none yet");
            FieldStart();
            Buttons(ctx, bake: false);
            return;
        }
        int errors = level.ErrorCount, warnings = level.WarningCount;
        int enabled = level.Rules.Rules.Count(r => r.Enabled);
        if (!Heading("Dressing", errors > 0 ? $"{errors} error{(errors == 1 ? "" : "s")}" : warnings > 0 ? $"{warnings} warning{(warnings == 1 ? "" : "s")}" : $"{enabled} rule{(enabled == 1 ? "" : "s")}")) return;

        // Which rules touch the selection, then what to do about it.
        var meshes = ctx.Sel.MeshIds();
        if (meshes.Count > 0)
        {
            var touching = meshes.SelectMany(level.RulesFor).Select(t => t.Rule).Distinct().ToList();
            RowLabel("Rules", "Rules that change or add to the selected meshes");
            string list = touching.Count == 0 ? "none take it" : string.Join(", ", touching);
            string shown = Ellipsize(list, ImGui.GetContentRegionAvail().X);
            ImGui.TextColored(touching.Count == 0 ? Theme.TextDim : Theme.Text, shown);
            if (shown != list) Tip(list);
            FieldStart();
            Buttons(ctx, bake: true);
        }
        else
        {
            FieldStart();
            Buttons(ctx, bake: false);
        }

        // What each rule made (the whole level, when nothing is selected).
        if (meshes.Count == 0)
        {
            foreach (var s in level.Stats)
                Line(s.Rule, !s.Enabled ? "off" : s.Outputs == 0 ? "nothing yet"
                        : $"{s.Outputs} mesh{(s.Outputs == 1 ? "" : "es")} from {s.Targets} {s.TargetNoun}{(s.Targets == 1 ? "" : s.TargetNoun == "mesh" ? "es" : "s")}",
                    Theme.TextDim, $"{s.Rule}: {s.Kind} ({s.Stage.ToString().ToLowerInvariant()}){(s.Enabled ? $", {s.Ms:0.0} ms" : "")}", dim: !s.Enabled);
            foreach (var id in level.Rules.Broken) Line(id, "not run: errors below", Theme.Error, null);
        }

        // One mesh selected: its per-mesh switches and overrides, and why the other rules leave it alone.
        if (meshes.Count == 1 && ctx.Doc.Find(meshes.First()) is { } single)
        {
            _overrides.Draw(ctx, level, single);
            Why(ctx, level, single);
        }

        // Problems: the selection's first, then the level's.
        var problems = level.Diagnostics
            .OrderBy(d => d.MeshId is { } m && meshes.Contains(m) ? 0 : 1).ThenBy(d => d.IsError ? 0 : 1).ToList();
        if (problems.Count == 0) return;
        SectionDivider();
        float wrap = ImGui.GetContentRegionAvail().X;
        foreach (var d in problems.Take(MaxProblems))
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + wrap);
            ImGui.TextColored(d.IsError ? Theme.Error : Theme.Hover, $"{d.Code} {d.Message}");
            if (d.Pos != null || d.Path != null)
                Tip($"{(d.Pos is { } p ? $"{p}\n" : "")}{d.Path}".TrimEnd());
            if (d.Fix != null) ImGui.TextColored(Theme.TextDim, d.Fix);
            ImGui.PopTextWrapPos();
            if (d.MeshId is { } id && ctx.Doc.Find(id) is { } mesh && !meshes.Contains(id))
            {
                if (ImGui.SmallButton($"Select {Clip(mesh.Name, 20)}##{d.GetHashCode()}"))
                {
                    ctx.SetMode(SelectMode.Object);
                    ctx.Sel.Clear();
                    ctx.Sel.Add(ElementRef.Object(id));
                }
            }
            ImGui.Dummy(Theme.Px(0, 2));
        }
        if (problems.Count > MaxProblems)
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(Theme.TextDim, $"and {problems.Count - MaxProblems} more. Chip --rules-check lists them all.");
            ImGui.PopTextWrapPos();
        }
    }

    /// <summary>Dress with and the Dressing tab as a row of two; under them Bake, when the selection has dressing to bake.</summary>
    void Buttons(EditorContext ctx, bool bake)
    {
        int i = ButtonRow("dressing", Command(ctx.Commands, "dressing.dressWith", "Dress with…"),
            new RowButton("Rules…", feature.RulesPath is { } path
                ? $"Edit the rules in the Dressing tab  (Ctrl+Shift+D). They live in {Path.GetFileName(path)}, which agents can edit too; Chip reloads it whenever it changes."
                : "Set up dressing rules in the Dressing tab  (Ctrl+Shift+D)"));
        if (i == 0) ctx.Commands.Execute("dressing.dressWith");
        if (i == 1) feature.Window.Open = true;
        if (!bake || ctx.Commands.Find("dressing.bake") is not { Enabled: true }) return;
        FieldStart();
        if (ImGui.Button("Bake dressing", new Vector2(-1, 0))) ctx.Commands.Execute("dressing.bake");
        Tip("Turn the selection's dressing into ordinary meshes you can edit by hand. They stop following the rules.");
    }

    /// <summary>
    /// "Why isn't this dressed?": for the selected face (one face in face mode) or mesh, each rule that leaves it alone and
    /// why (a missing tag, the wrong facing, switched off), with the closest tag it has when that looks like a typo.
    /// </summary>
    void Why(EditorContext ctx, DressedLevel level, Core.Geometry.PolyMesh mesh)
    {
        int face = ctx.Sel.Mode == SelectMode.Face && ctx.Sel.Count == 1 ? ctx.Sel.Items.First().A : -1;
        var reasons = level.Explain(mesh, face).Where(r => r.Reason != null).ToList();
        if (reasons.Count == 0) return;
        ImGui.Dummy(Theme.Px(0, 2));
        string what = face >= 0 ? "face" : "mesh";
        string why = _whyOpen ? $"Hide rules that skip this {what}" : $"Why don't {reasons.Count} rule{(reasons.Count == 1 ? "" : "s")} take this {what}?";
        string shown = Ellipsize(why, ImGui.GetContentRegionAvail().X - ImGui.GetStyle().FramePadding.X * 2);
        if (ImGui.Button($"{shown}##why", new Vector2(-1, 0))) _whyOpen = !_whyOpen;
        if (shown != why) Tip(why);
        if (!_whyOpen) return;
        float w = ImGui.GetContentRegionAvail().X;
        float line = ImGui.GetTextLineHeight();
        foreach (var (rule, reason) in reasons)
        {
            // One selectable row per rule: name, the reason under it, a chevron to open the rule.
            ImGui.PushID(rule.Id);
            var p = ImGui.GetCursorScreenPos();
            float textW = w - Theme.Px(24);
            var reasonSize = ImGui.CalcTextSize(reason!, false, textW);
            float h = line + reasonSize.Y + Theme.Px(6);
            if (ImGui.Selectable("##why", false, ImGuiSelectableFlags.None, new Vector2(w, h))) feature.Window.ShowRule(rule.Id);
            Tip($"Open {rule.Id} in the Dressing tab");
            var dl = ImGui.GetWindowDrawList();
            dl.AddText(p + Theme.Px(4, 2), ImGui.ColorConvertFloat4ToU32(Theme.Text), rule.Id);
            dl.AddText(ImGui.GetFont(), ImGui.GetFontSize(), p + new Vector2(Theme.Px(4), line + Theme.Px(2)), ImGui.ColorConvertFloat4ToU32(Theme.TextDim), reason!, textW);
            Icons.Draw(dl, Icon.ChevronRight, new Vector2(p.X + w - Theme.Px(12), p.Y + h * 0.5f), ImGui.ColorConvertFloat4ToU32(Theme.TextDim));
            ImGui.PopID();
        }
    }

    static string Clip(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    /// <summary>A name on the left and a summary right-aligned, clipped so the two never overlap.</summary>
    static void Line(string left, string right, Vector4 rightColor, string? tip, bool dim = false)
    {
        float avail = ImGui.GetContentRegionAvail().X;
        float rw = ImGui.CalcTextSize(right).X;
        float lw = ImGui.CalcTextSize(left).X;
        float gap = Theme.Px(12);
        if (lw + gap + rw > avail) left = Clip(left, Math.Max(4, (int)(left.Length * (avail - rw - gap) / MathF.Max(lw, 1))));
        float x = ImGui.GetCursorPosX();
        ImGui.TextColored(dim ? Theme.TextDim : Theme.Text, left);
        if (tip != null) Tip(tip);
        ImGui.SameLine(x + MathF.Max(ImGui.CalcTextSize(left).X + gap, avail - rw));
        ImGui.TextColored(rightColor, right);
    }
}
