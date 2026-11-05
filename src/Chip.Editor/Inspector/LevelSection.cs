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
    readonly Features.Scene.WorldPropertiesFields _world = new();
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
        _world.Draw(ctx);
        SectionDivider();

        RowLabel("Density", "Texel density of this level: how many texture pixels cover a metre. New faces use it, unless their texture has its own density. Faces already in the level stay as they are.");
        if (DensityField("##levelDensity", doc.TexelDensity, out float density))
        {
            // Changing it twice before rescaling still rescales from what the faces were built at.
            if (!RescalePending(doc)) (_rescaleDoc, _rescaleFrom) = (doc, doc.TexelDensity);
            ctx.Session.Do("Texel Density", () => TexelDensityOps.Change(doc, ctx.Materials, density));
            _rescaleTo = density;
            ctx.Status($"Level texel density: 1 m is {density:0.##} px. New faces use it; existing faces stay as they are");
        }
        if (RescalePending(doc))
        {
            FieldStart();
            if (ImGui.Button("Rescale existing faces", new Vector2(-1, 0)))
            {
                int faces = 0;
                float from = _rescaleFrom;
                ctx.Session.Do("Rescale Faces", () => faces = TexelDensityOps.Rescale(doc, ctx.Materials, from, doc.TexelDensity));
                _rescaleDoc = null;
                ctx.Status($"{(faces == 1 ? "1 face" : $"{faces} faces")} rescaled from {from:0.##} to {doc.TexelDensity:0.##} px per metre");
            }
            Tip($"Scale faces built at {_rescaleFrom:0.##} px per metre to match {doc.TexelDensity:0.##}, so their textures tile differently. Faces with hand-placed UVs, and textures with their own density, stay as they are.");
        }

        RowLabel("Export", "Where Ctrl+E writes the .glb");
        string target = doc.ExportPath != null ? Path.GetFileName(doc.ExportPath) : "not chosen yet";
        ImGui.TextColored(doc.ExportPath != null ? Theme.Text : Theme.TextDim, Ellipsize(target, ImGui.GetContentRegionAvail().X));
        if (doc.ExportPath != null) Tip(doc.ExportPath);
        FieldStart();
        int export = ButtonRow("export",
            Command(ctx.Commands, "file.export", "Export now"),
            new RowButton("Settings…", "Scale, collision baking, tessellation for vertex lighting, textures and material remaps"));
        if (export == 0) ctx.Commands.Execute("file.export");
        if (export == 1) ctx.Commands.Execute("pipeline.exportSettings");
        FieldStart();
        bool onSave = doc.Export.ExportOnSave;
        if (Check("Export on save", ref onSave, "Every save also writes the .glb, so the engine picks up changes  (Ctrl+Alt+E)"))
            ctx.Commands.Execute("pipeline.exportOnSave");

        RowLabel("Rules", "Dressing rules: they turn the blockout into the shipped level (docs/dressing.md)");
        var rules = ctx.Dressed();
        ImGui.TextColored(rules != null ? Theme.Text : Theme.TextDim, rules != null ? Count(rules.Rules.Rules.Count(r => r.Enabled), "rule", "rules") + " on" : "none yet");
        FieldStart();
        if (ImGui.Button(rules != null ? "Edit rules…" : "Set up rules…", new Vector2(-1, 0))) ctx.Commands.Execute("dressing.rules");
        Tip("Open the Dressing tab  (Ctrl+Shift+D)");
    }

    // The density change the Rescale button would follow. Undoing the change, or opening another level, drops the offer.
    Document? _rescaleDoc;
    float _rescaleFrom, _rescaleTo;

    bool RescalePending(Document doc) => ReferenceEquals(_rescaleDoc, doc) && doc.TexelDensity == _rescaleTo && _rescaleFrom != _rescaleTo;

    static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";
}
