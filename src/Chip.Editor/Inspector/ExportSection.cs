using System.Numerics;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Inspector;

/// <summary>Export button, destination, and document export settings.</summary>
public sealed class ExportSection : IInspectorSection
{
    public string Id => "export";
    public int Order => 900;
    public bool Visible(EditorContext ctx) => true;

    public void Draw(EditorContext ctx)
    {
        var doc = ctx.Doc;
        Heading("Export", doc.ExportPath != null ? Path.GetFileName(doc.ExportPath) : null);
        if (ImGui.Button("Export GLB", new Vector2(-1, 0))) ctx.Commands.Execute("file.export");
        Tip(doc.ExportPath != null ? $"Write {doc.ExportPath}  (Ctrl+E)" : "Choose where to write the .glb  (Ctrl+E)");
        // One place for every export option (scale, collision bake, tessellation, textures, remaps): the settings window.
        if (ImGui.Button("Export settings…", new Vector2(-1, 0))) ctx.Commands.Execute("pipeline.exportSettings");
        Tip("Scale, collision baking, tessellation for vertex lighting, textures and material remaps  (File > Export settings…)");
    }
}
