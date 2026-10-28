using Chip.Editor.Framework;

namespace Chip.Editor.Features.Logic;

/// <summary>
/// Level logic in the editor: the Entity IO tables in the inspector, the Wiring window over the whole level, the links
/// between entities (references and connections), and the eyedropper that picks them.
/// </summary>
public sealed class LogicFeature : IEditorFeature
{
    /// <summary>The one eyedropper; reference fields arm it. Not on the tool strip.</summary>
    public static EntityPickTool Picker { get; } = new();

    public void Register(EditorContext ctx)
    {
        ctx.AddSection(new EntityIoSection());
        ctx.AddSection(new LinksSection());
        var wiring = new WiringWindow();
        ctx.AddPanel(wiring.Draw);
        ctx.Commands.Add("logic.wiring", "Wiring…", () => wiring.Open = !wiring.Open, "View", "Ctrl+Shift+I",
            group: "5-panels", order: 4, isChecked: () => wiring.Open);
    }
}
