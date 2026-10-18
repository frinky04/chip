using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.Tools;
using Chip.Editor.Viewport;

namespace Chip.Editor.Features.Base;

/// <summary>Default tools, selection modes, gizmo modes, grid, snap, backfaces, focus, shortcuts sheet.</summary>
public sealed class ViewFeature : IEditorFeature
{
    public void Register(EditorContext ctx)
    {
        ctx.AddTool(new SelectTool());
        ctx.AddTool(new BlockTool());
        Navigator.RegisterReferences(ctx.Commands);

        var c = ctx.Commands;
        (SelectMode Mode, string Key)[] modes = [(SelectMode.Vertex, "1"), (SelectMode.Edge, "2"), (SelectMode.Face, "3"), (SelectMode.Object, "4")];
        foreach (var (mode, key) in modes)
            c.Add($"select.mode{mode}", $"{mode.Name()} mode", () => ctx.SetMode(mode), "Select", key, group: "0-mode",
                order: (int)mode, isChecked: () => ctx.Sel.Mode == mode);

        (GizmoMode Mode, string Label, string Key)[] gizmos = [(GizmoMode.Translate, "Move", "W"), (GizmoMode.Rotate, "Rotate", "E"), (GizmoMode.Scale, "Scale", "R")];
        foreach (var (mode, label, key) in gizmos)
            c.Add(new Command
            {
                Id = $"tool.gizmo{mode}", Label = label, Menu = "Tools", Group = "2-transform", Order = (int)mode,
                Hotkeys = [Hotkey.Parse(key)], IsChecked = () => ctx.ActiveTool == ctx.DefaultTool && ctx.Gizmo.Mode == mode,
                Description = mode == GizmoMode.Translate ? "Hold Shift on faces to extrude" : null,
                Execute = () =>
                {
                    ctx.Gizmo.Mode = mode;
                    if (ctx.DefaultTool != null) ctx.SetTool(ctx.DefaultTool);
                },
            });
        c.AddReference("Tools", "Shift + drag", "extrude faces (move gizmo)");
        c.AddReference("Tools", "Enter / Esc", "confirm / cancel");

        c.Add("view.focus", "Focus selection", ctx.Focus, "View", "F", group: "1-camera", order: 0);
        c.Add("view.frameAll", "Frame everything", ctx.FrameAll, "View", group: "1-camera", order: 1);
        c.Add("view.gridSmaller", "Smaller grid", () => ctx.StepGrid(-1), "View", "[", group: "2-grid", order: 0);
        c.Add("view.gridLarger", "Larger grid", () => ctx.StepGrid(1), "View", "]", group: "2-grid", order: 1);
        c.Add("view.snap", "Snap to grid", () => ctx.Snap = !ctx.Snap, "View", group: "2-grid", order: 2, isChecked: () => ctx.Snap);
        c.Add("view.backfaces", "Select backfaces", () =>
        {
            ctx.Backfaces = !ctx.Backfaces;
            ctx.Status(ctx.Backfaces ? "Backface selection on: faces pointing away can be picked" : "Backface selection off");
        }, "View", "Alt+B", group: "3-picking", isChecked: () => ctx.Backfaces);

        c.Add("help.shortcuts", "Keyboard shortcuts", () => ctx.ShowShortcuts = true, "Help", "F1");
    }
}
