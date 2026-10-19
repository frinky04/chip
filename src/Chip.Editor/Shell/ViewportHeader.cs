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

    void DrawViewportHeader(float width)
    {
        float h = HeaderHeight;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.Bg); // chrome, like Krita's toolbar
        // Taller controls than the inspector's: the header is the editor's main toolbar.
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(ImGui.GetStyle().FramePadding.X, Theme.Px(6)));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Theme.Px(8), MathF.Floor((h - ImGui.GetFrameHeight()) * 0.5f)));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Theme.Px(2, 0));
        ImGui.BeginChild("##vpheader", new Vector2(width, h), ImGuiChildFlags.AlwaysUseWindowPadding, ImGuiWindowFlags.NoScrollbar);

        int picked = SegmentedControl("mode", ModeItems, Array.IndexOf(HeaderModes, Ctx.Sel.Mode));
        if (picked >= 0) Ctx.SetMode(HeaderModes[picked]);
        VSeparator();

        // The active pane's projection and shading; in multi-pane layouts each pane also has its own corner menu.
        var layout = ViewportLayout.Of(Ctx);
        var pane = layout.Active;
        if (Dropdown("projection", null, pane.Name, "Perspective", "Projection of the active view  (Alt+1 to Alt+4)"))
        {
            ProjectionItems(layout, pane);
            ImGui.EndPopup();
        }
        ImGui.SameLine(0, Theme.Px(4));
        if (Dropdown("shading", null, ViewportPane.ModeLabel(pane.Mode), "UV checker", "Shading of the active view  (Alt+Z cycles)"))
        {
            ShadingItems(pane);
            ImGui.EndPopup();
        }
        ImGui.SameLine(0, Theme.Px(4));
        var gizmo = Ctx.Gizmo;
        if (Dropdown("axes", Icon.Move, OrientationLabel(gizmo.Orientation), "World", "Gizmo axes: world, local (the selection's main face) or view"))
        {
            foreach (var o in Enum.GetValues<GizmoOrientation>())
                if (ImGui.MenuItem(OrientationLabel(o), null, gizmo.Orientation == o)) gizmo.Orientation = o;
            ImGui.EndPopup();
        }

        // Right group: grid, snap, backfaces. Measured so it right-aligns exactly.
        var style = ImGui.GetStyle();
        string gridText = Labels.Length(Ctx.Grid);
        float slot = ImGui.CalcTextSize("3.13 cm").X;
        float Btn(string t) => ImGui.CalcTextSize(t).X + style.FramePadding.X * 2;
        float ToggleW(string label) => Theme.Px(8) * 2 + Fonts.IconSize + Theme.Px(6) + ImGui.CalcTextSize(label).X;
        float gap = Theme.Px(6);
        bool dressing = Ctx.Dressed() != null; // the toggle only exists for levels with a rules file
        float rightW = ImGui.CalcTextSize("Grid").X + gap + Btn("-") + gap + slot + gap + Btn("+") + Theme.Px(12)
            + ToggleW("Snap") + Theme.Px(2) + ToggleW("Backfaces") + (dressing ? Theme.Px(2) + ToggleW("Dressed") : 0);
        ImGui.SameLine(MathF.Max(width - style.WindowPadding.X - rightW, ImGui.GetCursorPosX() + Theme.Px(12)));
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextDim, "Grid");
        ImGui.SameLine(0, gap);
        if (ImGui.Button("-")) Ctx.StepGrid(-1);
        Tip("Smaller grid  ([)");
        ImGui.SameLine(0, gap);
        float pad = (slot - ImGui.CalcTextSize(gridText).X) * 0.5f;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + pad);
        ImGui.Text(gridText);
        ImGui.SameLine(0, gap + pad);
        if (ImGui.Button("+")) Ctx.StepGrid(1);
        Tip("Larger grid  (])");
        ImGui.SameLine(0, Theme.Px(12));
        if (Toggle("snap", Icon.Snap, "Snap", Ctx.Snap ? "Snap to grid is on. Hold Ctrl while dragging to bypass" : "Snap to grid is off", Ctx.Snap)) Ctx.Snap = !Ctx.Snap;
        ImGui.SameLine();
        if (Toggle("backfaces", Icon.Backfaces, "Backfaces", "Select faces that point away from you  (Alt+B)", Ctx.Backfaces)) Ctx.Commands.Execute("view.backfaces");
        if (dressing)
        {
            ImGui.SameLine();
            if (Toggle("dressed", Icon.Eye, "Dressed", Ctx.ShowDressed ? "Showing the dressed level; you still edit the blockout  (`)" : "Showing the bare blockout. Export still includes the dressing  (`)", Ctx.ShowDressed))
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
    static bool Dropdown(string id, Icon? icon, string text, string widest, string tip)
    {
        float h = ImGui.GetFrameHeight();
        float pad = Theme.Px(8), chevron = Theme.Px(14), iconW = icon != null ? Fonts.IconSize + Theme.Px(6) : 0;
        var size = new Vector2(pad + iconW + MathF.Max(ImGui.CalcTextSize(widest).X, ImGui.CalcTextSize(text).X) + pad + chevron, h);
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
