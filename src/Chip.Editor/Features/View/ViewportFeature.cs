using Chip.Editor.Framework;
using Chip.Editor.UI;
using Chip.Editor.Viewport;

namespace Chip.Editor.Features.View;

/// <summary>
/// Views and navigation (stream G): projections (Alt+1..4), layouts, shading modes (Alt+Z), boundary edges, gizmo axes,
/// rotation snap, camera bookmarks (Ctrl+Shift+1..9 store, Ctrl+1..9 recall), and the gizmo hooks for the temporary
/// pivot and CapsLock drag alignment.
/// </summary>
public sealed class ViewportFeature : IEditorFeature
{
    static readonly float[] RotationSteps = [5, 15, 45, 90];

    string? _bookmarksFor;

    public void Register(EditorContext ctx)
    {
        var c = ctx.Commands;
        ViewportLayout Layout() => ViewportLayout.Of(ctx);

        // Gizmo hooks: the select tool's existing drag path picks these up without changes.
        ctx.Gizmo.PivotHook = () => ctx.PivotOverride;
        ctx.Gizmo.SnapProvider = (view, mouse, excludeSelection) =>
            DragAlign.Find(ctx.Doc, view, mouse, excludeSelection ? ctx.Sel.VertexSets(ctx.Doc) : null, Theme.Scale);

        (LayoutKind Kind, string Label)[] layouts = [(LayoutKind.Single, "Single view"), (LayoutKind.Two, "Two views"), (LayoutKind.Four, "Four views")];
        for (int i = 0; i < layouts.Length; i++)
        {
            var (kind, label) = layouts[i];
            c.Add($"view.layout{kind}", label, () => Layout().SetKind(kind), "View", group: "0-layout", order: i,
                isChecked: () => Layout().Kind == kind);
        }

        Projection[] projections = [Projection.Perspective, Projection.Top, Projection.Front, Projection.Side];
        for (int i = 0; i < projections.Length; i++)
        {
            var p = projections[i];
            c.Add(new Command
            {
                Id = $"view.{p.ToString().ToLowerInvariant()}", Label = Camera.Label(p), Menu = "View", Group = "0-projection", Order = i,
                Hotkeys = [Hotkey.Parse($"Alt+{i + 1}")],
                Description = p == Projection.Perspective ? "Switch the active view to perspective" : $"Switch the active view to the orthographic {Camera.Label(p).ToLowerInvariant()} view",
                Execute = () => Layout().SetProjection(Layout().Active, p),
                IsChecked = () => Layout().Active.Projection == p,
            });
        }

        ViewMode[] modes = [ViewMode.Lit, ViewMode.Unlit, ViewMode.Wireframe, ViewMode.UvChecker];
        for (int i = 0; i < modes.Length; i++)
        {
            var m = modes[i];
            c.Add($"view.mode{m}", ViewportPane.ModeLabel(m), () => Layout().Active.Mode = m, "View", group: "4-shading", order: i,
                isChecked: () => Layout().Active.Mode == m);
        }
        c.Add("view.cycleMode", "Next shading mode", () =>
        {
            var pane = Layout().Active;
            pane.Mode = modes[(Array.IndexOf(modes, pane.Mode) + 1) % modes.Length];
            ctx.Status($"{pane.Name} view: {ViewportPane.ModeLabel(pane.Mode).ToLowerInvariant()}");
        }, "View", "Alt+Z", group: "4-shading", order: 10);
        c.Add(new Command
        {
            Id = "view.boundaryEdges", Label = "Dashed open edges", Menu = "View", Group = "4-shading", Order = 11,
            Description = "Draw boundary edges (used by one face) dashed in cyan",
            Execute = () => ViewSettings.Of(ctx).BoundaryEdges = !ViewSettings.Of(ctx).BoundaryEdges,
            IsChecked = () => ViewSettings.Of(ctx).BoundaryEdges,
        });

        for (int i = 0; i < RotationSteps.Length; i++)
        {
            float step = RotationSteps[i];
            c.Add($"view.rotationSnap{step:0}", $"Rotation snap {step:0}°", () => ctx.Gizmo.RotateSnapDegrees = step, "View",
                group: "2-grid", order: 10 + i, isChecked: () => ctx.Gizmo.RotateSnapDegrees == step);
        }

        (GizmoOrientation O, string Label, string Desc)[] axes =
        [
            (GizmoOrientation.World, "World axes", "Gizmo follows the world axes"),
            (GizmoOrientation.Local, "Local axes", "Gizmo follows the selection's main face"),
            (GizmoOrientation.View, "View axes", "Gizmo follows the camera"),
        ];
        for (int i = 0; i < axes.Length; i++)
        {
            var (o, label, desc) = axes[i];
            c.Add(new Command
            {
                Id = $"tool.axes{o}", Label = label, Menu = "Tools", Group = "3-axes", Order = i, Description = desc,
                Execute = () => ctx.Gizmo.Orientation = o, IsChecked = () => ctx.Gizmo.Orientation == o,
            });
        }

        for (int slot = 1; slot <= CameraBookmarks.Slots; slot++)
        {
            int s = slot;
            c.Add(new Command
            {
                Id = $"view.bookmarkStore{s}", Label = $"Store camera {s}", Category = "Camera", Hotkeys = [Hotkey.Parse($"Ctrl+Shift+{s}")],
                Execute = () =>
                {
                    SyncBookmarks(ctx);
                    Layout().StoreBookmark(s);
                    BookmarkStore.Save(ctx.Session.Path, Layout().Bookmarks);
                    ctx.Status($"Stored camera {s}. Press Ctrl+{s} to return here");
                },
            });
            c.Add(new Command
            {
                Id = $"view.bookmark{s}", Label = $"Recall camera {s}", Category = "Camera", Hotkeys = [Hotkey.Parse($"Ctrl+{s}")],
                Execute = () =>
                {
                    SyncBookmarks(ctx);
                    if (!Layout().RecallBookmark(s)) ctx.Status($"Camera {s} is empty. Press Ctrl+Shift+{s} to store the current view");
                },
            });
        }

        c.AddReference("Camera", "RMB / MMB (2D views)", "pan");
        c.AddReference("Camera", "Scroll (2D views)", "zoom at the cursor");
        c.AddReference("Tools", "MMB + drag gizmo", "move the pivot (resets on new selection)");
        c.AddReference("Tools", "CapsLock + drag gizmo", "align to the vertex, edge or face under the cursor");
        c.AddReference("Tools", "Ctrl + drag gizmo", "bypass snapping");
    }

    /// <summary>Bookmarks belong to a level: reload them when the open document changes.</summary>
    void SyncBookmarks(EditorContext ctx)
    {
        var path = ctx.Session.Path;
        if (path == _bookmarksFor) return;
        _bookmarksFor = path;
        ViewportLayout.Of(ctx).Bookmarks.CopyFrom(BookmarkStore.Load(path));
    }
}
