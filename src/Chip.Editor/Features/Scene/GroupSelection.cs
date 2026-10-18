using System.Runtime.CompilerServices;
using Chip.Core.Scene;
using Chip.Editor.Framework;

namespace Chip.Editor.Features.Scene;

/// <summary>
/// Hammer-style group selection: when an object joins the selection (click, box select, any command), the rest of its
/// outermost group joins too. Runs once per frame and only looks at what was added since the last frame, so Ctrl-click can
/// still take single members back out. Selections made by edits (a duplicate, a new block) are left alone: expansion only
/// happens on frames where the document didn't change. The outliner calls <see cref="Accept"/> after selecting a member on purpose.
/// </summary>
public sealed class GroupSelection
{
    static readonly ConditionalWeakTable<EditorContext, GroupSelection> Table = new();

    public static GroupSelection Of(EditorContext ctx) => Table.GetValue(ctx, _ => new GroupSelection());

    readonly HashSet<ElementRef> _seen = new();
    Selection? _seenSel;
    int _seenVersion = -1;

    /// <summary>Off: clicking a grouped object selects just that object.</summary>
    public bool Enabled { get; set; } = true;

    public void Update(EditorContext ctx)
    {
        var sel = ctx.Sel;
        if (Enabled && sel.Mode == SelectMode.Object && ctx.Doc.Groups.Count > 0 && !ctx.Interacting
            && ReferenceEquals(sel, _seenSel) && ctx.Session.Version == _seenVersion)
        {
            var added = sel.Items.Where(i => !_seen.Contains(i)).Select(i => i.Mesh).ToList();
            if (added.Count > 0)
                foreach (var id in GroupOps.ExpandToGroups(ctx.Doc, added))
                    sel.Items.Add(ElementRef.Object(id));
        }
        Accept(ctx);
    }

    /// <summary>Takes the current selection as already expanded (no group expansion for it).</summary>
    public void Accept(EditorContext ctx)
    {
        _seenSel = ctx.Sel;
        _seenVersion = ctx.Session.Version;
        _seen.Clear();
        _seen.UnionWith(ctx.Sel.Items);
    }
}
