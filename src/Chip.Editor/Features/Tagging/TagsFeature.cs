using Chip.Core.Scene;
using Chip.Editor.Framework;

namespace Chip.Editor.Features.Tagging;

/// <summary>
/// Tags (dressing phase 0, see docs/dressing.md): names on faces and meshes that dressing rules select by. Ctrl+T opens a
/// popup at the mouse to add, remove and find tags; the inspector's Tags section shows the selection's tags as chips.
/// Face mode tags faces; object mode tags whole meshes. Every change is one undo step.
/// </summary>
public sealed class TagsFeature : IEditorFeature
{
    readonly TagsPopup _popup = new();

    public void Register(EditorContext ctx)
    {
        ctx.Commands.Add("tags.edit", "Tags...", () => _popup.Open(), "Edit", "Ctrl+T",
            () => TagOps.CanTag(ctx.Sel.Mode), "5-tags", 0);
        ctx.AddPanel(_popup.Draw);
        ctx.AddSection(new TagsSection(_popup));
        ctx.Commands.AddReference("Edit", "Ctrl+Enter in Tags", "select everything with the tag");
    }

    /// <summary>Adds or removes <paramref name="tag"/> on the selection as one undo step; does nothing if nothing would change.</summary>
    internal static void Set(EditorContext ctx, string tag, bool on)
    {
        var (with, total) = TagOps.Coverage(ctx.Doc, ctx.Sel, tag);
        int changes = on ? total - with : with;
        if (changes == 0) return;
        ctx.Session.Do(on ? "Add tag" : "Remove tag", ctx.Sel.MeshIds(), () => TagOps.Set(ctx.Doc, ctx.Sel, tag, on));
        string what = ctx.Sel.Mode.Count(changes);
        ctx.Status(on ? $"Tagged {what} \"{tag}\"" : $"Removed \"{tag}\" from {what}");
    }

    /// <summary>Adds the tag unless every target already has it, in which case it removes it.</summary>
    internal static void Toggle(EditorContext ctx, string tag)
    {
        var (with, total) = TagOps.Coverage(ctx.Doc, ctx.Sel, tag);
        Set(ctx, tag, on: total == 0 || with < total);
    }

    /// <summary>Selects every visible, unlocked face (or mesh, in object mode, or for a mesh-only tag) with the tag.</summary>
    internal static void SelectAll(EditorContext ctx, string tag)
    {
        var mode = ctx.Sel.Mode == SelectMode.Object ? SelectMode.Object : SelectMode.Face;
        int n = TagOps.Select(ctx.Doc, ctx.Sel, tag, mode);
        if (n == 0 && mode == SelectMode.Face) n = TagOps.Select(ctx.Doc, ctx.Sel, tag, SelectMode.Object);
        ctx.Status(n == 0 ? $"Nothing visible is tagged \"{tag}\"" : $"Selected {ctx.Sel.Mode.Count(n)} tagged \"{tag}\"");
    }
}
