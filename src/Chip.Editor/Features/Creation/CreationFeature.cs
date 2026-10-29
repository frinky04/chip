using Chip.Core.Geometry.Primitives;
using Chip.Editor.Framework;
using Chip.Editor.Tools;

namespace Chip.Editor.Features.Creation;

/// <summary>
/// Creation tools beyond the block: Add Rectangle (Shift+R) and the polygon tool (Shift+P), plus one command per block
/// shape (palette and scripts: <c>block.shapeStairs</c> picks stairs and switches to the block tool).
/// </summary>
public sealed class CreationFeature : IEditorFeature
{
    public void Register(EditorContext ctx)
    {
        ctx.AddTool(new RectangleTool());
        ctx.AddTool(new PolygonTool());

        BlockTool? Block() => ctx.Tools.OfType<BlockTool>().FirstOrDefault();
        foreach (var shape in BlockTool.ShapeList)
        {
            ctx.Commands.Add(new Command
            {
                Id = $"block.shape{shape}",
                Label = $"Block shape: {BlockTool.Label(shape).ToLowerInvariant()}",
                Category = "Tools",
                Description = "Draw this shape with the block tool",
                Execute = () =>
                {
                    if (Block() is not { } tool) return;
                    tool.Shape = shape;
                    ctx.SetTool(tool);
                },
                CanExecute = () => Block() != null,
                IsChecked = () => Block()?.Shape == shape,
            });
        }
        ctx.Commands.AddReference("Tools", "Shift + drag", "draw on a plane facing you");
        ctx.Commands.AddReference("Tools", "Ctrl", "bypass snapping");
    }
}
