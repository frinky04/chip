using System.Numerics;
using System.Runtime.CompilerServices;
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
    /// <summary>Per context; <c>Revision</c> is <see cref="ReferenceModels.Revision"/> at build (a model reloading changes its box).</summary>
    static readonly ConditionalWeakTable<EditorContext, Rendering.FrameCache<(int Revision, Dictionary<int, EntityDrawGeometry> Boxes)>> GeometryCache = new();
    static readonly ConditionalWeakTable<EditorContext, EntityVisibilityFrameCache> VisibilityCache = new();

    // Set only by the hidden benchmark to isolate frame submission from P1's ray-casting cost.
    internal static bool BenchmarkSkipOcclusion;

    public static void Draw(EditorContext ctx)
    {
        var doc = ctx.Doc;
        if (doc.Entities.Count == 0) return;
        var layout = ViewportLayout.Of(ctx);
        var panes = layout.Panes;
        int paneCount = ViewportLayout.PaneCount(layout.Kind);
        var min = panes[0].Origin;
        var max = min + panes[0].Size;
        for (int i = 1; i < paneCount; i++)
        {
            min = Vector2.Min(min, panes[i].Origin);
            max = Vector2.Max(max, panes[i].Origin + panes[i].Size);
        }
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
        int? hovered = objectMode && ctx.Hover is { } h && doc.FindEntity(h.Mesh) != null ? h.Mesh : null;
        var geometry = GeometryCache.GetValue(ctx, _ => new Rendering.FrameCache<(int, Dictionary<int, EntityDrawGeometry>)>(selection: false));
        if (!geometry.IsCurrent(ctx.Session) || geometry.Value.Revision != ReferenceModels.DrawRevision)
        {
            var boxes = doc.Entities.ToDictionary(e => e.Id, EntityDrawGeometry.Build);
            geometry.Store(ctx.Session, (ReferenceModels.DrawRevision, boxes)); // read after building: a first load bumps it
        }
        var visibility = VisibilityCache.GetValue(ctx, _ => new EntityVisibilityFrameCache());
        bool occlusion = !BenchmarkSkipOcclusion && doc.Entities.Count <= OcclusionLimit;

        for (int i = 0; i < paneCount; i++)
        {
            var pane = panes[i];
            var view = pane == layout.Active ? ctx.View : pane.View;
            dl.PushClipRect(pane.Origin, pane.Origin + pane.Size, true);
            foreach (var e in doc.Entities)
            {
                if (e.Hidden) continue;
                var look = objectMode && sel.Items.Contains(ElementRef.Object(e.Id)) ? EntityLook.Selected : hovered == e.Id ? EntityLook.Hovered : EntityLook.Normal;
                bool occluded = occlusion && !view.IsOrthographic && !visibility.Get(ctx.Session, view.Eye, ctx.Backfaces, e, ctx.Picks);
                EntityDraw.Draw(dl, pane.Origin, view, e, look, occluded, e.Locked, geometry.Value.Boxes[e.Id]);
            }
            if (pane == layout.Active && ctx.ActiveTool == ctx.DefaultTool && !sel.IsEmpty && sel.Entities(doc).Count > 0 && ctx.SelectionPivot() is { } pivot)
                ctx.Gizmo.Draw(dl, pane.Origin, view, pivot);
            dl.PopClipRect();
        }
        ImGui.End();
    }
}
