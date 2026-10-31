using System.Numerics;
using System.Runtime.CompilerServices;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.Scene;

/// <summary>
/// The Scene tab of the dock (Ctrl+Shift+O): the outliner, a tree of folders, groups, meshes and entities (a mesh entity
/// holds its meshes). Click selects (a folder or group selects its contents), Ctrl+click toggles, Shift+click selects a
/// range; Up and Down walk the rows, Left and Right fold them. Double-click or F2 renames; the eye and lock buttons hide
/// and lock; right-click opens the row's menu (focus, duplicate, folders, hide everything else...). Dragging rows onto a
/// folder or group moves them into it, onto another row moves them beside it, onto the empty space below the tree out to
/// the top level. The bar above filters by name or class and by kind, and makes folders. Selecting in the viewport
/// unfolds the tree to the active object and scrolls it into view. Under the tree: hidden and locked counts with Unhide
/// all and Unlock all, and the selection sets.
/// </summary>
public sealed class Outliner : IDockPanel
{
    static readonly ConditionalWeakTable<EditorContext, Outliner> Instances = new();
    const string Payload = "CHIP_OUTLINER";
    const string RowMenu = "##rowmenu", SpaceMenu = "##spacemenu", ShowMenu = "##showmenu";
    public const string PanelId = "scene";

    public static Outliner For(EditorContext ctx) => Instances.GetValue(ctx, _ => new Outliner());

    readonly HashSet<int> _collapsed = new();
    string _search = "";
    OutlinerShow _show;
    int? _anchor;          // last clicked row id, for Shift ranges and the empty-folder target
    int? _cursor;          // the row the arrows move from (the far end of a Shift range); F2 renames it
    int? _renaming;
    bool _focusRename;
    string _renameText = "";
    List<OutlinerNode> _dragged = new();
    readonly Rendering.FrameCache<List<OutlinerNode>> _tree = new(selection: false);
    (string Search, OutlinerShow Show)? _treeQuery;
    List<OutlinerNode>? _rows;
    string _summary = "";
    float _footerH;
    readonly Rendering.FrameCache<(int Hidden, int Locked)> _hiddenLocked = new(selection: false);
    ElementRef? _seenActive;   // the viewport's active object last frame, to reveal a new one
    int? _scrollTo;
    OutlinerNode? _menuNode;
    bool _openRowMenu;
    int? _pendingMenu;     // a row whose menu opens when it's next drawn (captures)
    Vector2? _menuAt;

    int _pendingFrames;

    /// <summary>
    /// Opens a row's right-click menu beside the row, for headless captures (the mouse opens it otherwise). It waits a few
    /// frames: windows appearing for the first time take focus, which closes any popup already open.
    /// </summary>
    public void OpenMenu(int id)
    {
        _pendingMenu = id;
        _pendingFrames = 2;
    }

    public string Id => PanelId;
    public string Title => "Scene";
    public Icon Icon => Icon.ListTree;
    public int Order => 10;
    public string? Command => "scene.outliner";

    /// <summary>What the bar's kind filter shows. Settable for tests and captures.</summary>
    public OutlinerShow Show
    {
        get => _show;
        set { _show = value; _rows = null; }
    }

    public void Draw(EditorContext ctx)
    {
        TrackSelection(ctx);
        DrawBar(ctx);
        DrawTree(ctx, _footerH); // the footer's height from last frame: the tree takes the rest
        float footerTop = ImGui.GetCursorPosY();
        DrawFooter(ctx);
        _footerH = ImGui.GetCursorPosY() - footerTop + ImGui.GetStyle().ItemSpacing.Y;
    }

    /// <summary>Hidden and locked counts (with the commands that undo them), then the selection sets.</summary>
    void DrawFooter(EditorContext ctx)
    {
        var doc = ctx.Doc;
        if (!_hiddenLocked.IsCurrent(ctx.Session))
            _hiddenLocked.Store(ctx.Session, (doc.Meshes.Count(m => m.Hidden) + doc.Entities.Count(e => e.Hidden) + doc.Groups.Count(g => g.Hidden),
                doc.Meshes.Count(m => m.Locked) + doc.Entities.Count(e => e.Locked) + doc.Groups.Count(g => g.Locked)));
        var (hidden, locked) = _hiddenLocked.Value;
        if (hidden + locked > 0)
        {
            ImGui.Dummy(Theme.Px(0, 2));
            float half = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
            ImGui.BeginDisabled(hidden == 0);
            if (ImGui.Button(hidden == 0 ? "Nothing hidden" : $"Unhide {hidden}", new Vector2(half, 0))) ctx.Commands.Execute("edit.unhideAll");
            ImGui.EndDisabled();
            Tip("Show everything hidden  (Alt+H)");
            ImGui.SameLine();
            ImGui.BeginDisabled(locked == 0);
            if (ImGui.Button(locked == 0 ? "Nothing locked" : $"Unlock {locked}", new Vector2(-1, 0))) ctx.Commands.Execute("scene.unlockAll");
            ImGui.EndDisabled();
            Tip("Unlock everything  (Ctrl+Shift+L)");
        }
        ImGui.Dummy(Theme.Px(0, 4));
        SelectionTools.SelectionSetsView.Draw(ctx, SelectionTools.SelectionSets.For(ctx));
    }

    // ---- Bar: search, kind filter, new folder ----

    void DrawBar(EditorContext ctx)
    {
        float button = ImGui.GetFrameHeight();
        float gap = ImGui.GetStyle().ItemSpacing.X;
        ImGui.SetNextItemWidth(MathF.Max(Theme.Px(60), ImGui.GetContentRegionAvail().X - (button + gap) * 2));
        ImGui.InputTextWithHint("##search", "Search names and classes", ref _search, 128);
        ImGui.SameLine();
        var showIcon = _show switch { OutlinerShow.Meshes => Icon.Object, OutlinerShow.Entities => Icon.MapPin, _ => Icon.ListTree };
        if (BarButton("##show", showIcon, _show != OutlinerShow.All, _show switch
            {
                OutlinerShow.Meshes => "Showing meshes only",
                OutlinerShow.Entities => "Showing entities only",
                _ => "Showing everything. Click to list only meshes or only entities",
            }, button))
            ImGui.OpenPopup(ShowMenu);
        if (ImGui.BeginPopup(ShowMenu))
        {
            foreach (var (show, label) in new[] { (OutlinerShow.All, "Everything"), (OutlinerShow.Meshes, "Meshes only"), (OutlinerShow.Entities, "Entities only") })
                if (ImGui.MenuItem(label, null, _show == show)) Show = show;
            ImGui.EndPopup();
        }
        ImGui.SameLine();
        if (BarButton("##folder", Icon.Folder, false, "New folder: the selected objects go in it, or it starts empty", button, plus: true))
            NewFolder(ctx);
    }

    /// <summary>An icon button; <paramref name="on"/> draws the icon orange (a setting that's on), <paramref name="plus"/> adds a small plus.</summary>
    static bool BarButton(string id, Icon icon, bool on, string tip, float size, bool plus = false)
    {
        bool clicked = ImGui.Button(id, new Vector2(size));
        var (a, b) = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
        var dl = ImGui.GetWindowDrawList();
        var c = (a + b) * 0.5f;
        uint col = on ? Theme.AccentU32 : ImGui.ColorConvertFloat4ToU32(Theme.Text);
        Icons.Draw(dl, icon, plus ? c - Theme.Px(1.5f, 1.5f) : c, col);
        if (plus)
        {
            // A plus at the bottom right, on a patch of the button colour so it reads apart from the folder.
            var p = b - Theme.Px(6, 6);
            float r = Theme.Px(3);
            dl.AddCircleFilled(p, r + Theme.Px(1.5f), ImGui.ColorConvertFloat4ToU32(ImGui.IsItemHovered() ? Theme.FrameHover : Theme.Frame));
            dl.AddLine(p - new Vector2(r, 0), p + new Vector2(r + 1, 0), col, Theme.Px(1.5f));
            dl.AddLine(p - new Vector2(0, r), p + new Vector2(0, r + 1), col, Theme.Px(1.5f));
        }
        Tip(tip);
        return clicked;
    }

    // ---- Tree ----

    void DrawTree(EditorContext ctx, float reserve)
    {
        var doc = ctx.Doc;
        bool searching = _search.Trim().Length > 0;
        if (!_tree.IsCurrent(ctx.Session) || _treeQuery != (_search, _show))
        {
            _tree.Store(ctx.Session, OutlinerTree.Build(doc, _search, _show));
            _treeQuery = (_search, _show);
            _rows = null;
            _summary = OutlinerTree.Summary(doc);
        }
        var rows = _rows ??= OutlinerTree.Flatten(_tree.Value, _collapsed, expandAll: searching);
        // Renaming a row the search or kind filter hides (F2 on the selection): drop the filter so its field can show.
        if (_renaming is { } renaming && rows.All(r => r.Id != renaming) && (searching || _show != OutlinerShow.All))
        {
            _search = "";
            Show = OutlinerShow.All;
        }

        float footerH = ImGui.GetFrameHeightWithSpacing() + reserve;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.Bg);
        if (ImGui.BeginChild("##tree", new Vector2(0, MathF.Max(Theme.Px(120), ImGui.GetContentRegionAvail().Y - footerH)), ImGuiChildFlags.None))
        {
            if (rows.Count == 0)
            {
                ImGui.Dummy(Theme.Px(0, 4));
                ImGui.Indent(Theme.Px(8));
                ImGui.TextColored(Theme.TextDim, searching ? "Nothing matches the search"
                    : _show != OutlinerShow.All ? $"The level has no {(_show == OutlinerShow.Meshes ? "meshes" : "entities")}"
                    : "The level is empty. Blocks and entities you add appear here");
                ImGui.Unindent(Theme.Px(8));
            }
            ScrollToTarget(rows);
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
            if (_openRowMenu) { ImGui.OpenPopup(RowMenu); _openRowMenu = false; }
            if (_menuAt is { } at) { ImGui.SetNextWindowPos(at); _menuAt = null; }
            if (ImGui.BeginPopup(RowMenu))
            {
                if (_menuNode != null) DrawRowMenu(ctx, _menuNode);
                ImGui.EndPopup();
            }
            if (ImGui.BeginPopup(SpaceMenu))
            {
                DrawSpaceMenu(ctx);
                ImGui.EndPopup();
            }
            if (ImGui.IsWindowFocused() && _renaming == null)
            {
                Keys(ctx, rows);
                // F2 with the tree focused renames the row the arrows are on (a hovered row still wins).
                if ((_cursor ?? _anchor) is { } current && rows.Any(r => r.Id == current))
                    ctx.OfferRename(() => BeginRename(ctx.Doc, current), hovered: false);
            }
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
        bool selected = IsSelected(ctx, OutlinerTree.Objects(doc, node));
        float button = h;
        float buttonsW = button * 2;

        ImGui.SetNextItemAllowOverlap();
        if (ImGui.Selectable("##row", false, ImGuiSelectableFlags.AllowDoubleClick, new Vector2(rowW - buttonsW, h)))
        {
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) BeginRename(doc, node.Id);
            else Click(ctx, rows, index, ImGui.GetIO().KeyShift, ImGui.GetIO().KeyCtrl);
        }
        bool hovered = ImGui.IsItemHovered();
        if (_renaming == null && ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(start, start + new Vector2(rowW, h)))
            ctx.OfferRename(() => BeginRename(ctx.Doc, node.Id), hovered: true); // F2 renames the row under the mouse
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            // Like a file browser: right-clicking outside the selection selects that row first.
            if (!selected) Click(ctx, rows, index, false, false);
            _menuNode = node;
            _openRowMenu = true;
        }
        if (_pendingMenu == node.Id && _pendingFrames-- <= 0)
        {
            _menuNode = node;
            _openRowMenu = true;
            _menuAt = start + new Vector2(Theme.Px(60), h);
            _pendingMenu = null;
        }
        DragSource(ctx, rows, node, selected);
        DropTarget(ctx, node);

        // Selected rows are solid orange with dark text (Krita's layer list), then chevron, icon, name and detail. The
        // last clicked row (an empty folder, say, which can't be selected) gets a quiet frame.
        var dl = ImGui.GetWindowDrawList();
        if (selected) SelectedRow(dl, start, start + new Vector2(rowW, h));
        else if (hovered) dl.AddRectFilled(start, start + new Vector2(rowW, h), ImGui.ColorConvertFloat4ToU32(Theme.Frame));
        else if (_anchor == node.Id && node.IsContainer) dl.AddRect(start, start + new Vector2(rowW, h), ImGui.ColorConvertFloat4ToU32(Theme.FrameHover));
        uint text = selected ? Theme.OnAccentU32 : ImGui.ColorConvertFloat4ToU32(node.Hidden ? Theme.TextDisabled : Theme.Text);
        uint dim = selected ? Theme.OnAccentU32 & 0xB0FFFFFF : ImGui.ColorConvertFloat4ToU32(Theme.TextDim);
        float x = start.X + Theme.Px(4) + indent;
        float cy = start.Y + h * 0.5f;
        if (node.Children.Count > 0)
        {
            bool collapsed = _collapsed.Contains(node.Id) && _search.Trim().Length == 0;
            ImGui.SetCursorScreenPos(new Vector2(x - Theme.Px(2), start.Y));
            if (ImGui.InvisibleButton("##chevron", new Vector2(Theme.Px(16), h))) Fold(node.Id, !collapsed);
            Icons.Draw(dl, collapsed ? Icon.ChevronRight : Icon.ChevronDown, new Vector2(x + Theme.Px(6), cy), dim);
        }
        x += Theme.Px(16);
        var (icon, iconCol) = node.Kind switch
        {
            OutlinerKind.Folder => (Icon.Folder, dim),
            OutlinerKind.Group => (doc.FindGroup(node.Id) is { IsInstance: true } ? Icon.Link : Icon.TreatAsOne, dim),
            OutlinerKind.Mesh => (Icon.Object, dim),
            _ => EntityStyle(doc, node.Id),
        };
        Icons.Draw(dl, icon, new Vector2(x + Fonts.IconSize * 0.5f, cy), selected ? text : node.Hidden ? ImGui.ColorConvertFloat4ToU32(Theme.TextDisabled) : iconCol);
        if (hovered && node.Kind is OutlinerKind.Folder or OutlinerKind.Group)
            Tip(node.Kind == OutlinerKind.Folder ? "Folder: its members select one by one"
                : doc.FindGroup(node.Id) is { IsInstance: true } copy
                    ? $"Linked copy of {doc.FindDefinition(copy.Instance)?.Name}, one of {InstanceOps.Copies(doc, copy.Instance).Count}: editing any copy changes all of them"
                    : "Group: clicking any member in the viewport selects all of it");
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
        if (RowToggle("##eye", new Vector2(bx, start.Y), button, node.Hidden ? Icon.EyeOff : Icon.Eye, node.Hidden, showControls, selected,
                node.Hidden ? $"Show {node.Name}" : $"Hide {node.Name}"))
            SetHidden(ctx, node, !node.Hidden);
        if (RowToggle("##lock", new Vector2(bx + button, start.Y), button, node.Locked ? Icon.Lock : Icon.LockOpen, node.Locked, showControls, selected,
                node.Locked ? $"Unlock {node.Name}" : $"Lock {node.Name}: it stays visible but can't be selected"))
            SetLocked(ctx, node, !node.Locked);

        ImGui.SetCursorScreenPos(start + new Vector2(0, h));
        ImGui.Dummy(new Vector2(0, 0));
        ImGui.PopID();
    }

    static (Icon, uint) EntityStyle(Document doc, int id) =>
        doc.FindEntity(id) is { } e ? EntityDraw.Style(e.ClassName) : (Icon.MapPin, ImGui.ColorConvertFloat4ToU32(Theme.TextDim));

    static bool RowToggle(string id, Vector2 pos, float size, Icon icon, bool on, bool show, bool onAccent, string tip)
    {
        ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGui.InvisibleButton(id, new Vector2(size));
        bool hovered = ImGui.IsItemHovered();
        Tip(tip);
        if (!on && !show && !hovered) return clicked;
        var col = onAccent ? Theme.OnAccent with { W = hovered || on ? 1 : 0.7f } : hovered || on ? Theme.Text : Theme.TextDim with { W = 0.7f };
        Icons.Draw(ImGui.GetWindowDrawList(), icon, pos + new Vector2(size * 0.5f), ImGui.ColorConvertFloat4ToU32(col));
        return clicked;
    }

    void Fold(int id, bool collapse)
    {
        if (collapse ? _collapsed.Add(id) : _collapsed.Remove(id)) _rows = null;
    }

    // ---- Following the viewport ----

    /// <summary>A new active object picked elsewhere (the viewport, a command) unfolds the tree to it and scrolls it into view.</summary>
    void TrackSelection(EditorContext ctx)
    {
        var active = ctx.Sel.Mode == SelectMode.Object ? ctx.Sel.Active : null;
        if (active == _seenActive) return;
        _seenActive = active;
        if (active is { } a && SceneOps.IsObject(ctx.Doc, a.Mesh)) Reveal(ctx.Doc, a.Mesh);
    }

    /// <summary>Unfolds the rows holding an object, group or folder and scrolls to it.</summary>
    public void Reveal(Document doc, int id)
    {
        foreach (int a in OutlinerTree.Ancestors(doc, id)) Fold(a, false);
        _scrollTo = id;
    }

    void ScrollToTarget(List<OutlinerNode> rows)
    {
        if (_scrollTo is not { } id) return;
        _scrollTo = null;
        int index = rows.FindIndex(r => r.Id == id);
        if (index < 0) return;
        float step = ImGui.GetFrameHeightWithSpacing();
        float y = index * step, view = ImGui.GetWindowHeight(), scroll = ImGui.GetScrollY();
        if (y < scroll || y + step > scroll + view) ImGui.SetScrollY(MathF.Max(0, y - view * 0.4f));
    }

    // ---- Selection ----

    static bool IsSelected(EditorContext ctx, List<int> objects) =>
        ctx.Sel.Mode == SelectMode.Object && objects.Count > 0 && objects.All(id => ctx.Sel.Items.Contains(ElementRef.Object(id)));

    /// <summary>What clicking a row selects: its objects, and for a mesh entity its meshes too (they move with it).</summary>
    static IEnumerable<int> Selects(Document doc, OutlinerNode node)
    {
        foreach (int id in OutlinerTree.Objects(doc, node))
        {
            if (SceneOps.IsSelectable(doc, id)) yield return id;
            if (node.Kind == OutlinerKind.Entity && doc.FindEntity(id) is { IsMeshEntity: true } e)
                foreach (int m in e.Meshes.Where(m => SceneOps.IsSelectable(doc, m))) yield return m;
        }
    }

    void Click(EditorContext ctx, List<OutlinerNode> rows, int index, bool shift, bool ctrl)
    {
        var doc = ctx.Doc;
        var node = rows[index];
        var sel = ctx.Sel;
        if (sel.Mode != SelectMode.Object) sel.ConvertTo(SelectMode.Object, doc);
        var objects = Selects(doc, node).ToList();
        if (shift && _anchor is { } a && rows.FindIndex(r => r.Id == a) is var ai && ai >= 0)
        {
            if (!ctrl) sel.Clear();
            for (int i = Math.Min(ai, index); i <= Math.Max(ai, index); i++)
                foreach (var id in Selects(doc, rows[i]))
                    sel.Items.Add(ElementRef.Object(id));
        }
        else if (ctrl)
        {
            bool all = objects.Count > 0 && objects.All(id => sel.Items.Contains(ElementRef.Object(id)));
            foreach (var id in objects)
                if (all) sel.Items.Remove(ElementRef.Object(id)); else sel.Items.Add(ElementRef.Object(id));
            _anchor = node.Id;
        }
        else
        {
            sel.Clear();
            // The row's own object leads (it's what the inspector shows), then the rest.
            if (!node.IsContainer && objects.Contains(node.Id)) sel.Add(ElementRef.Object(node.Id));
            foreach (var id in objects) sel.Items.Add(ElementRef.Object(id));
            if (sel.Active == null && objects.Count > 0) sel.Active = ElementRef.Object(objects[0]);
            _anchor = node.Id;
        }
        if (sel.Active is { } act && !sel.Items.Contains(act)) sel.Active = null;
        if (objects.Count == 0 && OutlinerTree.Objects(doc, node).Count > 0)
            ctx.Status($"{node.Name} is hidden or locked, so it can't be selected. Use the eye or lock button first");
        GroupSelection.Of(ctx).Accept(ctx); // a member picked here means that member, not its whole group
        _seenActive = sel.Mode == SelectMode.Object ? sel.Active : null; // already in view: don't jump
        _cursor = node.Id;
    }

    /// <summary>Up and Down move to the next row (Shift extends), Left folds or goes to the parent, Right unfolds.</summary>
    void Keys(EditorContext ctx, List<OutlinerNode> rows)
    {
        if (rows.Count == 0 || ImGui.GetIO().KeyCtrl || ImGui.GetIO().KeyAlt) return;
        int at = (_cursor ?? _anchor) is { } a ? rows.FindIndex(r => r.Id == a) : -1;
        int step = (ImGui.IsKeyPressed(ImGuiKey.DownArrow) ? 1 : 0) - (ImGui.IsKeyPressed(ImGuiKey.UpArrow) ? 1 : 0);
        if (step != 0)
        {
            int next = Math.Clamp(at < 0 ? 0 : at + step, 0, rows.Count - 1);
            Click(ctx, rows, next, ImGui.GetIO().KeyShift, false); // a Shift range grows from the anchor
            _scrollTo = rows[next].Id;
            return;
        }
        if (at < 0) return;
        var node = rows[at];
        if (ImGui.IsKeyPressed(ImGuiKey.RightArrow) && node.Children.Count > 0) Fold(node.Id, false);
        if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow))
        {
            if (node.Children.Count > 0 && !_collapsed.Contains(node.Id)) Fold(node.Id, true);
            else
                for (int i = at - 1; i >= 0; i--)
                    if (rows[i].Depth < node.Depth) { Click(ctx, rows, i, false, false); _scrollTo = rows[i].Id; break; }
        }
    }

    // ---- Menus ----

    void DrawRowMenu(EditorContext ctx, OutlinerNode node)
    {
        var doc = ctx.Doc;
        var group = node.IsContainer ? doc.FindGroup(node.Id) : null;
        bool objects = ctx.Sel.Mode == SelectMode.Object && !ctx.Sel.IsEmpty;
        MenuCommand(ctx, "view.focus", "Focus");
        if (ImGui.MenuItem("Rename", ctx.Commands.Find("edit.rename")?.HotkeyText)) BeginRename(doc, node.Id);
        ImGui.Separator();
        MenuCommand(ctx, "edit.duplicate", "Duplicate");
        MenuCommand(ctx, "edit.delete", group != null ? "Delete with contents" : "Delete");
        if (group != null)
        {
            if (ImGui.MenuItem(group.Folder ? "Remove folder, keep contents" : "Ungroup, keep contents"))
            {
                string name = group.Name;
                ctx.Session.Do(group.Folder ? "Remove Folder" : "Ungroup", () => { if (ctx.Doc.FindGroup(node.Id) is { } g) GroupOps.Dissolve(ctx.Doc, g); });
                ctx.Status($"Removed {name}; what it held moved up a level");
            }
            if (ImGui.MenuItem(group.Folder ? "Turn into a group" : "Turn into a folder"))
            {
                bool folder = !group.Folder;
                ctx.Session.Do(folder ? "Make Folder" : "Make Group", () =>
                {
                    if (ctx.Doc.FindGroup(node.Id) is not { } g) return;
                    if (folder) InstanceOps.MakeUnique(ctx.Doc, [g]); // a folder only sorts things, so it can't be a linked copy
                    g.Folder = folder;
                });
                ctx.Status(folder ? $"{group.Name} is a folder: its members now select one by one"
                    : $"{group.Name} is a group: clicking any member in the viewport selects all of it");
            }
            if (ImGui.MenuItem("New folder inside")) NewFolder(ctx, inside: group, empty: true);
        }
        ImGui.Separator();
        MenuCommand(ctx, "scene.group", "Group");
        if (ImGui.MenuItem("Move to new folder", null, false, objects)) NewFolder(ctx);
        if (ImGui.MenuItem("Move to top level", null, false, objects && ctx.Sel.MeshIds().Any(id => GroupOps.GroupOf(doc, id) != null)))
            MoveToTopLevel(ctx);
        if (node.Kind == OutlinerKind.Entity && doc.FindEntity(node.Id) is { IsMeshEntity: true }) MenuCommand(ctx, "scene.untie", "Untie from entity");
        ImGui.Separator();
        if (ImGui.MenuItem(node.Hidden ? "Show" : "Hide")) SetHidden(ctx, node, !node.Hidden);
        if (ImGui.MenuItem(node.Locked ? "Unlock" : "Lock")) SetLocked(ctx, node, !node.Locked);
        if (ImGui.MenuItem("Hide everything else", null, false, objects)) HideOthers(ctx);
    }

    void DrawSpaceMenu(EditorContext ctx)
    {
        if (ImGui.MenuItem("New folder")) NewFolder(ctx, empty: true);
        ImGui.Separator();
        if (ImGui.MenuItem("Expand all"))
        {
            _collapsed.Clear();
            _rows = null;
        }
        if (ImGui.MenuItem("Collapse all"))
        {
            void Walk(OutlinerNode n)
            {
                if (n.Children.Count == 0) return;
                _collapsed.Add(n.Id);
                foreach (var c in n.Children) Walk(c);
            }
            foreach (var r in _tree.Value) Walk(r);
            _rows = null;
        }
    }

    static void MenuCommand(EditorContext ctx, string id, string label)
    {
        if (ctx.Commands.Find(id) is { } c && ImGui.MenuItem(label, c.HotkeyText, false, c.Enabled)) c.Execute();
    }

    /// <summary>
    /// Makes a folder: with objects selected (and not <paramref name="empty"/>) they go in it, else it starts empty inside
    /// <paramref name="inside"/> (default: the last clicked folder or group). The new row opens for renaming.
    /// </summary>
    public void NewFolder(EditorContext ctx, Group? inside = null, bool empty = false)
    {
        var doc = ctx.Doc;
        var ids = empty || ctx.Sel.Mode != SelectMode.Object ? [] : ctx.Sel.MeshIds().Where(id => SceneOps.IsObject(doc, id)).ToList();
        inside ??= _anchor is { } a ? doc.FindGroup(a) : null;
        int? made = null;
        ctx.Session.Do("New Folder", () => made = GroupOps.CreateFolder(ctx.Doc, ids, null, ids.Count == 0 ? inside : null).Id);
        if (made is not { } id || ctx.Doc.FindGroup(id) is not { } folder) return;
        ctx.ShowDock(PanelId);
        Reveal(ctx.Doc, id);
        BeginRename(ctx.Doc, id);
        ctx.Status(ids.Count == 0 ? $"Made {folder.Name}. Type a name, then drag rows onto it" : $"Moved {Count(ids.Count)} into {folder.Name}. Type a name for it");
    }

    static void MoveToTopLevel(EditorContext ctx)
    {
        var ids = ctx.Sel.MeshIds().Where(id => SceneOps.IsObject(ctx.Doc, id)).ToList();
        ctx.Session.Do("Move to Top Level", () =>
        {
            foreach (int id in ids) GroupOps.MoveTo(ctx.Doc, id, null);
            GroupOps.Prune(ctx.Doc);
        });
        ctx.Status($"Moved {Count(ids.Count)} to the top level");
    }

    /// <summary>Hides every object outside the selection (Alt+H brings them back).</summary>
    static void HideOthers(EditorContext ctx)
    {
        var keep = ctx.Sel.MeshIds();
        var doc = ctx.Doc;
        foreach (var e in doc.Entities.Where(e => keep.Contains(e.Id))) keep.UnionWith(e.Meshes);
        var others = doc.Meshes.Select(m => m.Id).Concat(doc.Entities.Select(e => e.Id)).Where(id => !keep.Contains(id)).ToList();
        ctx.Session.Do("Hide Others", () => SceneOps.SetHidden(ctx.Doc, others, true));
        ctx.Status($"Hid {Count(others.Count)}. Alt+H shows everything again");
    }

    // ---- Rename ----

    /// <summary>Opens a row's name for typing. An entity shows its own name, which may be empty (it then uses its class).</summary>
    void BeginRename(Document doc, int id)
    {
        _renaming = id;
        _renameText = doc.FindEntity(id) is { } e ? e.Name : SceneOps.NameOf(doc, id) ?? "";
        _focusRename = true;
        _anchor = _cursor = id;
    }

    /// <summary>
    /// What Rename (F2) renames when no panel offers a row: the active object, else a selected one (entities first, so a
    /// mesh entity rather than one of its meshes). In face, edge and vertex modes, the mesh holding the selection.
    /// </summary>
    public static int? RenameTarget(EditorContext ctx)
    {
        var doc = ctx.Doc;
        if (ctx.Sel.Active is { } a && SceneOps.IsObject(doc, a.Mesh)) return a.Mesh;
        return ctx.Sel.MeshIds().Where(id => SceneOps.IsObject(doc, id)).OrderBy(id => doc.FindEntity(id) == null).ThenBy(id => id)
            .Select(id => (int?)id).FirstOrDefault();
    }

    /// <summary>Shows the Scene tab, unfolds the tree to <see cref="RenameTarget"/> and opens its name for typing.</summary>
    public void RenameSelection(EditorContext ctx)
    {
        if (RenameTarget(ctx) is not { } id) return;
        ctx.ShowDock(PanelId);
        Reveal(ctx.Doc, id);
        BeginRename(ctx.Doc, id);
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

    /// <summary>The objects a row's eye and lock set: a mesh entity's meshes go with it.</summary>
    static List<int> Flagged(Document doc, OutlinerNode node) =>
        doc.FindEntity(node.Id) is { } e ? [e.Id, .. e.Meshes] : [node.Id];

    static void SetHidden(EditorContext ctx, OutlinerNode node, bool hidden) =>
        ctx.Session.Do(hidden ? $"Hide {node.Name}" : $"Show {node.Name}", () =>
        {
            if (node.IsContainer && ctx.Doc.FindGroup(node.Id) is { } g) GroupOps.SetHidden(ctx.Doc, g, hidden);
            else SceneOps.SetHidden(ctx.Doc, Flagged(ctx.Doc, node), hidden);
        });

    static void SetLocked(EditorContext ctx, OutlinerNode node, bool locked) =>
        ctx.Session.Do(locked ? $"Lock {node.Name}" : $"Unlock {node.Name}", () =>
        {
            if (node.IsContainer && ctx.Doc.FindGroup(node.Id) is { } g) GroupOps.SetLocked(ctx.Doc, g, locked);
            else SceneOps.SetLocked(ctx.Doc, Flagged(ctx.Doc, node), locked);
        });

    // ---- Drag and drop ----

    void DragSource(EditorContext ctx, List<OutlinerNode> rows, OutlinerNode node, bool selected)
    {
        if (!ImGui.BeginDragDropSource()) return;
        // Dragging a selected row drags every selected row; otherwise just this one.
        _dragged = selected ? rows.Where(r => IsSelected(ctx, OutlinerTree.Objects(ctx.Doc, r)) && !HasSelectedAncestor(ctx, rows, r)).ToList() : [node];
        ImGui.SetDragDropPayload(Payload, IntPtr.Zero, 0);
        ImGui.Text(_dragged.Count == 1 ? $"Move {_dragged[0].Name}" : $"Move {_dragged.Count} items");
        ImGui.TextColored(Theme.TextDim, "Drop on a folder or group to put it inside, on a row to put it beside, or below the list for the top level");
        ImGui.EndDragDropSource();
    }

    /// <summary>A row inside a selected group (or mesh entity) moves with it, so it isn't moved on its own.</summary>
    static bool HasSelectedAncestor(EditorContext ctx, List<OutlinerNode> rows, OutlinerNode row)
    {
        var doc = ctx.Doc;
        if (row.Kind == OutlinerKind.Mesh && MeshEntityOps.OwnerOf(doc, row.Id) is { } owner && IsSelected(ctx, [owner.Id])) return true;
        var g = row.IsContainer ? doc.FindGroup(row.Id) is { } self ? GroupOps.ParentOf(doc, self) : null : GroupOps.GroupOf(doc, row.Id);
        for (; g != null; g = GroupOps.ParentOf(doc, g))
            if (IsSelected(ctx, GroupOps.AllMembers(doc, g))) return true;
        return false;
    }

    /// <summary>A folder or group row takes drops inside it; any other row takes them beside it (into its group).</summary>
    void DropTarget(EditorContext ctx, OutlinerNode row)
    {
        if (!ImGui.BeginDragDropTarget()) return;
        unsafe
        {
            if (ImGui.AcceptDragDropPayload(Payload).NativePtr != null)
            {
                var doc = ctx.Doc;
                int at = row.Kind == OutlinerKind.Mesh && MeshEntityOps.OwnerOf(doc, row.Id) is { } owner ? owner.Id : row.Id;
                Move(ctx, row.IsContainer ? doc.FindGroup(row.Id) : GroupOps.GroupOf(doc, at));
            }
        }
        ImGui.EndDragDropTarget();
    }

    /// <summary>The space under the last row: dropping there moves items out of their groups; a click clears the selection.</summary>
    void DropZone(EditorContext ctx)
    {
        var avail = ImGui.GetContentRegionAvail();
        ImGui.InvisibleButton("##dropzone", new Vector2(MathF.Max(1, avail.X), MathF.Max(Theme.Px(24), avail.Y)));
        if (ImGui.IsItemClicked() && !ImGui.GetIO().KeyCtrl && !ImGui.GetIO().KeyShift)
        {
            ctx.Sel.Clear();
            _anchor = _cursor = null;
        }
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) ImGui.OpenPopup(SpaceMenu);
        if (!ImGui.BeginDragDropTarget()) return;
        unsafe
        {
            if (ImGui.AcceptDragDropPayload(Payload).NativePtr != null) Move(ctx, null);
        }
        ImGui.EndDragDropTarget();
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
                if (n.IsContainer)
                {
                    if (doc.FindGroup(n.Id) is { } g && !GroupOps.MoveTo(doc, g, dest)) refused++;
                    continue;
                }
                GroupOps.MoveTo(doc, n.Id, dest);
                // A mesh entity's meshes follow it, so selecting the folder or group selects the whole entity.
                if (doc.FindEntity(n.Id) is { } e)
                    foreach (int m in e.Meshes) GroupOps.MoveTo(doc, m, dest);
            }
            GroupOps.Prune(doc);
        });
        if (refused > 0) ctx.Status("A group can't go inside itself; that part of the move was skipped");
        else ctx.Status(target == null ? $"Moved {Count(items.Count)} to the top level" : $"Moved {Count(items.Count)} into {target.Name}");
    }

    static string Count(int n) => n == 1 ? "1 item" : $"{n} items";
}
