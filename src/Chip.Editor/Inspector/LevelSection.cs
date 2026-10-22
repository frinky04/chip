using System.Numerics;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Inspector;

/// <summary>
/// With nothing selected the inspector shows the level itself: its texel density, where it exports, and its dressing
/// rules file. Everything about the level as a whole lives here rather than in a menu.
/// </summary>
public sealed class LevelSection : IInspectorSection
{
    readonly Rendering.FrameCache<string> _counts = new(selection: false);

    public string Id => "level";
    public int Order => 90;
    public bool Visible(EditorContext ctx) => ctx.Sel.IsEmpty;

    public void Draw(EditorContext ctx)
    {
        var doc = ctx.Doc;
        string name = ctx.Session.Path != null ? Path.GetFileNameWithoutExtension(ctx.Session.Path) : "Untitled";
        if (!Heading("Level", name)) return;
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
        ImGui.TextWrapped($"Nothing selected. Click or drag to select {ctx.Sel.Mode.Plural()}.");
        ImGui.PopStyleColor();
        if (!_counts.IsCurrent(ctx.Session))
            _counts.Store(ctx.Session, $"{Count(doc.Meshes.Count, "mesh", "meshes")}, {Count(doc.Meshes.Sum(m => m.Faces.Count), "face", "faces")}, {Count(doc.Entities.Count, "entity", "entities")}");
        ImGui.TextColored(Theme.TextDisabled, _counts.Value);
        SectionDivider();

        RowLabel("Density", "Texel density of this level: how many texture pixels cover a metre. Textures without their own density follow it; changing it rescales their faces.");
        if (DensityField("##levelDensity", doc.TexelDensity, out float density))
        {
            int faces = 0;
            ctx.Session.Do("Texel Density", () => faces = TexelDensityOps.Change(doc, ctx.Materials, density));
            ctx.Status($"Level texel density: 1 m is {density:0.##} px. {(faces == 1 ? "1 face" : $"{faces} faces")} rescaled to match");
        }

        RowLabel("Export", "Where Ctrl+E writes the .glb");
        string target = doc.ExportPath != null ? Path.GetFileName(doc.ExportPath) : "not chosen yet";
        ImGui.TextColored(doc.ExportPath != null ? Theme.Text : Theme.TextDim, MaterialTilesFit(target));
        if (doc.ExportPath != null) Tip(doc.ExportPath);
        ImGui.SetCursorPosX(LabelColumn + ImGui.GetStyle().WindowPadding.X);
        float half = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
        if (ImGui.Button("Export now", new Vector2(half, 0))) ctx.Commands.Execute("file.export");
        Tip("Write the .glb  (Ctrl+E)");
        ImGui.SameLine();
        if (ImGui.Button("Settings…", new Vector2(-1, 0))) ctx.Commands.Execute("pipeline.exportSettings");
        Tip("Scale, collision baking, tessellation for vertex lighting, textures and material remaps");
        ImGui.SetCursorPosX(LabelColumn + ImGui.GetStyle().WindowPadding.X);
        bool onSave = doc.Export.ExportOnSave;
        if (ImGui.Checkbox("Export on save", ref onSave)) ctx.Commands.Execute("pipeline.exportOnSave");
        Tip("Every save also writes the .glb, so the engine picks up changes  (Ctrl+Alt+E)");

        RowLabel("Rules", "Dressing rules: they turn the blockout into the shipped level (docs/dressing.md)");
        var rules = ctx.Dressed();
        ImGui.TextColored(rules != null ? Theme.Text : Theme.TextDim, rules != null ? Count(rules.Rules.Rules.Count(r => r.Enabled), "rule", "rules") + " on" : "no rules yet");
        ImGui.SameLine();
        float w = ImGui.CalcTextSize("Edit").X + ImGui.GetStyle().FramePadding.X * 2;
        ImGui.SetCursorPosX(ImGui.GetWindowContentRegionMax().X - w);
        if (ImGui.SmallButton("Edit")) ctx.Commands.Execute("dressing.rules");
        Tip("Open the Dressing tab  (Ctrl+Shift+D)");
    }

    static string MaterialTilesFit(string text) => Features.Materials.MaterialTiles.Fit(text, ImGui.GetContentRegionAvail().X);

    static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";
}
