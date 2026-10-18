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
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Theme.Px(2, 3));
        bool open = ImGui.TreeNodeEx("Settings", ImGuiTreeNodeFlags.SpanAvailWidth);
        ImGui.PopStyleVar();
        if (!open) return;
        float scale = doc.ExportScale;
        RowLabel("Scale", "Multiplier on export. 1 for Godot (metres), 100 for centimetre engines");
        if (ImGui.InputFloat("##scale", ref scale, 0, 0, "%.3g ×", ImGuiInputTextFlags.EnterReturnsTrue) && scale > 0)
            ctx.Session.Do("Export Scale", () => ctx.Doc.ExportScale = scale);
        bool col = doc.ExportCollision;
        if (ImGui.Checkbox("Static collision", ref col)) ctx.Session.Do("Export Collision", () => ctx.Doc.ExportCollision = col);
        Tip("Names nodes with -col so Godot builds a trimesh collider for each mesh");
        ImGui.TreePop();
    }
}
