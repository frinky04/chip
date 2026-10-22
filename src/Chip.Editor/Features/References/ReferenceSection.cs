using Chip.Core.IO;
using Chip.Core.Scene;
using Chip.Editor.Features.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.References;

/// <summary>
/// The selected reference model: which model, its size, and whether it exports as a marker node. Sits under the entity
/// section, which still edits its name, origin and angles.
/// </summary>
public sealed class ReferenceSection : IInspectorSection
{
    public string Id => "reference";
    public int Order => 111;

    public bool Visible(EditorContext ctx) => ctx.Sel.Entities(ctx.Doc) is [{ } e] && ReferenceModels.Is(e);

    public void Draw(EditorContext ctx)
    {
        var e = ctx.Sel.Entities(ctx.Doc)[0];
        int id = e.Id;
        var model = ReferenceModels.ModelOf(e);
        Heading("Reference", ReferenceModels.DisplayName(model));

        RowLabel("Model", "A built-in model or a .glb/.gltf file, drawn exactly where your engine would put it at this origin");
        if (ReferencePicker.Draw($"##model{id}", model, out var chosen))
            EntitySection.Edit(ctx, id, "Change Reference Model", x => EntityOps.SetProperty(x, ReferenceModels.ModelKey, chosen));
        ReferencePicker.Summary(model);

        ImGui.Dummy(Theme.Px(0, 2));
        bool marker = ReferenceModels.Exports(e);
        string node = GlbExporter.EntityNodeName(e);
        if (Toggle($"marker{id}", Icon.MapPin, "Export as marker",
                $"On: exports an empty node called {node} here, to put the real asset on in your engine. Off: editor only", marker))
            EntitySection.Edit(ctx, id, marker ? "Stop Exporting Marker" : "Export Marker",
                x => EntityOps.SetProperty(x, ReferenceModels.MarkerKey, marker ? "0" : "1"));
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
        ImGui.TextWrapped(marker ? $"Exports as the empty node {node}" : "Editor only: not exported");
        ImGui.PopStyleColor();
    }
}
