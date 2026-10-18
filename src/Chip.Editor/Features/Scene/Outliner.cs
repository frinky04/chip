using System.Numerics;
using System.Runtime.CompilerServices;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.Scene;

/// <summary>
/// The outliner (Ctrl+Shift+O): a floating tree of groups, meshes and entities. Click selects (a group selects its members),
/// Ctrl+click toggles, Shift+click selects a range; double-click or F2 renames; the eye and lock buttons hide and lock;
/// dragging rows onto a group moves them into it (onto the empty space below the tree: out to the top level). The search
/// box filters by name or entity class. Selection stays in sync with the viewport because both read the session's selection.
/// </summary>
public sealed class Outliner
{
    static readonly ConditionalWeakTable<EditorContext, Outliner> Instances = new();
    const string Payload = "CHIP_OUTLINER";

    public static Outliner For(EditorContext ctx) => Instances.GetValue(ctx, _ => new Outliner());

    readonly HashSet<int> _collapsed = new();
    string _search = "";
    int? _anchor;          // last clicked row id, for Shift ranges and F2
    int? _renaming;
    bool _focusRename;
    string _renameText = "";
    List<OutlinerNode> _dragged = new();
    bool _wasOpen;
    readonly Rendering.FrameCache<List<OutlinerNode>> _tree = new(selection: false);
    string? _treeSearch;
    List<OutlinerNode>? _rows;
    string _summary = "";

    public bool Open { get; set; }

    public void Draw(EditorContext ctx)
    {
        if (!Open) { _wasOpen = false; return; }
        var vpSize = ctx.ViewportSize;
        float margin = Theme.Px(10), top = Theme.Px(44);
        float w = MathF.Min(Theme.Px(340), MathF.Max(Theme.Px(260), vpSize.X * 0.3f));
        ImGui.SetNextWindowPos(ctx.ViewportOrigin + new Vector2(margin, top), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(w, MathF.Max(Theme.Px(260), MathF.Min(Theme.Px(560), vpSize.Y - top - margin))), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(Theme.Px(220, 160), new Vector2(float.MaxValue));
        if (!_wasOpen) ImGui.SetNextWindowFocus();
        _wasOpen = true;
        bool open = true;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.Px(10, 8));
        bool visible = ImGui.Begin("Outliner###outliner", ref open, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar);
        ImGui.PopStyleVar();
        if (visible) DrawContents(ctx);
        ImGui.End();
        if (!open) Open = false;
    }

    void DrawContents(EditorContext ctx)
    {
        var doc = ctx.Doc;
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##search", "Search names and classes", ref _search, 128);
        bool searching = _search.Trim().Length > 0;
        if (!_tree.IsCurrent(ctx.Session) || _treeSearch != _search)
        {
            _tree.Store(ctx.Session, OutlinerTree.Build(doc, _search));
            _treeSearch = _search;
            _rows = null;
            _summary = OutlinerTree.Summary(doc);
        }
        var rows = _rows ??= OutlinerTree.Flatten(_tree.Value, _collapsed, expandAll: searching);

        float footerH = ImGui.GetFrameHeightWithSpacing();
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.Bg);
        if (ImGui.BeginChild("##tree", new Vector2(0, ImGui.GetContentRegionAvail().Y - footerH), ImGuiChildFlags.None))
        {
            if (rows.Count == 0)
            {
                ImGui.Dummy(Theme.Px(0, 4));
                ImGui.Indent(Theme.Px(8));
                ImGui.TextColored(Theme.TextDim, searching ? "Nothing matches the search" : "The level is empty. Blocks and entities you add appear here");
                ImGui.Unindent(Theme.Px(8));
            }
            unsafe
            {
                var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
                clipper.Begin(rows.Count, ImGui.GetFrameHeightWithSpacing());
                // Keep a rename field submitted while editing, even when the user scrolls it out of view.
                if (_renaming is { } rename && rows.FindIndex(r => r.Id == rename) is int ri && ri >= 0)
                    clipper.IncludeItemByIndex(ri);
                while (clipper.Step())
                    for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++) DrawRow(ctx, rows, i);
                clipper.End();
                clipper.Destroy();
            }
            DropZone(ctx);
            if (ImGui.IsWindowFocused() && _renaming == null && _anchor is { } a && ImGui.IsKeyPressed(ImGuiKey.F2)
                && rows.Find(r => r.Id == a) is { } row)
                BeginRename(doc, row);
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();

        ImGui.AlignTextToFramePadding();
        if (searching)
        {
            int matches = rows.Count(r => OutlinerTree.Matches(r, _search.Trim()));
            ImGui.TextColored(Theme.TextDim, matches == 1 ? "1 match" : $"{matches} matches");
        }
        else ImGui.TextColored(Theme.TextDim, _summary);
    }

    void DrawRow(EditorContext ctx, List<OutlinerNode> rows, int index)
    {
        var doc = ctx.Doc;
        var node = rows[index];
        ImGui.PushID(node.Id);
        float h = ImGui.GetFrameHeight();
        float indent = Theme.Px(14) * node.Depth;
        var start = ImGui.GetCursorScreenPos();
        float rowW = ImGui.GetContentRegionAvail().X;
        var objects = OutlinerTree.Objects(doc, node);
        bool selected = IsSelected(ctx, objects);
        bool isGroup = node.Kind == OutlinerKind.Group;
        float button = h;
        float buttonsW = button * 2;

        ImGui.SetNextItemAllowOverlap();
        if (ImGui.Selectable("##row", false, ImGuiSelectableFlags.AllowDoubleClick, new Vector2(rowW - buttonsW, h)))
        {
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) BeginRename(doc, node);
            else Click(ctx, rows, index);
        }
        bool hovered = ImGui.IsItemHovered();
        DragSource(ctx, rows, node, selected);
        if (isGroup) DropTarget(ctx, doc.FindGroup(node.Id));

        // Selection tint across the whole row (orange = what you're editing), then chevron, icon, name and detail.
        var dl = ImGui.GetWindowDrawList();
        if (selected) dl.AddRectFilled(start, start + new Vector2(rowW, h), ImGui.ColorConvertFloat4ToU32(Theme.Accent with { W = 0.22f }));
        uint text = ImGui.ColorConvertFloat4ToU32(node.Hidden ? Theme.TextDisabled : Theme.Text);
        uint dim = ImGui.ColorConvertFloat4ToU32(Theme.TextDim);
        float x = start.X + Theme.Px(4) + indent;
        float cy = start.Y + h * 0.5f;
        if (isGroup)
        {
            bool collapsed = _collapsed.Contains(node.Id) && _search.Trim().Length == 0;
            ImGui.SetCursorScreenPos(new Vector2(x - Theme.Px(2), start.Y));
            if (ImGui.InvisibleButton("##chevron", new Vector2(Theme.Px(16), h)))
            {
                if (!_collapsed.Add(node.Id)) _collapsed.Remove(node.Id);
                _rows = null;
            }
            Icons.Draw(dl, collapsed ? Icon.ChevronRight : Icon.ChevronDown, new Vector2(x + Theme.Px(6), cy), dim);
        }
        x += Theme.Px(16);
        var (icon, iconCol) = node.Kind switch
        {
            OutlinerKind.Group => (Icon.Folder, dim),
            OutlinerKind.Mesh => (Icon.Object, dim),
            _ => EntityStyle(doc, node.Id),
        };
        Icons.Draw(dl, icon, new Vector2(x + Fonts.IconSize * 0.5f, cy), node.Hidden ? ImGui.ColorConvertFloat4ToU32(Theme.TextDisabled) : iconCol);
        x += Fonts.IconSize + Theme.Px(6);
        float textY = MathF.Round(start.Y + (h - ImGui.GetTextLineHeight()) * 0.5f);
        if (_renaming == node.Id) DrawRename(ctx, node, new Vector2(x, start.Y), start.X + rowW - buttonsW - x - Theme.Px(4));
        else
        {
            dl.PushClipRect(start, new Vector2(start.X + rowW - buttonsW, start.Y + h), true);
            dl.AddText(new Vector2(x, textY), text, node.Name);
            if (node.Detail.Length > 0)
                dl.AddText(new Vector2(x + ImGui.CalcTextSize(node.Name).X + Theme.Px(8), textY), dim, node.Detail);
            dl.PopClipRect();
        }

        // Eye and lock: always shown when on (hidden / locked), otherwise only on the hovered row.
        bool showControls = hovered || ImGui.IsMouseHoveringRect(start, start + new Vector2(rowW, h));
        float bx = start.X + rowW - buttonsW;
        if (RowToggle("##eye", new Vector2(bx, start.Y), button, node.Hidden ? Icon.EyeOff : Icon.Eye, node.Hidden, showControls,
                node.Hidden ? $"Show {node.Name}" : $"Hide {node.Name}"))
            SetHidden(ctx, node, !node.Hidden);
        if (RowToggle("##lock", new Vector2(bx + button, start.Y), button, node.Locked ? Icon.Lock : Icon.LockOpen, node.Locked, showControls,
                node.Locked ? $"Unlock {node.Name}" : $"Lock {node.Name}: it stays visible but can't be selected"))
            SetLocked(ctx, node, !node.Locked);

        ImGui.SetCursorScreenPos(start + new Vector2(0, h));
        ImGui.Dummy(new Vector2(0, 0));
        ImGui.PopID();
    }

    static (Icon, uint) EntityStyle(Document doc, int id) =>
        doc.FindEntity(id) is { } e ? EntityDraw.Style(e.ClassName) : (Icon.MapPin, ImGui.ColorConvertFloat4ToU32(Theme.TextDim));

    static bool RowToggle(string id, Vector2 pos, float size, Icon icon, bool on, bool show, string tip)
    {
        ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGui.InvisibleButton(id, new Vector2(size));
        bool hovered = ImGui.IsItemHovered();
        Tip(tip);
        if (!on && !show && !hovered) return clicked;
        var col = hovered ? Theme.Text : on ? Theme.Text : Theme.TextDim with { W = 0.7f };
        Icons.Draw(ImGui.GetWindowDrawList(), icon, pos + new Vector2(size * 0.5f), ImGui.ColorConvertFloat4ToU32(col));
        return clicked;
    }

    // ---- Selection ----

    static bool IsSelected(EditorContext ctx, List<int> objects) =>
        ctx.Sel.Mode == SelectMode.Object && objects.Count > 0 && objects.All(id => ctx.Sel.Items.Contains(ElementRef.Object(id)));

    void Click(EditorContext ctx, List<OutlinerNode> rows, int index)
    {
        var io = ImGui.GetIO();
        var doc = ctx.Doc;
        var node = rows[index];
        var sel = ctx.Sel;
        if (sel.Mode != SelectMode.Object) sel.ConvertTo(SelectMode.Object, doc);
        var objects = OutlinerTree.Objects(doc, node).Where(id => SceneOps.IsSelectable(doc, id)).ToList();
        if (io.KeyShift && _anchor is { } a && rows.FindIndex(r => r.Id == a) is var ai && ai >= 0)
        {
            if (!io.KeyCtrl) sel.Clear();
            for (int i = Math.Min(ai, index); i <= Math.Max(ai, index); i++)
                foreach (var id in OutlinerTree.Objects(doc, rows[i]).Where(id => SceneOps.IsSelectable(doc, id)))
                    sel.Items.Add(ElementRef.Object(id));
        }
        else if (io.KeyCtrl)
        {
            bool all = objects.Count > 0 && objects.All(id => sel.Items.Contains(ElementRef.Object(id)));
            foreach (var id in objects)
                if (all) sel.Items.Remove(ElementRef.Object(id)); else sel.Items.Add(ElementRef.Object(id));
            _anchor = node.Id;
        }
        else
        {
            sel.Clear();
            foreach (var id in objects) sel.Add(ElementRef.Object(id));
            _anchor = node.Id;
        }
        if (sel.Active is { } act && !sel.Items.Contains(act)) sel.Active = null;
        if (objects.Count == 0 && OutlinerTree.Objects(doc, node).Count > 0)
            ctx.Status($"{node.Name} is hidden or locked, so it can't be selected. Use the eye or lock button first");
        GroupSelection.Of(ctx).Accept(ctx); // a member picked here means that member, not its whole group
    }

    // ---- Rename ----

    void BeginRename(Document doc, OutlinerNode node)
    {
        _renaming = node.Id;
        _renameText = node.Kind == OutlinerKind.Entity && doc.FindEntity(node.Id) is { } e ? e.Name : node.Name;
        _focusRename = true;
        _anchor = node.Id;
    }

    void DrawRename(EditorContext ctx, OutlinerNode node, Vector2 pos, float width)
    {
        ImGui.SetCursorScreenPos(pos);
        ImGui.SetNextItemWidth(MathF.Max(Theme.Px(60), width));
        if (_focusRename) { ImGui.SetKeyboardFocusHere(); _focusRename = false; }
        ImGui.InputTextWithHint("##rename", node.Kind == OutlinerKind.Entity ? "name (empty: use the class)" : "name", ref _renameText, 128,
            ImGuiInputTextFlags.AutoSelectAll);
        if (ImGui.IsKeyPressed(ImGuiKey.Escape)) { _renaming = null; return; }
        if (!ImGui.IsItemDeactivated()) return;
        _renaming = null;
        var text = _renameText.Trim();
        if (text.Length == 0 && node.Kind != OutlinerKind.Entity) { ctx.Status("A name can't be empty; kept the old one"); return; }
        string current = ctx.Doc.FindEntity(node.Id) is { } e ? e.Name : SceneOps.NameOf(ctx.Doc, node.Id) ?? "";
        if (text == current) return;
        ctx.Session.Do("Rename", () => SceneOps.Rename(ctx.Doc, node.Id, text));
    }

    // ---- Hide / lock ----

    static void SetHidden(EditorContext ctx, OutlinerNode node, bool hidden) =>
        ctx.Session.Do(hidden ? $"Hide {node.Name}" : $"Show {node.Name}", () =>
        {
            if (node.Kind == OutlinerKind.Group && ctx.Doc.FindGroup(node.Id) is { } g) GroupOps.SetHidden(ctx.Doc, g, hidden);
            else SceneOps.SetHidden(ctx.Doc, [node.Id], hidden);
        });

    static void SetLocked(EditorContext ctx, OutlinerNode node, bool locked) =>
        ctx.Session.Do(locked ? $"Lock {node.Name}" : $"Unlock {node.Name}", () =>
        {
            if (node.Kind == OutlinerKind.Group && ctx.Doc.FindGroup(node.Id) is { } g) GroupOps.SetLocked(ctx.Doc, g, locked);
            else SceneOps.SetLocked(ctx.Doc, [node.Id], locked);
        });

    // ---- Drag and drop ----

    void DragSource(EditorContext ctx, List<OutlinerNode> rows, OutlinerNode node, bool selected)
    {
        if (!ImGui.BeginDragDropSource()) return;
        // Dragging a selected row drags every selected row; otherwise just this one.
        _dragged = selected ? rows.Where(r => IsSelected(ctx, OutlinerTree.Objects(ctx.Doc, r)) && !HasSelectedAncestor(ctx, rows, r)).ToList() : [node];
        ImGui.SetDragDropPayload(Payload, IntPtr.Zero, 0);
        ImGui.Text(_dragged.Count == 1 ? $"Move {_dragged[0].Name}" : $"Move {_dragged.Count} items");
        ImGui.TextColored(Theme.TextDim, "Drop on a group, or below the list for the top level");
        ImGui.EndDragDropSource();
    }

    /// <summary>A row inside a selected group moves with that group, so it isn't moved on its own.</summary>
    static bool HasSelectedAncestor(EditorContext ctx, List<OutlinerNode> rows, OutlinerNode row)
    {
        var doc = ctx.Doc;
        var g = row.Kind == OutlinerKind.Group ? doc.FindGroup(row.Id) is { } self ? GroupOps.ParentOf(doc, self) : null : GroupOps.GroupOf(doc, row.Id);
        for (; g != null; g = GroupOps.ParentOf(doc, g))
            if (IsSelected(ctx, GroupOps.AllMembers(doc, g))) return true;
        return false;
    }

    void DropTarget(EditorContext ctx, Group? target)
    {
        if (!ImGui.BeginDragDropTarget()) return;
        unsafe
        {
            if (ImGui.AcceptDragDropPayload(Payload).NativePtr != null) Move(ctx, target);
        }
        ImGui.EndDragDropTarget();
    }

    /// <summary>The space under the last row: dropping there moves items out of their groups.</summary>
    void DropZone(EditorContext ctx)
    {
        var avail = ImGui.GetContentRegionAvail();
        ImGui.InvisibleButton("##dropzone", new Vector2(MathF.Max(1, avail.X), MathF.Max(Theme.Px(24), avail.Y)));
        if (ImGui.IsItemClicked() && !ImGui.GetIO().KeyCtrl && !ImGui.GetIO().KeyShift) ctx.Sel.Clear();
        DropTarget(ctx, null);
    }

    void Move(EditorContext ctx, Group? target)
    {
        var items = _dragged;
        _dragged = new();
        if (items.Count == 0) return;
        int refused = 0;
        ctx.Session.Do(target == null ? "Move to Top Level" : $"Move into {target.Name}", () =>
        {
            var doc = ctx.Doc;
            var dest = target == null ? null : doc.FindGroup(target.Id);
            foreach (var n in items)
            {
                if (n.Kind == OutlinerKind.Group)
                {
                    if (doc.FindGroup(n.Id) is { } g && !GroupOps.MoveTo(doc, g, dest)) refused++;
                }
                else GroupOps.MoveTo(doc, n.Id, dest);
            }
            GroupOps.Prune(doc);
        });
        if (refused > 0) ctx.Status("A group can't go inside itself; that part of the move was skipped");
        else ctx.Status(target == null ? $"Moved {Count(items.Count)} to the top level" : $"Moved {Count(items.Count)} into {target.Name}");
    }

    static string Count(int n) => n == 1 ? "1 item" : $"{n} items";
}
