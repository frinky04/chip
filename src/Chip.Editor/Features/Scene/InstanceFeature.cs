using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.Scene;

/// <summary>
/// Linked copies (<see cref="InstanceDefinition"/>): make the selection a linked prop, duplicate it linked, break the
/// link, select every copy. The session keeps copies the same (<see cref="InstanceSync"/>), so editing any copy with
/// any tool edits them all; the inspector's Linked section says so while one is selected.
/// </summary>
public sealed class InstanceFeature : IEditorFeature
{
    public void Register(EditorContext ctx)
    {
        bool ObjectMode() => ctx.Sel.Mode == SelectMode.Object && !ctx.Sel.IsEmpty;
        bool AnyCopy() => InstanceOps.SelectedCopies(ctx.Doc, ctx.Sel).Count > 0;
        var c = ctx.Commands;
        c.Add(new Command
        {
            Id = "instance.make", Label = "Make linked", Execute = () => Make(ctx), Menu = "Edit", Group = "2-linked", Order = 0,
            CanExecute = () => ObjectMode() && !AnyCopy(),
            Description = "Turns the selected meshes into a linked prop: copies made from it stay the same as it, whichever one you edit",
        });
        c.Add(new Command
        {
            Id = "instance.duplicateLinked", Label = "Duplicate linked", Execute = () => DuplicateLinked(ctx), Menu = "Edit",
            Group = "2-linked", Order = 1, CanExecute = ObjectMode, Hotkeys = [Hotkey.Parse("Ctrl+Alt+D")],
            Description = "A copy that stays linked: editing any copy changes all of them. Plain meshes are made a linked prop first",
        });
        c.Add(new Command
        {
            Id = "instance.selectCopies", Label = "Select all linked copies", Execute = () => SelectCopies(ctx), Menu = "Select",
            Group = "7-linked", CanExecute = AnyCopy,
        });
        c.Add(new Command
        {
            Id = "instance.makeUnique", Label = "Make unique", Execute = () => MakeUnique(ctx), Menu = "Edit", Group = "2-linked", Order = 2,
            CanExecute = AnyCopy,
            Description = "Breaks the link: the selected copies become plain groups that no longer change with the others",
        });
        ctx.AddSection(new InstanceSection());
    }

    /// <summary>One undo step that may fail with a message (then nothing changes and the message shows as an error).</summary>
    static void Edit(EditorContext ctx, string label, Func<string?> edit)
    {
        ctx.Session.Begin(label, ctx.Sel.MeshIds());
        string? error;
        try { error = edit(); }
        catch { ctx.Session.Cancel(); throw; }
        if (error != null)
        {
            ctx.Session.Cancel();
            ctx.Status(error, error: true);
            return;
        }
        ctx.Session.End();
    }

    const string OnlyMeshes = "Only plain meshes can be linked: leave out entities, mesh entities and copies that are already linked";

    static void SelectAll(EditorContext ctx, IEnumerable<Group> copies)
    {
        ctx.Sel.Clear();
        foreach (var g in copies)
            foreach (var id in InstanceOps.MeshIds(ctx.Doc, g))
                if (SceneOps.IsSelectable(ctx.Doc, id)) ctx.Sel.Add(ElementRef.Object(id));
    }

    static void Make(EditorContext ctx) => Edit(ctx, "Make Linked", () =>
    {
        var name = InstanceName(ctx);
        if (InstanceOps.Make(ctx.Doc, ctx.Sel.MeshIds(), name) is not { } copy) return OnlyMeshes;
        SelectAll(ctx, [copy]);
        ctx.Status($"{copy.Name} is a linked prop. Duplicate it linked (Ctrl+Alt+D) and edit any copy to change them all");
        return null;
    });

    static void DuplicateLinked(EditorContext ctx) => Edit(ctx, "Duplicate Linked", () =>
    {
        var doc = ctx.Doc;
        var copies = InstanceOps.SelectedCopies(doc, ctx.Sel);
        if (copies.Count == 0)
        {
            if (InstanceOps.Make(doc, ctx.Sel.MeshIds(), InstanceName(ctx)) is not { } made) return OnlyMeshes;
            copies = [made];
        }
        else if (ctx.Sel.MeshIds().Any(id => SceneOps.IsObject(doc, id) && InstanceOps.CopyOf(doc, id) == null))
            return "Select only linked copies, or only plain meshes, to duplicate linked";
        // Whole copies: duplicating a copy makes another copy of the same definition.
        var ids = SceneOps.Duplicate(doc, copies.SelectMany(g => InstanceOps.MeshIds(doc, g)).ToList());
        var placed = ids.Select(id => InstanceOps.CopyOf(doc, id)).OfType<Group>().Distinct().ToList();
        SelectAll(ctx, placed);
        int total = InstanceOps.Copies(doc, copies[0].Instance).Count;
        ctx.Status(placed.Count == 1 ? $"Linked copy made: {total} copies of {placed[0].Name}" : $"{placed.Count} linked copies made");
        return null;
    });

    static void SelectCopies(EditorContext ctx)
    {
        var defs = InstanceOps.SelectedCopies(ctx.Doc, ctx.Sel).Select(g => g.Instance).ToHashSet();
        if (ctx.Sel.Mode != SelectMode.Object) ctx.Sel.ConvertTo(SelectMode.Object, ctx.Doc);
        var all = ctx.Doc.Groups.Where(g => defs.Contains(g.Instance)).ToList();
        SelectAll(ctx, all);
        ctx.Status($"Selected {all.Count} linked {(all.Count == 1 ? "copy" : "copies")}");
    }

    static void MakeUnique(EditorContext ctx) => Edit(ctx, "Make Unique", () =>
    {
        int n = InstanceOps.MakeUnique(ctx.Doc, InstanceOps.SelectedCopies(ctx.Doc, ctx.Sel));
        ctx.Status(n == 1 ? "Unlinked: this copy no longer changes with the others" : $"Unlinked {n} copies");
        return null;
    });

    /// <summary>A linked prop is named after the one group selected, else the first mesh.</summary>
    static string? InstanceName(EditorContext ctx)
    {
        var (groups, loose) = GroupOps.Cover(ctx.Doc, ctx.Sel.MeshIds());
        if (groups.Count == 1 && loose.Count == 0) return null; // the group keeps its name
        return loose.Select(ctx.Doc.Find).OfType<Core.Geometry.PolyMesh>().FirstOrDefault()?.Name is { } n ? $"{n} prop" : null;
    }
}

/// <summary>
/// While the selection is in a linked copy: which prop it is, how many copies it has (all of which change with any
/// edit), and the linked commands.
/// </summary>
public sealed class InstanceSection : IInspectorSection
{
    public string Id => "linked";
    public int Order => 101; // right under the selection: it changes what an edit does
    public bool Visible(EditorContext ctx) => InstanceOps.SelectedCopies(ctx.Doc, ctx.Sel).Count > 0;

    public void Draw(EditorContext ctx)
    {
        var doc = ctx.Doc;
        var copies = InstanceOps.SelectedCopies(doc, ctx.Sel);
        if (copies.Count == 0) return;
        var defs = copies.Select(g => g.Instance).Distinct().ToList();
        int total = doc.Groups.Count(g => defs.Contains(g.Instance));
        if (!Heading("Linked", total == 1 ? "1 copy" : $"{total} copies")) return;
        string what = defs.Count == 1 ? doc.FindDefinition(defs[0])?.Name ?? "This prop" : $"{defs.Count} linked props";
        FieldNote(total == 1
            ? $"{what}: the only copy so far. Duplicate it linked to place more."
            : $"{what}: edits to any copy change all {total} of them. Moving or turning a whole copy moves only that one.");
        RowLabel("Copies", "Linked copies of the selected prop");
        CommandRow(ctx.Commands, "linked", ("instance.selectCopies", "Select all", null), ("instance.duplicateLinked", "Duplicate", Icon.Link));
        FieldStart();
        CommandRow(ctx.Commands, "linked2", ("instance.makeUnique", "Make unique", Icon.Unlink));

        // A prefab: where its file is, whether it's there, and opening it; otherwise saving it as one.
        var source = defs.Count == 1 ? doc.FindDefinition(defs[0])?.Source : null;
        RowLabel("Prefab", source == null ? "Lives only in this level. Save it as a prefab to place it in other levels" : source.Path);
        if (source == null) CommandRow(ctx.Commands, "prefab", ("prefab.save", "Save as prefab…", null));
        else
        {
            bool missing = ctx.Session.PrefabRoot is { } root && !File.Exists(Prefabs.FullPath(root, source));
            ImGui.PushStyleColor(ImGuiCol.Text, missing ? Theme.Error : Theme.Text);
            ImGui.TextUnformatted(Ellipsize(source.Path, ImGui.GetContentRegionAvail().X));
            ImGui.PopStyleColor();
            Tip(missing ? $"{source.Path} is missing: the copies keep their last shape. Editing one writes the file again" : source.Path);
            FieldStart();
            CommandRow(ctx.Commands, "prefab", ("prefab.open", "Open prefab", null));
        }
    }
}
