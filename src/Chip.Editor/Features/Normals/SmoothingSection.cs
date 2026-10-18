using Chip.Core.Geometry;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.Normals;

/// <summary>
/// Inspector block for the selection's smoothing: soft / hard / dominant buttons, the auto-smooth angle and the
/// auto-smooth and clear actions. The heading counts soft edges in the selection's scope.
/// </summary>
public sealed class SmoothingSection(NormalsFeature feature) : IInspectorSection
{
    readonly Rendering.FrameCache<(int Soft, int Total)> _counts = new();
    static readonly float[] Presets = [30, 45, 60];

    public NormalsFeature Feature => feature;
    public string Id => "smoothing";
    public int Order => 250;
    public bool Visible(EditorContext ctx) => !ctx.Sel.IsEmpty;

    public void Draw(EditorContext ctx)
    {
        if (!_counts.IsCurrent(ctx.Session))
        {
            int softCount = 0, totalCount = 0;
            if (ctx.Sel.Mode == SelectMode.Object)
            {
                var meshes = ctx.Doc.Meshes.Where(m => ctx.Sel.Items.Contains(ElementRef.Object(m.Id))).ToArray();
                var counts = new (int Soft, int Total)[meshes.Length];
                Parallel.For(0, meshes.Length, i =>
                {
                    int softEdges = 0, allEdges = 0;
                    foreach (var edge in meshes[i].Edges())
                    {
                        allEdges++;
                        if (meshes[i].SoftEdges.Contains(edge)) softEdges++;
                    }
                    counts[i] = (softEdges, allEdges);
                });
                foreach (var count in counts) { softCount += count.Soft; totalCount += count.Total; }
            }
            else
                foreach (var (mesh, edges) in feature.ScopeEdges())
                {
                    totalCount += edges.Count;
                    softCount += edges.Count(mesh.SoftEdges.Contains);
                }
            _counts.Store(ctx.Session, (softCount, totalCount));
        }
        var (soft, total) = _counts.Value;
        Heading("Smoothing", total == 0 ? null : soft == 0 ? "all hard" : soft == total ? "all soft" : $"{soft} of {total} soft");

        var style = ImGui.GetStyle();
        float w = (ImGui.GetContentRegionAvail().X - style.ItemSpacing.X * 2) / 3;
        CommandButton(ctx, "mesh.softNormals", "Soft", w);
        ImGui.SameLine();
        CommandButton(ctx, "mesh.hardNormals", "Hard", w);
        ImGui.SameLine();
        CommandButton(ctx, "mesh.dominantNormals", "Dominant", w);

        RowLabel("Angle", "Auto-smooth threshold: edges whose faces meet at less than this angle become soft");
        float angle = feature.SmoothAngle;
        float presetW = ImGui.CalcTextSize("60°").X + style.FramePadding.X * 2;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - (presetW + style.ItemSpacing.X) * Presets.Length);
        if (Widgets.ValueSlider("##smoothAngle", "", ref angle, 1, 180, "%.0f°", a => $"{a:0}°")) feature.SmoothAngle = angle;
        foreach (var p in Presets)
        {
            ImGui.SameLine();
            bool on = MathF.Abs(feature.SmoothAngle - p) < 0.5f;
            if (on) ImGui.PushStyleColor(ImGuiCol.Button, Theme.FrameHover);
            if (ImGui.Button($"{p:0}°##preset", new System.Numerics.Vector2(presetW, 0))) feature.SmoothAngle = p;
            if (on) ImGui.PopStyleColor();
            Tip($"Set the angle to {p:0}°");
        }

        ImGui.Dummy(new System.Numerics.Vector2(LabelColumn - style.ItemSpacing.X, 0));
        ImGui.SameLine();
        float half = (ImGui.GetContentRegionAvail().X - style.ItemSpacing.X) / 2;
        CommandButton(ctx, "mesh.autoSmooth", "Auto-smooth", half);
        ImGui.SameLine();
        CommandButton(ctx, "mesh.clearSmoothing", "Clear", half);
    }

    /// <summary>A button that runs a registered command, greyed when it can't run, with its description and hotkey as the tip.</summary>
    static void CommandButton(EditorContext ctx, string id, string label, float width)
    {
        if (ctx.Commands.Find(id) is not { } cmd) return;
        bool enabled = cmd.Enabled;
        ImGui.BeginDisabled(!enabled);
        if (ImGui.Button($"{label}##{id}", new System.Numerics.Vector2(width, 0))) ctx.Commands.Execute(id);
        ImGui.EndDisabled();
        Tip(cmd.HotkeyText is { } key ? $"{cmd.Description}  ({key})" : cmd.Description ?? cmd.Label);
    }
}
