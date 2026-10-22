using System.Numerics;
using Chip.Core.Dressing;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.Dressing;

/// <summary>
/// Inspector block for the level's dressing: the rules file, dressed / blockout view, which rules touch the selection,
/// what each rule made, and every problem with its fix (the selection's first). Shown when the level has a rules file.
/// </summary>
public sealed class DressingSection(DressingFeature feature) : IInspectorSection
{
    const int MaxProblems = 6;
    readonly DressingOverrides _overrides = new(feature);
    bool _whyOpen;

    public string Id => "dressing";
    public int Order => 160; // right after Tags: what the rules select by, then what they do
    public bool Visible(EditorContext ctx) => ctx.Dressed() != null;

    public void Draw(EditorContext ctx)
    {
        if (ctx.Dressed() is not { } level) return;
        int errors = level.ErrorCount, warnings = level.WarningCount;
        int enabled = level.Rules.Rules.Count(r => r.Enabled);
        Heading("Dressing", errors > 0 ? $"{errors} error{(errors == 1 ? "" : "s")}" : warnings > 0 ? $"{warnings} warning{(warnings == 1 ? "" : "s")}" : $"{enabled} rule{(enabled == 1 ? "" : "s")}");

        int view = TextSegments("##dressView", [("Dressed", "Show the level as shipped  (`)"), ("Blockout", "Show only the blockout. Export still includes the dressing  (`)")], ctx.ShowDressed ? 0 : 1);
        if (view >= 0 && (view == 0) != ctx.ShowDressed) ctx.Commands.Execute("view.dressed");
        ImGui.SameLine();
        float openW = ImGui.CalcTextSize("Rules...").X + ImGui.GetStyle().FramePadding.X * 2;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, ImGui.GetContentRegionAvail().X - openW));
        if (ImGui.Button("Rules...")) feature.Window.Open = true;
        Tip($"Edit the rules (Ctrl+Shift+D). They live in {Path.GetFileName(feature.RulesPath)}, which agents can edit too; Chip reloads it whenever it changes.");

        // What touches the selection.
        var meshes = ctx.Sel.MeshIds();
        if (meshes.Count > 0)
        {
            var touching = meshes.SelectMany(level.RulesFor).Select(t => t.Rule).Distinct().ToList();
            Line("Selection", touching.Count == 0 ? "no rules apply" : string.Join(", ", touching), Theme.TextDim, "Rules that change or add to the selected meshes");
            if (ctx.Commands.Find("dressing.bake") is { Enabled: true })
            {
                if (ImGui.Button("Bake dressing", new Vector2(-1, 0))) ctx.Commands.Execute("dressing.bake");
                Tip("Turn the selection's dressing into ordinary meshes you can edit by hand. They stop following the rules.");
            }
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
            ImGui.TextColored(Theme.TextDim, $"and {problems.Count - MaxProblems} more. Chip --rules-check lists them all.");
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
        if (ImGui.SmallButton(_whyOpen ? $"Hide rules that skip this {(face >= 0 ? "face" : "mesh")}" : $"Why don't {reasons.Count} rule{(reasons.Count == 1 ? "" : "s")} take this {(face >= 0 ? "face" : "mesh")}?"))
            _whyOpen = !_whyOpen;
        if (!_whyOpen) return;
        float wrap = ImGui.GetContentRegionAvail().X;
        foreach (var (rule, reason) in reasons)
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + wrap);
            ImGui.Text(rule.Id);
            ImGui.SameLine();
            ImGui.TextColored(Theme.TextDim, reason!);
            ImGui.PopTextWrapPos();
            if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                feature.Window.Open = true;
                feature.Window.Selected = rule.Id;
            }
            Tip("Click to open the rule");
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
