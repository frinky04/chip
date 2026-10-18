using System.Numerics;
using System.Runtime.CompilerServices;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.Materials;

/// <summary>
/// The material browser (T): a floating window over the viewport's right side with search, a folder tree built from the
/// library folders, favourites and recent materials, and a virtualised thumbnail grid. Click makes a material active,
/// double-click applies it to the selection, dragging onto a face applies it there. One instance per context holds the
/// session state (favourites, recent, thumbnails) that the inspector's Material section shares.
/// </summary>
public sealed class MaterialBrowser
{
    static readonly ConditionalWeakTable<EditorContext, MaterialBrowser> Instances = new();
    const int MaxRecent = 16;
    const string FavouritesKey = "*favourites", RecentKey = "*recent", AllKey = "*all";

    public static MaterialBrowser For(EditorContext ctx) => Instances.GetValue(ctx, _ => new MaterialBrowser());

    readonly List<string> _favourites = new();
    readonly List<string> _recent = new();
    string? _lastActive;
    string _search = "";
    string _folder = AllKey;
    float _thumb = 80;
    string? _dragging;
    Vector2 _winMin, _winMax;
    bool _wasOpen;

    // Filter cache: rebuilt when any input changes.
    (int Lib, string Search, string Folder, int Fav, int Recent) _filterKey = (-1, "", "", -1, -1);
    List<MaterialInfo> _shown = new();
    int _favVersion, _recentVersion;

    public bool Open { get; set; }
    public ThumbnailCache Thumbs { get; } = new();
    public IReadOnlyList<string> Favourites => _favourites;
    public IReadOnlyList<string> Recent => _recent;

    public bool IsFavourite(string name) => _favourites.Contains(name, StringComparer.OrdinalIgnoreCase);

    public void ToggleFavourite(string name)
    {
        int i = _favourites.FindIndex(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) _favourites.RemoveAt(i); else _favourites.Add(name);
        _favVersion++;
    }

    /// <summary>Most recent first; updated whenever the active material changes, however it changed.</summary>
    void TrackRecent(EditorContext ctx)
    {
        if (string.Equals(_lastActive, ctx.ActiveMaterial, StringComparison.Ordinal)) return;
        bool first = _lastActive == null;
        _lastActive = ctx.ActiveMaterial;
        if (first) return; // the startup default isn't a choice
        _recent.RemoveAll(r => string.Equals(r, ctx.ActiveMaterial, StringComparison.OrdinalIgnoreCase));
        _recent.Insert(0, ctx.ActiveMaterial);
        if (_recent.Count > MaxRecent) _recent.RemoveAt(_recent.Count - 1);
        _recentVersion++;
    }

    /// <summary>Applies a material to every addressed face (one undo step), keeping relative scale across densities.</summary>
    public static void ApplyToSelection(EditorContext ctx, string material)
    {
        ctx.ActiveMaterial = material;
        ctx.ForFaces("Apply Material", (mesh, faces) =>
        {
            foreach (var f in faces) ctx.Materials.Assign(mesh.Faces[f], material);
        });
    }

    /// <summary>Drop target: the face under the mouse, or the whole selection when that face (or its object) is selected.</summary>
    static void ApplyAtMouse(EditorContext ctx, string material)
    {
        if (ctx.RaycastFace(selectable: true) is not { } hit) { ctx.Status("Drop the material on a face to apply it"); return; }
        var sel = ctx.Sel;
        bool inSelection = sel.Mode switch
        {
            SelectMode.Face => sel.Items.Contains(ElementRef.Face(hit.Mesh.Id, hit.Face)),
            SelectMode.Object => sel.Items.Contains(ElementRef.Object(hit.Mesh.Id)),
            _ => false,
        };
        ctx.ActiveMaterial = material;
        if (inSelection) { ApplyToSelection(ctx, material); return; }
        ctx.Session.Do("Apply Material", [hit.Mesh.Id], () =>
        {
            if (sel.Mode == SelectMode.Object) foreach (var face in hit.Mesh.Faces) ctx.Materials.Assign(face, material);
            else ctx.Materials.Assign(hit.Mesh.Faces[hit.Face], material);
        });
        ctx.Status($"Applied {material}");
    }

    /// <summary>Per-frame host callback: recent tracking, thumbnail uploads, drag and drop, then the window when open.</summary>
    public void Draw(EditorContext ctx)
    {
        TrackRecent(ctx);
        Thumbs.Pump(ctx);
        UpdateDrag(ctx);
        if (!Open) { _wasOpen = false; return; }

        float margin = Theme.Px(10);
        var vpSize = ctx.ViewportSize;
        float w = MathF.Min(Theme.Px(480), MathF.Max(Theme.Px(300), vpSize.X * 0.45f));
        float top = Theme.Px(44); // clear the viewport header's grid and snap controls
        var pos = ctx.ViewportOrigin + new Vector2(vpSize.X - w - margin, top);
        ImGui.SetNextWindowPos(pos, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(w, MathF.Max(Theme.Px(240), vpSize.Y - top - margin)), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(Theme.Px(280, 200), new Vector2(float.MaxValue));
        if (!_wasOpen) ImGui.SetNextWindowFocus();
        _wasOpen = true;
        bool open = true;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.Px(10, 8));
        bool visible = ImGui.Begin("Materials###materialBrowser", ref open, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar);
        ImGui.PopStyleVar();
        if (visible)
        {
            _winMin = ImGui.GetWindowPos();
            _winMax = _winMin + ImGui.GetWindowSize();
            DrawContents(ctx);
        }
        ImGui.End();
        if (!open) Open = false;
    }

    void UpdateDrag(EditorContext ctx)
    {
        if (_dragging == null) return;
        var mouse = ImGui.GetIO().MousePos;
        bool overBrowser = Open && mouse.X >= _winMin.X && mouse.Y >= _winMin.Y && mouse.X < _winMax.X && mouse.Y < _winMax.Y;
        var rel = mouse - ctx.ViewportOrigin;
        bool overViewport = rel.X >= 0 && rel.Y >= 0 && rel.X < ctx.ViewportSize.X && rel.Y < ctx.ViewportSize.Y && !overBrowser;
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            var material = _dragging;
            _dragging = null;
            if (overViewport) ApplyAtMouse(ctx, material);
            return;
        }
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) { _dragging = null; return; }
        if (ImGui.BeginTooltip())
        {
            var m = ctx.Materials.Get(_dragging);
            nint tex = Thumbs.Get(ctx, m);
            if (tex != 0) { ImGui.Image(tex, new Vector2(Theme.Px(40))); ImGui.SameLine(); }
            ImGui.BeginGroup();
            ImGui.Text(m.Name);
            ImGui.TextColored(Theme.TextDim, overViewport ? "Release to apply to this face" : "Drop on a face to apply");
            ImGui.EndGroup();
            ImGui.EndTooltip();
        }
    }

    void DrawContents(EditorContext ctx)
    {
        var lib = ctx.Materials;
        float gap = Theme.Px(8);
        float sliderW = Theme.Px(96);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - sliderW - gap);
        ImGui.InputTextWithHint("##search", "Search materials", ref _search, 128);
        ImGui.SameLine(0, gap);
        ImGui.SetNextItemWidth(sliderW);
        Widgets.ValueSlider("##thumb", "", ref _thumb, 40, 160, "%.0f px", t => $"{t:0} px");
        Tip($"Thumbnail size: {_thumb:0} px");

        var shown = Filtered(lib);
        float footerH = ImGui.GetFrameHeightWithSpacing();
        var avail = ImGui.GetContentRegionAvail();
        float treeW = MathF.Min(Theme.Px(150), avail.X * 0.4f);

        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.Bg);
        if (ImGui.BeginChild("##folders", new Vector2(treeW, avail.Y - footerH), ImGuiChildFlags.None))
            DrawFolders(lib);
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.SameLine(0, gap);
        if (ImGui.BeginChild("##grid", new Vector2(0, avail.Y - footerH), ImGuiChildFlags.None))
            DrawGrid(ctx, shown);
        ImGui.EndChild();

        ImGui.AlignTextToFramePadding();
        if (_search.Length > 0)
        {
            ImGui.TextColored(Theme.TextDim, shown.Count == 1 ? "1 match" : $"{shown.Count} matches");
            ImGui.SameLine(0, Theme.Px(16));
        }
        KeyHints([("Click", "activate"), ("Double-click", "apply"), ("Drag", "onto a face")]);
    }

    List<MaterialInfo> Filtered(MaterialLibrary lib)
    {
        var key = (lib.Version, _search, _folder, _favVersion, _recentVersion);
        if (key == _filterKey) return _shown;
        _filterKey = key;
        IEnumerable<MaterialInfo> items = _folder switch
        {
            FavouritesKey => _favourites.Where(lib.Contains).Select(lib.Get),
            RecentKey => _recent.Where(lib.Contains).Select(lib.Get),
            AllKey => lib.All,
            _ => lib.All.Where(m => InFolder(m, _folder)),
        };
        var terms = _search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length > 0)
            items = items.Where(m => terms.All(t => m.Name.Contains(t, StringComparison.OrdinalIgnoreCase) || m.Folder.Contains(t, StringComparison.OrdinalIgnoreCase)));
        _shown = items.ToList();
        return _shown;
    }

    /// <summary>Folder keys are "source|sub/path"; a folder includes its subfolders.</summary>
    static string FolderKey(string source, string sub) => source + "|" + sub;

    static bool InFolder(MaterialInfo m, string key)
    {
        int bar = key.IndexOf('|');
        if (bar < 0 || !string.Equals(m.Source, key[..bar], StringComparison.OrdinalIgnoreCase)) return false;
        var sub = key[(bar + 1)..];
        return sub.Length == 0 || string.Equals(m.Folder, sub, StringComparison.OrdinalIgnoreCase)
            || m.Folder.StartsWith(sub + "/", StringComparison.OrdinalIgnoreCase);
    }

    void DrawFolders(MaterialLibrary lib)
    {
        ImGui.Dummy(Theme.Px(0, 2));
        Entry(FavouritesKey, "Favourites", _favourites.Count(lib.Contains));
        Entry(RecentKey, "Recent", _recent.Count(lib.Contains));
        Entry(AllKey, "All materials", lib.All.Count);
        ImGui.Dummy(Theme.Px(0, 2));
        ImGui.Separator();
        ImGui.Dummy(Theme.Px(0, 2));

        foreach (var group in lib.All.Where(m => m.Source.Length > 0).GroupBy(m => m.Source, StringComparer.OrdinalIgnoreCase))
        {
            var subs = group.Select(m => m.Folder).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
                .SelectMany(Ancestors).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
            string label = Path.GetFileName(group.Key) is { Length: > 0 } n ? n : group.Key;
            FolderNode(group.Key, "", label, subs, group.Key);
        }
    }

    /// <summary>"a/b/c" → "a", "a/b", "a/b/c".</summary>
    static IEnumerable<string> Ancestors(string folder)
    {
        for (int i = folder.IndexOf('/'); i >= 0; i = folder.IndexOf('/', i + 1)) yield return folder[..i];
        yield return folder;
    }

    void FolderNode(string source, string sub, string label, List<string> allSubs, string tip)
    {
        var children = allSubs.Where(s => sub.Length == 0 ? !s.Contains('/') : s.StartsWith(sub + "/", StringComparison.OrdinalIgnoreCase) && !s[(sub.Length + 1)..].Contains('/')).ToList();
        string key = FolderKey(source, sub);
        var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.OpenOnDoubleClick;
        if (children.Count == 0) flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
        if (_folder == key) flags |= ImGuiTreeNodeFlags.Selected;
        if (sub.Length == 0) ImGui.SetNextItemOpen(true, ImGuiCond.Once);
        bool open = ImGui.TreeNodeEx(key, flags, label);
        if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen()) _folder = key;
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip(tip);
        if (open && children.Count > 0)
        {
            foreach (var c in children) FolderNode(source, c, c[(c.LastIndexOf('/') + 1)..], allSubs, c);
            ImGui.TreePop();
        }
    }

    void Entry(string key, string label, int count)
    {
        if (ImGui.Selectable($"{label}##{key}", _folder == key)) _folder = key;
        var cs = count.ToString();
        float w = ImGui.CalcTextSize(cs).X;
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - w - Theme.Px(4));
        ImGui.TextColored(Theme.TextDim, cs);
    }

    void DrawGrid(EditorContext ctx, List<MaterialInfo> shown)
    {
        if (shown.Count == 0)
        {
            ImGui.Dummy(Theme.Px(0, 8));
            ImGui.TextColored(Theme.TextDim, _folder switch
            {
                FavouritesKey when _search.Length == 0 => "No favourites yet. Right-click a material to add it.",
                RecentKey when _search.Length == 0 => "Materials you make active show up here.",
                _ => _search.Length > 0 ? $"Nothing matches \"{_search}\"." : "No materials in this folder.",
            });
            return;
        }
        float size = Theme.Px(_thumb);
        bool caption = _thumb >= 56;
        float spacing = Theme.Px(8);
        float captionH = caption ? ImGui.GetTextLineHeight() * (Fonts.SmallSize / Fonts.BodySize) + Theme.Px(4) : 0;
        float rowH = size + captionH + spacing;
        int perRow = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + spacing) / (size + spacing)));
        int rows = (shown.Count + perRow - 1) / perRow;

        // Only rows in view are laid out (and so only their thumbnails are requested).
        float scroll = ImGui.GetScrollY(), viewH = ImGui.GetWindowHeight();
        int first = Math.Clamp((int)(scroll / rowH) - 1, 0, rows);
        int last = Math.Clamp((int)((scroll + viewH) / rowH) + 1, 0, rows);
        var start = ImGui.GetCursorPos();
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(spacing));
        for (int r = first; r < last; r++)
        {
            ImGui.SetCursorPos(start + new Vector2(0, r * rowH));
            for (int c = 0; c < perRow; c++)
            {
                int i = r * perRow + c;
                if (i >= shown.Count) break;
                if (c > 0) ImGui.SameLine(0, spacing);
                DrawTile(ctx, shown[i], size, caption);
            }
        }
        ImGui.PopStyleVar();
        ImGui.SetCursorPos(start + new Vector2(0, rows * rowH));
        ImGui.Dummy(new Vector2(1, 0));
    }

    void DrawTile(EditorContext ctx, MaterialInfo m, float size, bool caption)
    {
        var r = MaterialTiles.Tile(ctx, this, m, size, caption);
        if (r.Clicked) ctx.ActiveMaterial = m.Name;
        if (r.DoubleClicked) ApplyToSelection(ctx, m.Name);
        if (r.Dragging) _dragging = m.Name;
        if (r.ContextMenu) ImGui.OpenPopup("##tileMenu" + m.Name);
        if (ImGui.BeginPopup("##tileMenu" + m.Name))
        {
            ImGui.TextColored(Theme.TextDim, m.Name);
            ImGui.Separator();
            if (ImGui.MenuItem("Make active")) ctx.ActiveMaterial = m.Name;
            bool canApply = ctx.Sel.FaceSets(ctx.Doc).Count > 0;
            if (ImGui.MenuItem("Apply to selection", null, false, canApply)) ApplyToSelection(ctx, m.Name);
            if (ImGui.MenuItem(IsFavourite(m.Name) ? "Remove from favourites" : "Add to favourites")) ToggleFavourite(m.Name);
            ImGui.Separator();
            if (ImGui.MenuItem("Replace in level with this…")) ReplaceMaterialDialog.For(ctx).Show(ctx, to: m.Name);
            if (ImGui.MenuItem("Copy name")) ImGui.SetClipboardText(m.Name);
            ImGui.EndPopup();
        }
    }
}
