using System.Numerics;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using Chip.Editor.Viewport;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor;

/// <summary>
/// Viewport header: how you look and what you select. Selection mode, the active pane's projection and shading,
/// gizmo axes, then grid, snap and backfaces on the right.
/// </summary>
public sealed partial class EditorApp
{
    static float HeaderHeight => Theme.Px(44);

    static readonly SelectMode[] HeaderModes = [SelectMode.Vertex, SelectMode.Edge, SelectMode.Face, SelectMode.Object];

    static readonly (Icon, string, string)[] ModeItems =
    [
        (Icon.Vertex, "Vertex", "Select vertices  (1)"), (Icon.Edge, "Edge", "Select edges  (2)"),
        (Icon.Face, "Face", "Select faces  (3)"), (Icon.Object, "Object", "Select objects  (4)"),
    ];

    static readonly Projection[] Projections = [Projection.Perspective, Projection.Top, Projection.Front, Projection.Side];
    static readonly ViewMode[] ViewModes = [ViewMode.Lit, ViewMode.Unlit, ViewMode.Wireframe, ViewMode.UvChecker];

    /// <summary>
    /// How much the header gives up to fit its width: 0 everything labelled; 1 selection modes and toggles icon-only (names
    /// in the tooltips); 2 dropdowns as wide as their value, gizmo axes icon-only, and snap space moved into a ⋯ menu;
    /// 3 projection, shading and axes in that menu too; 4 the grid as a size menu, and backfaces and dressed in the ⋯ menu.
    /// </summary>
    int HeaderLevel(float width)
    {
        var style = ImGui.GetStyle();
        var pane = ViewportLayout.Of(Ctx).Active;
        float between = Theme.Px(4), separator = Theme.Px(17);
        for (int level = 0; level < 4; level++)
        {
            bool labels = level == 0, fitted = level >= 2, folded = level >= 3;
            float left = SegmentedWidth(labels ? ModeItems : ModeItems.Select(m => (m.Item1, "", m.Item3)).ToArray()) + separator
                + (folded ? 0 : DropdownWidth(null, pane.Name, fitted ? pane.Name : "Perspective") + between
                    + DropdownWidth(null, ViewportPane.ModeLabel(pane.Mode), fitted ? ViewportPane.ModeLabel(pane.Mode) : "UV checker") + between)
                + (fitted ? (folded ? 0 : DropdownWidth(Icon.Move, "", "") + between) + DropdownWidth(Icon.Dots, "", "")
                    : DropdownWidth(Icon.Move, OrientationLabel(Ctx.Gizmo.Orientation), "World"));
            if (style.WindowPadding.X * 2 + left + Theme.Px(12) + HeaderRightWidth(level) <= width) return level;
        }
        return 4;
    }

    /// <summary>The right group's width at a <see cref="HeaderLevel"/>, so it right-aligns exactly.</summary>
    float HeaderRightWidth(int level)
    {
        var style = ImGui.GetStyle();
        bool labels = level == 0, fitted = level >= 2, tight = level >= 4;
        bool dressing = Ctx.Dressed() != null; // the toggle only exists for levels with a rules file
        float Btn(string t) => ImGui.CalcTextSize(t).X + style.FramePadding.X * 2;
        float gap = Theme.Px(6);
        float grid = tight ? DropdownWidth(null, Labels.Length(Ctx.Grid), "50 cm")
            : (labels ? ImGui.CalcTextSize("Grid").X + gap : 0) + Btn("-") + gap + ImGui.CalcTextSize("50 cm").X + gap + Btn("+");
        return grid + Theme.Px(12) + ToggleWidth(labels ? "Snap" : "")
            + (fitted ? 0 : Theme.Px(2) + DropdownWidth(null, "Relative", "Relative"))
            + (tight ? 0 : Theme.Px(2) + ToggleWidth(labels ? "Backfaces" : "") + (dressing ? Theme.Px(2) + ToggleWidth(labels ? "Dressed" : "") : 0));
    }

    void DrawViewportHeader(float width)
    {
        float h = HeaderHeight;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.Bg); // chrome, like Krita's toolbar
        // Taller controls than the inspector's: the header is the editor's main toolbar.
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(ImGui.GetStyle().FramePadding.X, Theme.Px(6)));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Theme.Px(8), MathF.Floor((h - ImGui.GetFrameHeight()) * 0.5f)));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Theme.Px(2, 0));
        ImGui.BeginChild("##vpheader", new Vector2(width, h), ImGuiChildFlags.AlwaysUseWindowPadding, ImGuiWindowFlags.NoScrollbar);
        int level = HeaderLevel(width);
        bool labels = level == 0, fitted = level >= 2, folded = level >= 3, tight = level >= 4;
        bool dressing = Ctx.Dressed() != null; // the toggle only exists for levels with a rules file

        var modeItems = labels ? ModeItems : ModeItems.Select(m => (m.Item1, "", m.Item3)).ToArray();
        int picked = SegmentedControl("mode", modeItems, Array.IndexOf(HeaderModes, Ctx.Sel.Mode));
        if (picked >= 0) Ctx.SetMode(HeaderModes[picked]);
        VSeparator();

        // The active pane's projection and shading; in multi-pane layouts each pane also has its own corner menu.
        var layout = ViewportLayout.Of(Ctx);
        var pane = layout.Active;
        var gizmo = Ctx.Gizmo;
        if (!folded)
        {
            if (Dropdown("projection", null, pane.Name, fitted ? pane.Name : "Perspective", "Projection of the active view  (Alt+1 to Alt+4)"))
            {
                ProjectionItems(layout, pane);
                ImGui.EndPopup();
            }
            ImGui.SameLine(0, Theme.Px(4));
            string shading = ViewportPane.ModeLabel(pane.Mode);
            if (Dropdown("shading", null, shading, fitted ? shading : "UV checker", "Shading of the active view  (Alt+Z cycles)"))
            {
                ShadingItems(pane);
                ImGui.EndPopup();
            }
            ImGui.SameLine(0, Theme.Px(4));
            string axes = OrientationLabel(gizmo.Orientation);
            if (Dropdown("axes", Icon.Move, fitted ? "" : axes, fitted ? "" : "World", $"Gizmo axes: {axes.ToLowerInvariant()}. World, local (the selection's main face) or view"))
            {
                AxesItems();
                ImGui.EndPopup();
            }
        }
        if (fitted)
        {
            if (!folded) ImGui.SameLine(0, Theme.Px(4));
            if (Dropdown("more", Icon.Dots, "", "", tight ? "Projection, shading, gizmo axes, snap space, backfaces and dressed view"
                    : folded ? "Projection, shading, gizmo axes and snap space" : "Snap space"))
            {
                if (folded)
                {
                    ImGui.TextColored(Theme.TextDim, "Projection");
                    ProjectionItems(layout, pane);
                    ImGui.Separator();
                    ImGui.TextColored(Theme.TextDim, "Shading");
                    ShadingItems(pane);
                    ImGui.Separator();
                    ImGui.TextColored(Theme.TextDim, "Gizmo axes");
                    AxesItems();
                    ImGui.Separator();
                }
                ImGui.TextColored(Theme.TextDim, "Snap to");
                SnapSpaceItems();
                if (tight)
                {
                    ImGui.Separator();
                    if (ImGui.MenuItem("Select backfaces", "Alt+B", Ctx.Backfaces)) Ctx.Commands.Execute("view.backfaces");
                    if (dressing && ImGui.MenuItem("Dressed view", "`", Ctx.ShowDressed)) Ctx.Commands.Execute("view.dressed");
                }
                ImGui.EndPopup();
            }
        }

        // Right group: grid, snap, backfaces, dressed.
        var style = ImGui.GetStyle();
        string gridText = Labels.Length(Ctx.Grid);
        float slot = ImGui.CalcTextSize("50 cm").X;
        float gap = Theme.Px(6);
        string spaceText = Ctx.SnapSpace == SnapSpace.World ? "World" : "Relative";
        float leftEnd = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X; // the cursor has already wrapped to a new line
        ImGui.SameLine(MathF.Max(width - style.WindowPadding.X - HeaderRightWidth(level), leftEnd + Theme.Px(12)));
        ImGui.AlignTextToFramePadding();
        if (tight)
        {
            if (Dropdown("grid", null, gridText, "50 cm", "Grid size  ([ smaller, ] larger)"))
            {
                foreach (float g in EditorContext.GridSizes)
                    if (ImGui.MenuItem(Labels.Length(g), null, MathF.Abs(g - Ctx.Grid) < 1e-5f)) Ctx.Grid = g;
                ImGui.EndPopup();
            }
        }
        else
        {
            if (labels)
            {
                ImGui.TextColored(Theme.TextDim, "Grid");
                ImGui.SameLine(0, gap);
            }
            if (ImGui.Button("-")) Ctx.StepGrid(-1);
            Tip("Smaller grid  ([)");
            ImGui.SameLine(0, gap);
            float pad = (slot - ImGui.CalcTextSize(gridText).X) * 0.5f;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + pad);
            ImGui.Text(gridText);
            ImGui.SameLine(0, gap + pad);
            if (ImGui.Button("+")) Ctx.StepGrid(1);
            Tip("Larger grid  (])");
        }
        ImGui.SameLine(0, Theme.Px(12));
        string snapTip = Ctx.Snap ? "Snap to grid is on. Hold Ctrl while dragging to bypass" : "Snap to grid is off";
        if (fitted) snapTip += $"\nSnapping to {(Ctx.SnapSpace == SnapSpace.World ? "the world grid" : "whole grid steps")}  (\\ switches)";
        if (Toggle("snap", Icon.Snap, labels ? "Snap" : "", snapTip, Ctx.Snap)) Ctx.Snap = !Ctx.Snap;
        if (!fitted)
        {
            ImGui.SameLine(0, Theme.Px(2));
            if (Dropdown("snapspace", null, spaceText, "Relative",
                    "What moves snap to  (\\)\nWorld: the selection lands on the grid lines\nRelative: it moves in whole grid steps from where it was"))
            {
                SnapSpaceItems();
                ImGui.EndPopup();
            }
        }
        if (!tight)
        {
            ImGui.SameLine();
            if (Toggle("backfaces", Icon.Backfaces, labels ? "Backfaces" : "", "Select faces that point away from you  (Alt+B)", Ctx.Backfaces)) Ctx.Commands.Execute("view.backfaces");
        }
        if (dressing && !tight)
        {
            ImGui.SameLine();
            if (Toggle("dressed", Icon.Eye, labels ? "Dressed" : "", Ctx.ShowDressed ? "Showing the dressed level; you still edit the blockout  (`)" : "Showing the bare blockout. Export still includes the dressing  (`)", Ctx.ShowDressed))
                Ctx.Commands.Execute("view.dressed");
        }

        ImGui.EndChild();
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor();

        // Hairline under the header so the 3D view reads as a separate surface.
        var p = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddLine(p, p + new Vector2(width, 0), ImGui.ColorConvertFloat4ToU32(Theme.Border));
    }

    static string OrientationLabel(GizmoOrientation o) => o switch
    {
        GizmoOrientation.Local => "Local", GizmoOrientation.View => "View", _ => "World",
    };

    void ProjectionItems(ViewportLayout layout, ViewportPane pane)
    {
        for (int i = 0; i < Projections.Length; i++)
            if (ImGui.MenuItem(Camera.Label(Projections[i]), $"Alt+{i + 1}", pane.Projection == Projections[i]))
                layout.SetProjection(pane, Projections[i]);
    }

    static void ShadingItems(ViewportPane pane)
    {
        foreach (var m in ViewModes)
            if (ImGui.MenuItem(ViewportPane.ModeLabel(m), null, pane.Mode == m)) pane.Mode = m;
    }

    void AxesItems()
    {
        foreach (var o in Enum.GetValues<GizmoOrientation>())
            if (ImGui.MenuItem(OrientationLabel(o), null, Ctx.Gizmo.Orientation == o)) Ctx.Gizmo.Orientation = o;
    }

    void SnapSpaceItems()
    {
        if (ImGui.MenuItem("World grid", null, Ctx.SnapSpace == SnapSpace.World)) Ctx.SnapSpace = SnapSpace.World;
        if (ImGui.MenuItem("Relative steps", null, Ctx.SnapSpace == SnapSpace.Relative)) Ctx.SnapSpace = SnapSpace.Relative;
    }

    /// <summary>Corner menu of a pane in a multi-pane layout: projection, then shading.</summary>
    void ViewMenuItems(ViewportLayout layout, ViewportPane pane)
    {
        ProjectionItems(layout, pane);
        ImGui.Separator();
        ShadingItems(pane);
    }

    /// <summary>
    /// A compact menu button: current value, a chevron, and a popup under it. Returns true while the popup is open;
    /// the caller draws items and calls <c>ImGui.EndPopup</c>. <paramref name="widest"/> fixes the width so the header doesn't jump.
    /// </summary>
    static float DropdownWidth(Icon? icon, string text, string widest)
    {
        float pad = Theme.Px(8), chevron = Theme.Px(14);
        float iconW = icon != null ? Fonts.IconSize + (text.Length > 0 || widest.Length > 0 ? Theme.Px(6) : 0) : 0;
        return pad + iconW + MathF.Max(ImGui.CalcTextSize(widest).X, ImGui.CalcTextSize(text).X) + pad + chevron;
    }

    static bool Dropdown(string id, Icon? icon, string text, string widest, string tip)
    {
        float h = ImGui.GetFrameHeight();
        float pad = Theme.Px(8), iconW = icon != null ? Fonts.IconSize + Theme.Px(6) : 0;
        var size = new Vector2(DropdownWidth(icon, text, widest), h);
        var p = ImGui.GetCursorScreenPos();
        string popup = $"##{id}menu";
        bool clicked = ImGui.InvisibleButton($"##{id}", size);
        bool hovered = ImGui.IsItemHovered();
        bool open = ImGui.IsPopupOpen(popup);
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(p, p + size, ImGui.ColorConvertFloat4ToU32(hovered || open ? Theme.FrameHover : Theme.Frame), Theme.Px(3));
        uint fg = ImGui.ColorConvertFloat4ToU32(hovered || open ? Theme.Text : Theme.TextDim);
        if (icon is { } ic) Icons.Draw(dl, ic, new Vector2(p.X + pad + Fonts.IconSize * 0.5f, p.Y + h * 0.5f), fg);
        dl.AddText(new Vector2(p.X + pad + iconW, MathF.Round(p.Y + (h - ImGui.GetTextLineHeight()) * 0.5f)), ImGui.ColorConvertFloat4ToU32(Theme.Text), text);
        Chevron(dl, new Vector2(p.X + size.X - pad - Theme.Px(3), p.Y + h * 0.5f), fg);
        if (!open) Tip(tip);
        if (clicked) ImGui.OpenPopup(popup);
        ImGui.SetNextWindowPos(new Vector2(p.X, p.Y + h + Theme.Px(2)), ImGuiCond.Appearing);
        return ImGui.BeginPopup(popup);
    }
}
