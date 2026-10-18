using System.Numerics;
using Chip.Core.Picking;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.Viewport;
using ImGuiNET;

namespace Chip.Editor.Features.Scene;

/// <summary>
/// Draws entities in every visible viewport pane, whatever tool is active. The renderer only knows meshes, so this is a
/// per-frame panel: a transparent, input-less window over the viewport area (floating panels opened later stack above
/// it). In the active pane it redraws the gizmo on top when entities are selected, so their glyphs never cover its handles.
/// </summary>
public static class EntityOverlay
{
    const ImGuiWindowFlags Flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoBackground |
        ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoMove;
    // Not NoBringToFrontOnFocus: ImGui puts such windows at the back of the stack, under the viewport. NoInputs already
    // keeps this one from ever taking focus, so it stays just above the viewport and below panels opened later.

    /// <summary>Occlusion tests cast a ray per entity per pane; past this many they are skipped (everything draws bright).</summary>
    const int OcclusionLimit = 400;

    public static void Draw(EditorContext ctx)
    {
        var doc = ctx.Doc;
        if (doc.Entities.Count == 0) return;
        var layout = ViewportLayout.Of(ctx);
        var panes = layout.Visible.ToList();
        var min = panes.Select(p => p.Origin).Aggregate(Vector2.Min);
        var max = panes.Select(p => p.Origin + p.Size).Aggregate(Vector2.Max);
        if (max.X - min.X < 2 || max.Y - min.Y < 2) return;

        ImGui.SetNextWindowPos(min);
        ImGui.SetNextWindowSize(max - min);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.Begin("##entityOverlay", Flags);
        ImGui.PopStyleVar(2);
        var dl = ImGui.GetWindowDrawList();

        var sel = ctx.Sel;
        bool objectMode = sel.Mode == SelectMode.Object;
        var selected = objectMode ? sel.MeshIds() : [];
        int? hovered = objectMode && ctx.Hover is { } h && doc.FindEntity(h.Mesh) != null ? h.Mesh : null;
        bool occlusion = doc.Entities.Count <= OcclusionLimit;

        foreach (var pane in panes)
        {
            var view = pane == layout.Active ? ctx.View : pane.View;
            dl.PushClipRect(pane.Origin, pane.Origin + pane.Size, true);
            foreach (var e in doc.Entities)
            {
                if (e.Hidden) continue;
                var look = selected.Contains(e.Id) ? EntityLook.Selected : hovered == e.Id ? EntityLook.Hovered : EntityLook.Normal;
                bool occluded = occlusion && !view.IsOrthographic && !Visibility.IsVisible(doc, view.Eye, EntityOps.Center(e), ctx.Backfaces, ctx.Picks);
                EntityDraw.Draw(dl, pane.Origin, view, e, look, occluded, e.Locked);
            }
            if (pane == layout.Active && ctx.ActiveTool == ctx.DefaultTool && sel.Entities(doc).Count > 0 && ctx.SelectionPivot() is { } pivot)
                ctx.Gizmo.Draw(dl, pane.Origin, view, pivot);
            dl.PopClipRect();
        }
        ImGui.End();
    }
}
