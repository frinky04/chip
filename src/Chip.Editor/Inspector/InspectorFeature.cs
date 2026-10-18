using Chip.Editor.Framework;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Inspector;

/// <summary>The built-in inspector sections.</summary>
public sealed class InspectorFeature : IEditorFeature
{
    public void Register(EditorContext ctx)
    {
        ctx.AddSection(new ToolOptionsSection());
        ctx.AddSection(new SelectionSection());
        ctx.AddSection(new MaterialSection());
        ctx.AddSection(new TextureSection());
        ctx.AddSection(new ExportSection());
    }
}

/// <summary>The active tool's options, when it has any.</summary>
public sealed class ToolOptionsSection : IInspectorSection
{
    public string Id => "tool";
    public int Order => 0;
    public bool Visible(EditorContext ctx) => ctx.ActiveTool?.HasOptions == true;

    public void Draw(EditorContext ctx)
    {
        Heading(ctx.ActiveTool!.Name);
        ctx.ActiveTool.DrawOptions(ctx);
    }
}
