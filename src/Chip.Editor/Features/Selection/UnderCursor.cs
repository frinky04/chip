using System.Numerics;
using Chip.Core.Scene;
using Chip.Editor.Features.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;

namespace Chip.Editor.Features.SelectionTools;

/// <summary>
/// The objects under a point of the viewport, as object-mode clicks see them: best first, filtered by
/// <see cref="EditorContext.PickFilter"/>, and one per thing a click selects. With whole-group selection on, a group's
/// members are one entry, and a mesh entity's mesh stands for the entity (which then brings its meshes along). Shared by
/// click cycling, the hover tag and the pick list.
/// </summary>
public static class UnderCursor
{
    public static void Collect(EditorContext ctx, Vector2 mouse, List<ElementRef> into)
    {
        var doc = ctx.Doc;
        ctx.Picks.PickObjects(ctx.View, mouse, into, Theme.Scale, ctx.Backfaces, ctx.PickFilter);
        if (!GroupSelection.Of(ctx).Enabled) return;
        for (int i = 0; i < into.Count; i++)
            if (MeshEntityOps.OwnerOf(doc, into[i].Mesh) is { } owner && SceneOps.IsSelectable(doc, owner.Id))
                into[i] = ElementRef.Object(owner.Id);
        if (doc.Groups.Count == 0) return;
        var units = new HashSet<int>();
        into.RemoveAll(e => !units.Add(GroupOps.UnitOf(doc, e.Mesh)?.Id ?? e.Mesh));
    }

    /// <summary>
    /// Icon, name and a dim detail for an object: an entity's class or the mesh entity a mesh belongs to, and the group
    /// selecting it selects with it (when <paramref name="wholeGroups"/>).
    /// </summary>
    public static (Icon Icon, uint Color, string Name, string Detail) Describe(Document doc, int id, bool wholeGroups = false)
    {
        uint dim = ImGui.ColorConvertFloat4ToU32(Theme.TextDim);
        string group = wholeGroups && GroupOps.UnitOf(doc, id) is { } unit ? $"in {unit.Name}" : "";
        string Join(string a) => a.Length > 0 && group.Length > 0 ? $"{a}, {group}" : a + group;
        if (doc.FindEntity(id) is { } e)
        {
            var (icon, col) = EntityDraw.Style(e.ClassName);
            return (icon, col, SceneOps.DisplayName(e), Join(string.IsNullOrWhiteSpace(e.Name) ? "" : e.ClassName));
        }
        if (doc.Find(id) is { } m)
            return (Icon.Object, dim, m.Name, Join(MeshEntityOps.OwnerOf(doc, id) is { } owner ? $"part of {SceneOps.DisplayName(owner)}" : ""));
        return (Icon.Object, dim, "", "");
    }

    /// <summary>A small label beside the cursor: icon, name, detail, and an optional dim second line.</summary>
    public static void Tag(ImDrawListPtr dl, Vector2 at, Icon icon, uint iconColor, string name, string detail, string? hint)
    {
        var pad = Theme.Px(6, 4);
        float gap = Theme.Px(6);
        float line = ImGui.GetTextLineHeight();
        float top = Fonts.IconSize + gap + ImGui.CalcTextSize(name).X + (detail.Length > 0 ? gap + ImGui.CalcTextSize(detail).X : 0);
        float width = MathF.Max(top, hint != null ? ImGui.CalcTextSize(hint).X : 0);
        var size = new Vector2(width, line * (hint != null ? 2 : 1) + (hint != null ? Theme.Px(2) : 0)) + pad * 2;
        at = new Vector2(MathF.Round(at.X), MathF.Round(at.Y));
        dl.AddRectFilled(at, at + size, ImGui.ColorConvertFloat4ToU32(Theme.Panel with { W = 0.94f }), Theme.Px(3));
        dl.AddRect(at, at + size, ImGui.ColorConvertFloat4ToU32(Theme.Border), Theme.Px(3));
        var p = at + pad;
        Icons.Draw(dl, icon, new Vector2(p.X + Fonts.IconSize * 0.5f, p.Y + line * 0.5f), iconColor);
        float x = p.X + Fonts.IconSize + gap;
        dl.AddText(new Vector2(x, p.Y), ImGui.ColorConvertFloat4ToU32(Theme.Text), name);
        uint dim = ImGui.ColorConvertFloat4ToU32(Theme.TextDim);
        if (detail.Length > 0) dl.AddText(new Vector2(x + ImGui.CalcTextSize(name).X + gap, p.Y), dim, detail);
        if (hint != null) dl.AddText(new Vector2(p.X, p.Y + line + Theme.Px(2)), dim, hint);
    }
}
