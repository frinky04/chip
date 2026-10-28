using Chip.Editor.Framework;

namespace Chip.Editor.Features.Logic;

/// <summary>Level logic in the editor: links between entities (references, and later connections) and the eyedropper that picks them.</summary>
public sealed class LogicFeature : IEditorFeature
{
    /// <summary>The one eyedropper; reference fields arm it. Not on the tool strip.</summary>
    public static EntityPickTool Picker { get; } = new();

    public void Register(EditorContext ctx)
    {
        ctx.AddSection(new OutputsSection());
        ctx.AddSection(new LinksSection());
    }
}
