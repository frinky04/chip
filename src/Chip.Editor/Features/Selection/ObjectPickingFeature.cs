using System.Numerics;
using Chip.Core.Picking;
using Chip.Core.Scene;
using Chip.Editor.Features.Scene;
using Chip.Editor.Framework;
using Chip.Editor.Tools.Block;
using Chip.Editor.UI;
using ImGuiNET;

namespace Chip.Editor.Features.SelectionTools;

/// <summary>
/// Picking objects when things overlap. The pick filter (header, Select menu, 5 for entities only) decides whether
/// object mode picks everything, plain geometry or entities, so placing and editing entities never grabs a wall.
/// Ctrl+RMB (or Alt+S) lists everything under the cursor, best first: hovering a row outlines it, clicking selects it
/// (Shift adds, Ctrl toggles). Clicking again in the same spot to cycle lives in <see cref="Tools.SelectTool"/>.
/// </summary>
public sealed class ObjectPickingFeature : IEditorFeature
{
    const string Popup = "##underCursor";
    readonly List<ElementRef> _items = new();
    bool _open;
    Vector2 _at;

    public void Register(EditorContext ctx)
    {
        var c = ctx.Commands;
        c.Add("select.pickAll", "Pick everything", () => SetFilter(ctx, PickFilter.All), "Select", null,
            group: "0-mode", order: 10, isChecked: () => ctx.PickFilter == PickFilter.All);
        c.Add("select.pickGeometry", "Pick geometry only", () => SetFilter(ctx, PickFilter.Geometry), "Select", null,
            group: "0-mode", order: 11, isChecked: () => ctx.PickFilter == PickFilter.Geometry);
        c.Add("select.pickEntities", "Pick entities only", () =>
            SetFilter(ctx, ctx.PickFilter == PickFilter.Entities && ctx.Sel.Mode == SelectMode.Object ? PickFilter.All : PickFilter.Entities),
            "Select", "5", group: "0-mode", order: 12, isChecked: () => ctx.PickFilter == PickFilter.Entities);
        ctx.AddMouseBinding(new MouseBinding(ImGuiMouseButton.Right, Ctrl: true, Shift: false, Alt: false, "Select",
            "list everything under the cursor", Open));
        c.Add("select.underCursor", "List what's under the cursor", () =>
        {
            if (ctx.ViewportHovered) Open(ctx);
            else ctx.Status("Point at something in the viewport, then press Alt+S (or Ctrl+right-click it)");
        }, "Select", "Alt+S", group: "1-basic", order: 3);
        ctx.AddPanel(Draw);
    }

    /// <summary>Sets the filter, switching to object mode (the only mode it applies to).</summary>
    public static void SetFilter(EditorContext ctx, PickFilter filter)
    {
        if (ctx.Sel.Mode != SelectMode.Object) ctx.SetMode(SelectMode.Object);
        ctx.PickFilter = filter;
        ctx.Status(filter switch
        {
            PickFilter.Geometry => "Picking geometry only: entities can't be clicked or box selected",
            PickFilter.Entities => "Picking entities only: walls and floors can't be clicked or box selected. Press 5 again for everything",
            _ => "Picking everything",
        });
    }

    void Open(EditorContext ctx)
    {
        UnderCursor.Collect(ctx, ctx.Mouse, _items);
        if (_items.Count == 0)
        {
            ctx.Status(ctx.PickFilter == PickFilter.All ? "Nothing selectable under the cursor"
                : $"Nothing selectable under the cursor. Picking {ctx.PickFilter.Label().ToLowerInvariant()} only");
            return;
        }
        _open = true;
        _at = ctx.ViewportOrigin + ctx.Mouse;
    }

    void Draw(EditorContext ctx)
    {
        if (_open)
        {
            ImGui.OpenPopup(Popup);
            ImGui.SetNextWindowPos(_at);
            _open = false;
        }
        if (!ImGui.BeginPopup(Popup)) return;
        var doc = ctx.Doc;
        ImGui.TextColored(Theme.TextDim, _items.Count == 1 ? "Under the cursor" : $"Under the cursor, nearest first ({_items.Count})");
        ImGui.Separator();
        float h = ImGui.GetFrameHeight();
        float gap = Theme.Px(6);
        float width = Theme.Px(200);
        bool wholeGroups = GroupSelection.Of(ctx).Enabled;
        foreach (var item in _items)
        {
            var (_, _, name, detail) = UnderCursor.Describe(doc, item.Mesh, wholeGroups);
            width = MathF.Max(width, Fonts.IconSize + gap * 3 + ImGui.CalcTextSize(name).X + ImGui.CalcTextSize(detail).X);
        }
        var io = ImGui.GetIO();
        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            if (!SceneOps.IsSelectable(doc, item.Mesh)) continue; // deleted, hidden or locked since the list opened
            var (icon, color, name, detail) = UnderCursor.Describe(doc, item.Mesh, wholeGroups);
            bool selected = ctx.Sel.Mode == SelectMode.Object && ctx.Sel.Items.Contains(item);
            var start = ImGui.GetCursorScreenPos();
            ImGui.PushID(i);
            if (ImGui.Selectable("##row", false, ImGuiSelectableFlags.None, new Vector2(width, h)))
                Select(ctx, item, io.KeyShift, io.KeyCtrl);
            bool hovered = ImGui.IsItemHovered();
            ImGui.PopID();
            var dl = ImGui.GetWindowDrawList();
            if (selected) Widgets.SelectedRow(dl, start, start + new Vector2(width, h));
            uint text = selected ? Theme.OnAccentU32 : ImGui.ColorConvertFloat4ToU32(Theme.Text);
            uint dim = selected ? Theme.OnAccentU32 & 0xB0FFFFFF : ImGui.ColorConvertFloat4ToU32(Theme.TextDim);
            float y = MathF.Round(start.Y + (h - ImGui.GetTextLineHeight()) * 0.5f);
            Icons.Draw(dl, icon, new Vector2(start.X + gap + Fonts.IconSize * 0.5f, start.Y + h * 0.5f), selected ? text : color);
            float x = start.X + gap * 2 + Fonts.IconSize;
            dl.AddText(new Vector2(x, y), text, name);
            if (detail.Length > 0) dl.AddText(new Vector2(x + ImGui.CalcTextSize(name).X + gap, y), dim, detail);
            if (hovered) Outline(ctx, item.Mesh);
        }
        ImGui.Separator();
        ImGui.TextColored(Theme.TextDim, "Shift adds, Ctrl toggles");
        ImGui.EndPopup();
    }

    static void Select(EditorContext ctx, ElementRef item, bool add, bool toggle)
    {
        if (ctx.Sel.Mode != SelectMode.Object) ctx.SetMode(SelectMode.Object);
        var sel = ctx.Sel;
        if (toggle) sel.Toggle(item);
        else
        {
            if (!add) sel.Clear();
            sel.Add(item);
        }
        ctx.Status($"Selected {UnderCursor.Describe(ctx.Doc, item.Mesh).Name}");
    }

    /// <summary>Outlines a hovered row's object in the viewport, above everything (the renderer has already drawn this frame).</summary>
    static void Outline(EditorContext ctx, int id)
    {
        var dl = ImGui.GetForegroundDrawList();
        var origin = ctx.ViewportOrigin;
        dl.PushClipRect(origin, origin + ctx.ViewportSize, true);
        var doc = ctx.Doc;
        var parts = doc.FindEntity(id) is { } e ? e.Meshes.Append(id) : MeshEntityOps.OwnerOf(doc, id) is { } owner ? owner.Meshes.Append(owner.Id) : [id];
        foreach (int part in parts)
        {
            if (doc.Find(part) is { } m) Overlay.Wire(dl, origin, ctx.View, m);
            else if (doc.FindEntity(part) is { } ent)
                EntityDraw.Draw(dl, origin, ctx.View, ent, EntityLook.Selected, geometry: EntityDrawGeometry.Build(ent));
        }
        dl.PopClipRect();
    }
}
