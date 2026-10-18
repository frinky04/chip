using System.Numerics;
using Chip.Core.Geometry;
using Chip.Core.Scene;
using Chip.Core.Texturing;
using Chip.Editor.Framework;

namespace Chip.Editor.Features.Base;

/// <summary>Undo/redo, cancel, selection basics, delete/duplicate/hide, flip, snap, nudge.</summary>
public sealed class EditFeature : IEditorFeature
{
    EditorContext _ctx = null!;

    public void Register(EditorContext ctx)
    {
        _ctx = ctx;
        var c = ctx.Commands;
        bool HasSelection() => !ctx.Sel.IsEmpty;
        bool HasFaceTargets() => HasSelection() && ctx.Sel.Mode is SelectMode.Face or SelectMode.Object;

        c.Add(new Command
        {
            Id = "edit.undo", Label = "Undo", Menu = "Edit", Group = "1-history", Order = 0, DuringInteraction = true,
            Hotkeys = [Hotkey.Parse("Ctrl+Z")], Execute = Undo, CanExecute = () => ctx.Session.UndoLabel != null,
            DisplayLabel = () => ctx.Session.UndoLabel is { } l ? $"Undo {l}" : "Undo",
        });
        c.Add(new Command
        {
            Id = "edit.redo", Label = "Redo", Menu = "Edit", Group = "1-history", Order = 1, DuringInteraction = true,
            Hotkeys = [Hotkey.Parse("Ctrl+Y"), Hotkey.Parse("Ctrl+Shift+Z")], Execute = Redo, CanExecute = () => ctx.Session.RedoLabel != null,
            DisplayLabel = () => ctx.Session.RedoLabel is { } l ? $"Redo {l}" : "Redo",
        });
        c.Add(new Command
        {
            Id = "edit.cancel", Label = "Cancel / clear selection", Category = "Select", DuringInteraction = true,
            Hotkeys = [Hotkey.Parse("Esc")], Execute = Cancel,
        });

        c.Add("select.all", "Select all", () => ctx.Sel.SelectAll(ctx.Doc), "Select", "Ctrl+A", group: "1-basic", order: 0);
        c.Add("select.invert", "Invert selection", () => ctx.Sel.Invert(ctx.Doc), "Select", "Ctrl+I", group: "1-basic", order: 1);
        c.Add("select.none", "Select none", () => ctx.Sel.Clear(), "Select", group: "1-basic", order: 2, canExecute: HasSelection);

        c.Add("edit.delete", "Delete", Delete, "Edit", "Del", HasSelection, "2-edit", 0);
        c.Add("edit.duplicate", "Duplicate", Duplicate, "Edit", "Shift+D", HasFaceTargets, "2-edit", 1);
        c.Add("edit.hide", "Hide selected", Hide, "Edit", "H", HasSelection, "3-visibility", 0);
        c.Add("edit.unhideAll", "Unhide all", UnhideAll, "Edit", "Alt+H", () => SceneOps.AnyHidden(ctx.Doc), "3-visibility", 1);

        c.Add("mesh.flip", "Flip normals", () => ctx.ForFaces("Flip Normals", MeshOps.FlipFaces), "Mesh", "Shift+N", HasFaceTargets, "5-normals");
        c.Add("mesh.snapVertices", "Snap vertices to grid", SnapVertices, "Mesh", "Ctrl+Shift+Alt+V", HasSelection, "6-snap");

        c.Add("edit.nudgeLeft", "Nudge left", () => Nudge(-1, 0), hotkey: "Alt+Left");
        c.Add("edit.nudgeRight", "Nudge right", () => Nudge(1, 0), hotkey: "Alt+Right");
        c.Add("edit.nudgeUp", "Nudge up", () => Nudge(0, 1), hotkey: "Alt+Up");
        c.Add("edit.nudgeDown", "Nudge down", () => Nudge(0, -1), hotkey: "Alt+Down");
        c.AddReference("Edit", "Alt + arrows", "nudge one grid step");
    }

    void Undo()
    {
        _ctx.CancelInteraction();
        var label = _ctx.Session.UndoLabel;
        if (_ctx.Session.Undo()) _ctx.Status($"Undid {label}");
    }

    void Redo()
    {
        _ctx.CancelInteraction();
        var label = _ctx.Session.RedoLabel;
        if (_ctx.Session.Redo()) _ctx.Status($"Redid {label}");
    }

    /// <summary>Esc: abandon the tool's operation, else leave a non-default tool, else clear the selection.</summary>
    void Cancel()
    {
        if (_ctx.ActiveTool?.Cancel(_ctx) == true) return;
        if (_ctx.ActiveTool != _ctx.DefaultTool && _ctx.DefaultTool != null) _ctx.SetTool(_ctx.DefaultTool);
        else _ctx.Sel.Clear();
    }

    void Delete()
    {
        var sel = _ctx.Sel;
        _ctx.Session.Do("Delete", () =>
        {
            var doc = _ctx.Doc;
            if (sel.Mode == SelectMode.Object)
            {
                SceneOps.Delete(doc, sel.MeshIds()); // meshes, entities, and groups left empty
            }
            else
            {
                foreach (var g in sel.Items.GroupBy(i => i.Mesh))
                {
                    if (doc.Find(g.Key) is not { } mesh) continue;
                    var items = g.ToHashSet();
                    IEnumerable<int> faces = sel.Mode switch
                    {
                        SelectMode.Face => items.Select(i => i.A),
                        SelectMode.Edge => FacesWhere(mesh, f => f.Edges().Any(e => items.Contains(ElementRef.EdgeOf(mesh.Id, e)))),
                        _ => FacesWhere(mesh, f => f.Verts.Any(v => items.Contains(ElementRef.Vertex(mesh.Id, v)))),
                    };
                    MeshOps.DeleteFaces(mesh, faces.ToList());
                }
                doc.Meshes.RemoveAll(m => m.Faces.Count == 0);
            }
            sel.Clear();
        });
    }

    static IEnumerable<int> FacesWhere(PolyMesh m, Func<Face, bool> pred) =>
        Enumerable.Range(0, m.Faces.Count).Where(f => pred(m.Faces[f]));

    void Duplicate()
    {
        var sel = _ctx.Sel;
        _ctx.Session.Do("Duplicate", () =>
        {
            var doc = _ctx.Doc;
            var copies = new List<ElementRef>();
            if (sel.Mode == SelectMode.Object)
                copies.AddRange(SceneOps.Duplicate(doc, sel.MeshIds()).Select(id => ElementRef.Object(id))); // meshes and entities
            else
                foreach (var (mesh, faces) in sel.FaceSets(doc))
                    copies.AddRange(MeshOps.DuplicateFaces(mesh, faces).Select(f => ElementRef.Face(mesh.Id, f)));
            sel.Clear();
            foreach (var c in copies) sel.Add(c);
        });
    }

    void Hide()
    {
        var ids = _ctx.Sel.MeshIds();
        _ctx.Session.Do("Hide", () =>
        {
            SceneOps.SetHidden(_ctx.Doc, ids, true); // meshes and entities
            _ctx.Sel.Clear();
        });
    }

    void UnhideAll() => _ctx.Session.Do("Unhide All", () => SceneOps.UnhideAll(_ctx.Doc));

    void SnapVertices() => _ctx.Session.Do("Snap Vertices to Grid", () =>
    {
        foreach (var (mesh, verts) in _ctx.Sel.VertexSets(_ctx.Doc)) MeshOps.SnapToGrid(mesh, verts, _ctx.Grid);
    });

    /// <summary>Moves the selection one grid step along the view's dominant axes.</summary>
    void Nudge(int right, int up)
    {
        var cam = _ctx.Camera;
        var axisRight = TextureMapping.DominantAxis(cam.Right with { Y = 0 });
        var axisUp = MathF.Abs(cam.Forward.Y) > 0.7f ? TextureMapping.DominantAxis(cam.Up with { Y = 0 }) : Vector3.UnitY;
        var delta = (axisRight * right + axisUp * up) * _ctx.Grid;
        if (TransformGesture.Start(_ctx.Session, "Nudge") is not { } g) return;
        g.Apply(Matrix4x4.CreateTranslation(delta), rigid: true);
        g.Commit();
    }
}
