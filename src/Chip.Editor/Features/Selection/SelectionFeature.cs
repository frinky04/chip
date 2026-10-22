using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.Tools;

namespace Chip.Editor.Features.SelectionTools;

/// <summary>
/// Selection power tools (SPEC §4): edge loop/ring, grow/shrink, select connected, select by material, the visible-only box
/// select toggle and session selection sets. Double-click flood and Ctrl+Shift path select live in <see cref="SelectTool"/>.
/// </summary>
public sealed class SelectionFeature : IEditorFeature
{
    public void Register(EditorContext ctx)
    {
        var c = ctx.Commands;
        var sets = SelectionSets.For(ctx); // drawn under the outliner, in the Scene tab
        var tool = ctx.Tools.OfType<SelectTool>().FirstOrDefault();
        bool EdgesSelected() => ctx.Sel.Mode == SelectMode.Edge && !ctx.Sel.IsEmpty;
        bool ElementsSelected() => ctx.Sel.Mode != SelectMode.Object && !ctx.Sel.IsEmpty;

        c.Add("select.loop", "Select edge loop", () => Report(ctx, SelectionOps.SelectLoops(ctx.Doc, ctx.Sel),
                "No loop to extend: the edges end at poles, n-gons or borders"),
            "Select", "L", EdgesSelected, "2-topology", 0);
        c.Add("select.ring", "Select edge ring", () => Report(ctx, SelectionOps.SelectRings(ctx.Doc, ctx.Sel),
                "No ring to extend: the edges sit between n-gons, triangles or borders"),
            "Select", "X", EdgesSelected, "2-topology", 1);
        c.Add("select.grow", "Grow selection", () => SelectionOps.Grow(ctx.Doc, ctx.Sel), "Select", "Alt+= / Alt+NumPlus", ElementsSelected, "2-topology", 2);
        c.Add("select.shrink", "Shrink selection", () => SelectionOps.Shrink(ctx.Doc, ctx.Sel), "Select", "Alt+- / Alt+NumMinus", ElementsSelected, "2-topology", 3);
        c.Add("select.connected", "Select connected", () => SelectionOps.SelectConnected(ctx.Doc, ctx.Sel), "Select", null, ElementsSelected, "2-topology", 4);
        c.Add("select.byMaterial", "Select by material", () => ByMaterial(ctx), "Select", group: "3-match");

        if (tool != null)
            c.Add("select.visibleOnly", "Box select visible only", () =>
            {
                tool.VisibleOnly = !tool.VisibleOnly;
                ctx.Status(tool.VisibleOnly ? "Box select now skips elements hidden behind surfaces" : "Box select now selects through for vertices and edges");
            }, "Select", group: "4-options", isChecked: () => tool.VisibleOnly);

        c.Add("select.saveSet", "Save selection set", () => sets.Save(ctx, null), "Select", null, () => !ctx.Sel.IsEmpty, "5-sets", 0);
        c.Add(new Command
        {
            Id = "select.restoreSet", Label = "Restore selection set", Menu = "Select", Group = "5-sets", Order = 1,
            Execute = () => sets.Restore(ctx, sets.Last!), CanExecute = () => sets.Last != null,
            DisplayLabel = () => sets.Last is { } s ? $"Restore {s.Name}" : "Restore selection set",
        });

        c.AddReference("Select", "drag", "paint select: everything you sweep over (replaces)");
        c.AddReference("Select", "Shift + drag", "paint into the selection");
        c.AddReference("Select", "Ctrl + drag", "paint out of the selection");
        c.AddReference("Select", "Ctrl+Alt + drag", "box select (with Shift: add)");
        c.AddReference("Select", "Double-click", "connected faces / edge loop / connected vertices");
        c.AddReference("Select", "CapsLock + double-click", "faces on the plane (or Mouse4 / Mouse5)");
        c.AddReference("Select", "Ctrl+Shift + click", "shortest path from the active element");
    }

    static void Report(EditorContext ctx, int added, string none) =>
        ctx.Status(added > 0 ? $"Selected {ctx.Sel.Mode.Count(ctx.Sel.Count)}" : none);

    /// <summary>Faces using the active face's material, else the active material.</summary>
    static void ByMaterial(EditorContext ctx)
    {
        var material = SelectionOps.ActiveFaceMaterial(ctx.Doc, ctx.Sel) ?? ctx.ActiveMaterial;
        int n = SelectionOps.SelectByMaterial(ctx.Doc, ctx.Sel, material);
        ctx.Status(n > 0 ? $"Selected {SelectMode.Face.Count(n)} using {material}" : $"No visible faces use {material}");
    }
}
