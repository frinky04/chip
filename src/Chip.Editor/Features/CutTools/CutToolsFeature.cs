using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.Tools;

namespace Chip.Editor.Features.CutTools;

/// <summary>Stream B: clip (Shift+X), knife (C) and bevel (B), plus a menu command for exact bevels.</summary>
public sealed class CutToolsFeature : IEditorFeature
{
    public void Register(EditorContext ctx)
    {
        var clip = new ClipTool();
        var knife = new KnifeTool();
        var bevel = new BevelTool();
        ctx.AddTool(clip);
        ctx.AddTool(knife);
        ctx.AddTool(bevel);

        ctx.Commands.Add("mesh.bevel", "Bevel selection", () => bevel.Apply(ctx, bevel.Width, bevel.Segments), "Mesh",
            canExecute: () => ctx.Sel.Mode is SelectMode.Edge or SelectMode.Vertex && !ctx.Sel.IsEmpty, group: "4-cut");

        ctx.Commands.AddReference("Tools", "Tab", "clip: keep front, back or both");
        ctx.Commands.AddReference("Tools", "Enter", "clip or knife: apply the cut");
        ctx.Commands.AddReference("Tools", "Backspace", "knife: remove the last point");
        ctx.Commands.AddReference("Tools", "1 – 8", "bevel: segments while dragging");
    }
}
