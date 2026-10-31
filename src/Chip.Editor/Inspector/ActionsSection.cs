using System.Numerics;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Inspector;

/// <summary>
/// What you can do to the selection, as buttons: the mesh operations for the selection mode (inset and offset for
/// faces, bridge, fill and connect for edges, weld for vertices, merge and group for objects). The same commands as the Mesh
/// menu and their hotkeys, which the tooltips show; ones that don't apply to this selection are greyed.
/// </summary>
public sealed class ActionsSection : IInspectorSection
{
    static readonly Dictionary<SelectMode, (string Command, string Label)[]> ByMode = new()
    {
        [SelectMode.Vertex] =
        [
            ("mesh.weld", "Weld"), ("mesh.collapse", "Collapse"), ("mesh.connect", "Connect"),
            ("mesh.dissolve", "Dissolve"), ("tool.bevel", "Bevel"), ("mesh.snapVertices", "To grid"),
        ],
        [SelectMode.Edge] =
        [
            ("mesh.connect", "Connect"), ("mesh.bridge", "Bridge"), ("mesh.fill", "Fill"),
            ("tool.bevel", "Bevel"), ("mesh.dissolve", "Dissolve"), ("mesh.collapse", "Collapse"),
            ("mesh.arcExtrude", "Arc"), ("select.loop", "Loop"), ("select.ring", "Ring"),
        ],
        [SelectMode.Face] =
        [
            ("mesh.inset", "Inset"), ("mesh.outset", "Outset"), ("mesh.offset", "Offset"),
            ("mesh.arcExtrude", "Arc"), ("mesh.mergeFaces", "Merge"), ("mesh.bridge", "Bridge"),
            ("mesh.detach", "Detach"), ("mesh.extract", "Extract"), ("edit.duplicate", "Duplicate"),
            ("mesh.flip", "Flip"), ("mesh.mirror", "Mirror"), ("mesh.dissolve", "Dissolve"),
        ],
        [SelectMode.Object] =
        [
            ("edit.duplicate", "Duplicate"), ("mesh.mirror", "Mirror"), ("mesh.mergeMeshes", "Merge"),
            ("scene.group", "Group"), ("scene.ungroup", "Ungroup"), ("mesh.flip", "Flip"),
            ("mesh.weld", "Weld"), ("mesh.cleanUp", "Clean up"), ("mesh.check", "Check"),
            ("instance.make", "Link"), ("instance.duplicateLinked", "Dup linked"), ("instance.makeUnique", "Unlink"),
        ],
    };

    public string Id => "actions";
    public int Order => 105;
    public bool Visible(EditorContext ctx) => !ctx.Sel.IsEmpty && ByMode.ContainsKey(ctx.Sel.Mode) && !ctx.Interacting;

    public void Draw(EditorContext ctx)
    {
        if (!Heading(ctx.Sel.Mode switch
        {
            SelectMode.Vertex => "Vertices",
            SelectMode.Edge => "Edges",
            SelectMode.Face => "Faces",
            _ => "Objects",
        })) return;
        var items = ByMode[ctx.Sel.Mode];
        float gap = Theme.Px(4);
        const int columns = 3;
        float w = MathF.Floor((ImGui.GetContentRegionAvail().X - gap * (columns - 1)) / columns);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(gap, gap));
        int shown = 0;
        foreach (var (id, label) in items)
        {
            if (ctx.Commands.Find(id) is not { } c) continue;
            if (shown % columns != 0) ImGui.SameLine();
            shown++;
            bool enabled = c.Enabled;
            bool on = c.IsChecked?.Invoke() ?? false;
            ImGui.BeginDisabled(!enabled);
            if (on) PushPrimary();
            if (ImGui.Button($"{label}##{id}", new Vector2(w, 0))) c.Execute();
            if (on) PopPrimary();
            ImGui.EndDisabled();
            string tip = c.HotkeyText is { } key ? $"{c.Label}  ({key})" : c.Label;
            if (c.Description != null) tip += "\n" + c.Description;
            if (!enabled) tip += "\nDoesn't apply to this selection";
            Tip(tip);
        }
        ImGui.PopStyleVar();
    }
}
