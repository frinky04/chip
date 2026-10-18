using Chip.Core.Scene;
using Chip.Editor.Features.View;
using Chip.Editor.Framework;

namespace Chip.Editor.Features.Scene;

/// <summary>
/// Scene organisation (SPEC §10): the entity tool and inspector, entity drawing in every pane, the outliner, groups
/// (Ctrl+G / Ctrl+Alt+G, whole-group selection) and lock / unlock all (Ctrl+L / Ctrl+Shift+L).
/// </summary>
public sealed class SceneFeature : IEditorFeature
{
    public void Register(EditorContext ctx)
    {
        var outliner = Outliner.For(ctx);
        var groups = GroupSelection.Of(ctx);
        ctx.AddTool(new EntityTool());
        ViewSettings.Of(ctx).ConstructionTools.Add("entity");
        ctx.AddSection(new EntitySection());
        ctx.AddPanel(groups.Update);
        ctx.AddPanel(EntityOverlay.Draw);
        ctx.AddPanel(outliner.Draw);

        var c = ctx.Commands;
        bool ObjectsSelected() => ctx.Sel.Mode == SelectMode.Object && ctx.Sel.MeshIds().Any(id => SceneOps.IsObject(ctx.Doc, id));

        c.Add("scene.outliner", "Outliner", () => outliner.Open = !outliner.Open, "View", "Ctrl+Shift+O",
            group: "5-panels", order: 1, isChecked: () => outliner.Open);
        c.Add("scene.group", "Group", () => Group(ctx), "Edit", "Ctrl+G", ObjectsSelected, "4-organize", 0);
        c.Add("scene.ungroup", "Ungroup", () => Ungroup(ctx), "Edit", "Ctrl+Alt+G",
            () => ObjectsSelected() && ctx.Sel.MeshIds().Any(id => GroupOps.GroupOf(ctx.Doc, id) != null), "4-organize", 1);
        c.Add("scene.lock", "Lock selected", () => Lock(ctx), "Edit", "Ctrl+L", () => !ctx.Sel.IsEmpty, "3-visibility", 2);
        c.Add("scene.unlockAll", "Unlock all", () => UnlockAll(ctx), "Edit", "Ctrl+Shift+L", () => SceneOps.AnyLocked(ctx.Doc), "3-visibility", 3);
        c.Add("scene.selectGroups", "Select whole groups", () =>
        {
            groups.Enabled = !groups.Enabled;
            ctx.Status(groups.Enabled ? "Clicking a grouped object now selects its whole group" : "Clicking a grouped object now selects just that object");
        }, "Select", group: "4-options", order: 1, isChecked: () => groups.Enabled);

        c.AddReference("Outliner", "Double-click / F2", "rename");
        c.AddReference("Outliner", "Drag rows onto a group", "move into the group");
    }

    static void Group(EditorContext ctx)
    {
        Group? made = null;
        ctx.Session.Do("Group", () => made = GroupOps.Create(ctx.Doc, ctx.Sel.MeshIds()));
        if (made != null) ctx.Status($"Grouped {Objects(GroupOps.AllMembers(ctx.Doc, made).Count)} as {made.Name}");
    }

    static void Ungroup(EditorContext ctx)
    {
        int dissolved = 0;
        ctx.Session.Do("Ungroup", () => dissolved = GroupOps.Ungroup(ctx.Doc, ctx.Sel.MeshIds()));
        ctx.Status(dissolved switch
        {
            0 => "Took the selected objects out of their group",
            1 => "Ungrouped 1 group",
            _ => $"Ungrouped {dissolved} groups",
        });
    }

    /// <summary>Locks the selected objects (or the meshes holding selected elements). Locked objects can't be selected.</summary>
    static void Lock(EditorContext ctx)
    {
        var ids = ctx.Sel.MeshIds().Where(id => SceneOps.IsObject(ctx.Doc, id)).ToList();
        ctx.Session.Do("Lock", () =>
        {
            SceneOps.SetLocked(ctx.Doc, ids, true);
            ctx.Sel.Clear();
        });
        ctx.Status($"Locked {Objects(ids.Count)}. Ctrl+Shift+L unlocks everything");
    }

    static void UnlockAll(EditorContext ctx)
    {
        ctx.Session.Do("Unlock All", () => SceneOps.UnlockAll(ctx.Doc));
        ctx.Status("Unlocked everything");
    }

    static string Objects(int n) => n == 1 ? "1 object" : $"{n} objects";
}
