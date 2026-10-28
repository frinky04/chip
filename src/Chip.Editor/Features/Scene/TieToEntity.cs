using Chip.Core.Scene;
using Chip.Editor.Framework;
using ImGuiNET;

namespace Chip.Editor.Features.Scene;

/// <summary>
/// Tie to entity (Ctrl+Alt+T, Hammer's Ctrl+T): a popup at the mouse listing the mesh classes; picking one makes the
/// selected meshes a mesh entity of that class, its origin at their bottom centre. Untie (Ctrl+Shift+W, Hammer's "move to
/// world") turns the selected mesh entities back into plain meshes.
/// </summary>
public sealed class TieToEntity
{
    const string PopupId = "##tieToEntity";
    bool _openRequested;
    string _filter = "";

    public static bool CanTie(EditorContext ctx) =>
        ctx.Sel.Mode == SelectMode.Object && ctx.Sel.MeshIds().Any(id => ctx.Doc.Find(id) != null);

    public static bool CanUntie(EditorContext ctx) =>
        ctx.Sel.Mode == SelectMode.Object && Selected(ctx).Count > 0;

    /// <summary>The mesh entities in the selection (selected themselves, or through one of their meshes).</summary>
    static List<Entity> Selected(EditorContext ctx) => ctx.Sel.MeshIds()
        .Select(id => ctx.Doc.FindEntity(id) is { IsMeshEntity: true } e ? e : MeshEntityOps.OwnerOf(ctx.Doc, id))
        .OfType<Entity>().Distinct().ToList();

    public void Open() => _openRequested = true;

    public void Draw(EditorContext ctx)
    {
        if (_openRequested)
        {
            _openRequested = false;
            _filter = "";
            if (!ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId)) ImGui.OpenPopup(PopupId);
        }
        if (ClassPicker.Popup(PopupId, null, ref _filter, ClassPicker.Mesh) is { } picked) Tie(ctx, picked);
    }

    static void Tie(EditorContext ctx, string className)
    {
        var meshes = ctx.Sel.MeshIds().Where(id => ctx.Doc.Find(id) != null).ToList();
        Entity? made = null;
        ctx.Session.Do("Tie to Entity", () =>
        {
            made = MeshEntityOps.Tie(ctx.Doc, meshes, className);
            if (made != null) ctx.Sel.Add(ElementRef.Object(made.Id));
        });
        if (made != null)
            ctx.Status($"Tied {(meshes.Count == 1 ? "1 mesh" : $"{meshes.Count} meshes")} to a {EntityClasses.Get(className).Label}. Its origin is in the inspector");
    }

    public static void Untie(EditorContext ctx)
    {
        var entities = Selected(ctx);
        ctx.Session.Do("Untie", () =>
        {
            foreach (var e in entities)
            {
                ctx.Sel.Items.Remove(ElementRef.Object(e.Id));
                MeshEntityOps.Untie(ctx.Doc, e);
            }
        });
        ctx.Status(entities.Count == 1 ? $"Untied the {EntityClasses.Get(entities[0].ClassName).Label}: its meshes are plain meshes again"
            : $"Untied {entities.Count} entities");
    }
}
