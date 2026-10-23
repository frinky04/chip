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
    public int Order => 400; // after the surface (material, texture, hotspot): how it's lit, then collision
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
        if (!Heading("Smoothing", total == 0 ? null : soft == 0 ? "all hard" : soft == total ? "all soft" : $"{soft} of {total} soft")) return;

        RowLabel("Normals", "Set the selection's edges soft or hard, or let each face's normal follow its biggest neighbour");
        CommandRow(ctx.Commands, "normals", ("mesh.softNormals", "Soft", null), ("mesh.hardNormals", "Hard", null), ("mesh.dominantNormals", "Dominant", null));

        // Auto-smooth: the angle, then the commands that use it, in the same field column.
        RowLabel("Angle", "Auto-smooth threshold: edges whose faces meet at less than this angle become soft");
        var style = ImGui.GetStyle();
        float angle = feature.SmoothAngle;
        float gap = Theme.Px(4), presetW = ImGui.CalcTextSize("60°").X + style.FramePadding.X * 2;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - (presetW + gap) * Presets.Length);
        if (ValueSlider("##smoothAngle", "", ref angle, 1, 180, "%.0f°", a => $"{a:0}°")) feature.SmoothAngle = angle;
        ImGui.SameLine(0, gap);
        int preset = ButtonRow("presets", Presets.Select(p => new RowButton($"{p:0}°", $"Set the angle to {p:0}°", Selected: MathF.Abs(feature.SmoothAngle - p) < 0.5f)).ToArray());
        if (preset >= 0) feature.SmoothAngle = Presets[preset];
        FieldStart();
        CommandRow(ctx.Commands, "auto", ("mesh.autoSmooth", "Auto-smooth", null), ("mesh.clearSmoothing", "Clear", null));
    }
}
