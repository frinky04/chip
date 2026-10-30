using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.Scene;

/// <summary>Level-wide values use the same fields and default handling as entity properties.</summary>
public sealed class WorldProperties : IEditorFeature
{
    public void Register(EditorContext ctx) => ctx.Commands.Add("scene.levelProperties", "Level properties", () => ctx.Sel.Clear(), menu: "Edit", group: "9-level");
}

public sealed class WorldPropertiesFields
{
    readonly ValueFields _fields = new();

    public void Draw(EditorContext ctx)
    {
        if (EntityClasses.Current.World is not { } cls) return;
        SectionDivider();
        ImGui.TextUnformatted(cls.Label);
        if (cls.Doc.Length > 0) Tip(cls.Doc);
        var owner = new Entity { Id = 0, ClassName = cls.Name, Properties = ctx.Doc.World };
        foreach (var p in cls.Props)
        {
            ImGui.PushID(p.Name);
            RowLabel(p.Label, p.Doc);
            _fields.Draw(ctx, owner, p, EntityValues.Effective(owner, p), p.Name, ImGui.GetContentRegionAvail().X,
                value => ctx.Session.Do("Set Level " + p.Label, () => EntityOps.SetProperty(new Entity { Properties = ctx.Doc.World }, p, value)));
            ImGui.PopID();
        }
    }
}
