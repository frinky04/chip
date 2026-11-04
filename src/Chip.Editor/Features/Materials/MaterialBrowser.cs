using System.Numerics;
using System.Runtime.CompilerServices;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.UI;
using ImGuiNET;
using static Chip.Editor.UI.Widgets;

namespace Chip.Editor.Features.Materials;

/// <summary>
/// The Materials tab of the left dock (T), Scythe's material browser: search, All / Starred / Recent and a folder picker
/// built from the library folders, a virtualised thumbnail grid, and pinned under it the active material (its own texel
/// density), the Shift+Alt slots and Apply. Click makes a material active, double-click applies it to the selection,
/// dragging onto a face applies it there. One instance per context holds the session state (favourites, recent,
/// thumbnails) that the inspector's Material section shares.
/// </summary>
public sealed class MaterialBrowser : IDockPanel
{
    static readonly ConditionalWeakTable<EditorContext, MaterialBrowser> Instances = new();
    const int MaxRecent = 16;
    const string FavouritesKey = "*favourites", RecentKey = "*recent", AllKey = "*all";
    public const string PanelId = "materials";

    public static MaterialBrowser For(EditorContext ctx) => Instances.GetValue(ctx, _ => new MaterialBrowser());

    readonly List<string> _favourites = new();
    readonly List<string> _recent = new();
    string? _lastActive;
    string _search = "";
    string _folder = AllKey;
    float _thumb = 72;
    string? _dragging;
    float _footerH;

    // Filter cache: rebuilt when any input changes.
    (int Lib, string Search, string Folder, int Fav, int Recent) _filterKey = (-1, "", "", -1, -1);
    List<MaterialInfo> _shown = new();
    int _favVersion, _recentVersion;

    public string Id => PanelId;
    public string Title => "Materials";
    public Icon Icon => Icon.Palette;
    public int Order => 0;
    public string? Command => "materials.browser";

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

    /// <summary>Per-frame host callback, whether or not the tab shows: recent tracking, thumbnail uploads, drag and drop.</summary>
    public void Update(EditorContext ctx)
    {
        TrackRecent(ctx);
        Thumbs.Pump(ctx);
        UpdateDrag(ctx);
    }

    void UpdateDrag(EditorContext ctx)
    {
        if (_dragging == null) return;
        var mouse = ImGui.GetIO().MousePos;
        var rel = mouse - ctx.ViewportOrigin;
        bool overViewport = rel.X >= 0 && rel.Y >= 0 && rel.X < ctx.ViewportSize.X && rel.Y < ctx.ViewportSize.Y;
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

    /// <summary>The dock tab: search and filters, the grid, then the active material pinned at the bottom.</summary>
    public void Draw(EditorContext ctx)
    {
        var lib = ctx.Materials;
        var style = ImGui.GetStyle();
        float gap = Theme.Px(6);
        float more = ImGui.GetFrameHeight();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - more - gap);
        ImGui.InputTextWithHint("##search", "Search materials", ref _search, 128);
        ImGui.SameLine(0, gap);
        if (IconButton("more", Icon.Dots, "Thumbnail size, hotspot editor, texture folders, replace material", more)) ImGui.OpenPopup("##browserMenu");
        DrawMenu(ctx);

        DrawFilters(lib);
        var shown = Filtered(lib);

        float gridH = ImGui.GetContentRegionAvail().Y - _footerH;
        if (ImGui.BeginChild("##grid", new Vector2(0, MathF.Max(gridH, Theme.Px(80))), ImGuiChildFlags.None))
            DrawGrid(ctx, shown);
        ImGui.EndChild();

        float footerTop = ImGui.GetCursorPosY();
        DrawActive(ctx);
        _footerH = ImGui.GetCursorPosY() - footerTop + style.ItemSpacing.Y;
    }

    /// <summary>The ⋯ menu: thumbnail size, the hotspot editor, texture folders, and replacing materials in the level.</summary>
    void DrawMenu(EditorContext ctx)
    {
        if (!ImGui.BeginPopup("##browserMenu")) return;
        ImGui.TextColored(Theme.TextDim, "Thumbnails");
        ImGui.SetNextItemWidth(Theme.Px(200));
        ValueSlider("##thumb", "", ref _thumb, 40, 160, "%.0f px", t => $"{t:0} px");
        ImGui.Separator();
        foreach (var id in new[] { "hotspot.editor", "", "materials.addFolder", "materials.rescan", "", "materials.replace", "materials.applyDensity" })
        {
            if (id.Length == 0) ImGui.Separator();
            else if (ctx.Commands.Find(id) is { } c && ImGui.MenuItem(c.Label, c.HotkeyText, c.IsChecked?.Invoke() ?? false, c.Enabled)) c.Execute();
        }
        ImGui.EndPopup();
    }

    /// <summary>All / Starred / Recent, then the folder picker (a tree in a popup) filling the rest of the row.</summary>
    void DrawFilters(MaterialLibrary lib)
    {
        int active = _folder switch { AllKey => 0, FavouritesKey => 1, RecentKey => 2, _ => -1 };
        int picked = TextSegments("##quick", [("All", $"Every material ({lib.All.Count})"),
            ("Starred", "Materials you starred. Right-click a material to star it."), ("Recent", "Materials you made active lately")], active);
        if (picked >= 0) _folder = picked switch { 0 => AllKey, 1 => FavouritesKey, _ => RecentKey };
        float minCombo = Theme.Px(90);
        if (ImGui.GetContentRegionAvail().X - ImGui.GetItemRectSize().X - Theme.Px(6) >= minCombo) ImGui.SameLine(0, Theme.Px(6));
        ImGui.SetNextItemWidth(-1);
        string label = active >= 0 ? "Folder…" : FolderLabel(_folder);
        if (ImGui.BeginCombo("##folder", label, ImGuiComboFlags.HeightLarge))
        {
            DrawFolders(lib);
            ImGui.EndCombo();
        }
        Tip(active >= 0 ? "Show one texture folder" : $"Showing {label}. Pick another folder, or All.");
    }

    /// <summary>"concrete" for "C:/textures|walls/concrete", or the source folder's name for a whole source.</summary>
    static string FolderLabel(string key)
    {
        int bar = key.IndexOf('|');
        if (bar < 0) return key;
        var sub = key[(bar + 1)..];
        if (sub.Length > 0) return sub[(sub.LastIndexOf('/') + 1)..];
        var source = key[..bar];
        return Path.GetFileName(source) is { Length: > 0 } n ? n : source;
    }

    /// <summary>
    /// Pinned under the grid, Scythe's lower-left corner: the active material (star, size, tiling, its own texel density),
    /// the Shift+Alt slots, and Apply.
    /// </summary>
    void DrawActive(EditorContext ctx)
    {
        var lib = ctx.Materials;
        var active = lib.Get(ctx.ActiveMaterial);
        ImGui.Dummy(Theme.Px(0, 2));
        var dl = ImGui.GetWindowDrawList();
        var line = ImGui.GetCursorScreenPos();
        float x0 = ImGui.GetWindowPos().X;
        dl.AddLine(new Vector2(x0, line.Y), new Vector2(x0 + ImGui.GetWindowWidth(), line.Y), ImGui.ColorConvertFloat4ToU32(Theme.Border), Theme.Px(1));
        ImGui.Dummy(Theme.Px(0, 6));

        float big = Theme.Px(44);
        MaterialTiles.Tile(ctx, this, active, big, caption: false);
        ImGui.SameLine(0, Theme.Px(10));
        ImGui.BeginGroup();
        float star = ImGui.GetTextLineHeight();
        ImGui.PushFont(Fonts.Strong);
        ImGui.Text(MaterialTiles.Fit(active.Name, ImGui.GetContentRegionAvail().X - star - Theme.Px(8)));
        ImGui.PopFont();
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - star);
        bool fav = IsFavourite(active.Name);
        if (MaterialTiles.StarButton("##fav", fav, star, fav ? "Unstar" : "Star: pin it under Starred")) ToggleFavourite(active.Name);
        string tool = active.Surface != null ? "surface · " : "";
        ImGui.TextColored(Theme.TextDim, $"{tool}{active.Width} × {active.Height} px, tiles every {Labels.Length(active.TileWidth)}");
        ImGui.EndGroup();

        DrawOwnDensity(ctx, active);
        DrawAlpha(ctx, active);
        DrawSides(ctx, active);

        ImGui.Dummy(Theme.Px(0, 2));
        ImGui.TextColored(Theme.TextDim, "Slots");
        ImGui.SameLine();
        ImGui.TextColored(Theme.TextDisabled, "Shift+Alt+1..0");
        var slots = lib.All.Take(10).ToList();
        // Even rows that fill the width: all ten slots on one row when they fit at a usable size, else two rows of five.
        float spacing = Theme.Px(4), width = ImGui.GetContentRegionAvail().X;
        int perRow = (width - spacing * 9) / 10 >= Theme.Px(30) ? 10 : 5;
        float cell = MathF.Floor((width - spacing * (perRow - 1)) / perRow);
        for (int i = 0; i < slots.Count; i++)
        {
            if (i % perRow != 0) ImGui.SameLine(0, spacing);
            var t = MaterialTiles.Tile(ctx, this, slots[i], cell, caption: false, badge: ((i + 1) % 10).ToString());
            if (t.Clicked) ctx.ActiveMaterial = slots[i].Name;
            if (t.DoubleClicked) ApplyToSelection(ctx, slots[i].Name);
        }

        ImGui.Dummy(Theme.Px(0, 2));
        bool canApply = ctx.Sel.FaceSets(ctx.Doc).Count > 0;
        ImGui.BeginDisabled(!canApply);
        PushPrimary();
        if (ImGui.Button("Apply to selection", new Vector2(-1, 0))) ctx.Commands.Execute("texture.apply");
        PopPrimary();
        ImGui.EndDisabled();
        Tip(canApply ? "Apply the active material to the selection  (Shift+T)" : "Select faces or objects to apply  (Shift+T)");
    }

    /// <summary>The active texture's own density ("1 m is 128 px"), or following the level's (set in Level, with nothing selected).</summary>
    static void DrawOwnDensity(EditorContext ctx, MaterialInfo active)
    {
        bool custom = active.TexelDensity != null;
        bool canEdit = !MaterialLibrary.IsMissing(active) && active.Width > 0;
        RowLabel("Density", custom
            ? $"{active.Name} has its own texel density. Reset to follow the level's."
            : $"{active.Name} follows the level's texel density ({ctx.Materials.DefaultTexelsPerMeter:0.##} px per metre; change it under Level, with nothing selected). Change this to give the texture its own density (saved next to the texture).");
        float resetW = custom ? Theme.Px(60) + Theme.Px(6) : 0;
        ImGui.BeginDisabled(!canEdit);
        // Dimmed while the texture just follows the level; setting a value gives it its own density.
        if (DensityField("##textureDensity", active.TexelsPerMeter, out float own, resetW, dim: !custom))
            SetDensity(ctx, active, MathF.Abs(own - ctx.Materials.DefaultTexelsPerMeter) < 1e-3f ? null : own);
        ImGui.EndDisabled();
        if (custom)
        {
            ImGui.SameLine(0, Theme.Px(6));
            if (ImGui.Button("Reset", new Vector2(Theme.Px(60), 0))) SetDensity(ctx, active, null);
            Tip($"Follow the level again: 1 m is {ctx.Materials.DefaultTexelsPerMeter:0.##} px");
        }
    }

    static string AlphaLabel(AlphaMode m) => m switch { AlphaMode.Mask => "Cutout", AlphaMode.Blend => "Blend", _ => "Opaque" };

    /// <summary>How the texture's alpha draws and exports: read from the image (Auto), or set by hand (saved next to the texture).</summary>
    static void DrawAlpha(EditorContext ctx, MaterialInfo active)
    {
        if (active.Path == null) return; // surfaces drawn from their look, and the missing material, have no image alpha
        var detected = ctx.Materials.DetectAlpha(active.Path);
        RowLabel("Alpha", "How the texture's alpha draws in the viewport and exports (glTF alphaMode). Cutout: holes where "
            + "alpha is under 50 %, solid elsewhere (fences, foliage, grates). Blend: see-through by alpha, drawn after solid "
            + "faces (glass, water). Auto reads the image.");
        ImGui.SetNextItemWidth(-1);
        string auto = $"Auto: {AlphaLabel(detected)}";
        if (ImGui.BeginCombo("##alpha", active.Alpha is { } set ? AlphaLabel(set) : auto))
        {
            if (ImGui.Selectable(auto, active.Alpha == null)) SetAlpha(ctx, active, null);
            foreach (var mode in Enum.GetValues<AlphaMode>())
                if (ImGui.Selectable(AlphaLabel(mode), active.Alpha == mode)) SetAlpha(ctx, active, mode);
            ImGui.EndCombo();
        }
    }

    /// <summary>Whether faces with this material draw from both sides (saved next to the texture, exported as glTF doubleSided).</summary>
    static void DrawSides(EditorContext ctx, MaterialInfo active)
    {
        if (MaterialLibrary.IsMissing(active) || active.Surface is { IsTool: true }) return; // tool surfaces never render in the game
        RowLabel("Sides", "Double-sided faces draw from behind too, in the viewport and the export (glTF doubleSided): a "
            + "single-plane fence or foliage card. Leave it off for blocks, whose back faces are never seen. Rendering only: "
            + "a single-plane collider may still block from the front alone (Godot's trimesh default).");
        bool on = active.DoubleSided;
        if (ImGui.Checkbox("Double-sided##sides", ref on)
            && !ctx.Materials.SetDoubleSided(active.Name, on))
            ctx.Status($"Couldn't write {MaterialLibrary.SidecarName} in {active.Source}. The change applies until Chip closes; check the folder is writable.", error: true);
    }

    static void SetAlpha(EditorContext ctx, MaterialInfo m, AlphaMode? mode)
    {
        if (!ctx.Materials.SetAlphaMode(m.Name, mode))
            ctx.Status($"Couldn't write {MaterialLibrary.SidecarName} in {m.Source}. The change applies until Chip closes; check the folder is writable.", error: true);
    }

    static void UseActualSize(EditorContext ctx, MaterialInfo m)
    {
        bool saved = ctx.Materials.ResetSize(m.Name);
        ctx.Session.Touch(); // sizes changed: rebuild the scene's UVs
        if (!saved)
            ctx.Status($"Couldn't write {MaterialLibrary.SidecarName} in {m.Source}. The change applies until Chip closes; check the folder is writable.", error: true);
    }

    static void SetDensity(EditorContext ctx, MaterialInfo m, float? density)
    {
        bool saved = ctx.Materials.SetTexelDensity(m.Name, density);
        var now = ctx.Materials.Get(m.Name);
        if (!saved)
            ctx.Status($"Couldn't write {MaterialLibrary.SidecarName} in {now.Source}. The new size applies until Chip closes; check the folder is writable.", error: true);
        else
            ctx.Status($"{m.Name} tiles every {Labels.Length(now.TileWidth)}. Texture > Reset scale to material density updates existing faces");
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
        foreach (var group in lib.All.Where(m => m.Source.Length > 0).GroupBy(m => m.Source, StringComparer.OrdinalIgnoreCase))
        {
            var subs = group.Select(m => m.Folder).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
                .SelectMany(Ancestors).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
            string label = string.Equals(group.Key, lib.ProjectFolder, StringComparison.OrdinalIgnoreCase) ? "Project"
                : Path.GetFileName(group.Key) is { Length: > 0 } n ? n : group.Key;
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
        if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen())
        {
            _folder = key;
            ImGui.CloseCurrentPopup();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip(tip);
        if (open && children.Count > 0)
        {
            foreach (var c in children) FolderNode(source, c, c[(c.LastIndexOf('/') + 1)..], allSubs, c);
            ImGui.TreePop();
        }
    }

    void DrawGrid(EditorContext ctx, List<MaterialInfo> shown)
    {
        if (shown.Count == 0)
        {
            ImGui.Dummy(Theme.Px(0, 8));
            ImGui.TextColored(Theme.TextDim, _folder switch
            {
                FavouritesKey when _search.Length == 0 => "Nothing starred yet. Right-click a material to star it.",
                RecentKey when _search.Length == 0 => "Materials you make active show up here.",
                _ => _search.Length > 0 ? $"Nothing matches \"{_search}\"." : "No materials in this folder.",
            });
            return;
        }
        // The thumbnail size picks how many columns fit; the tiles then stretch to fill the row, so no strip is left over.
        bool caption = _thumb >= 56;
        float spacing = Theme.Px(8);
        float captionH = caption ? ImGui.GetTextLineHeight() * (Fonts.SmallSize / Fonts.BodySize) + Theme.Px(4) : 0;
        int perRow = 0;
        float size = 0;
        void Fit(float width)
        {
            perRow = Math.Max(1, (int)((width + spacing) / (Theme.Px(_thumb) + spacing)));
            size = MathF.Floor((width - spacing * (perRow - 1)) / perRow);
        }
        // Whether a scrollbar is needed is decided from the full width, never from whether one shows now, so a layout
        // that only just overflows can't flip between the two every frame.
        float full = ImGui.GetWindowWidth();
        Fit(full);
        if ((shown.Count + perRow - 1) / perRow * (size + captionH + spacing) > ImGui.GetWindowHeight())
            Fit(full - ImGui.GetStyle().ScrollbarSize);
        float rowH = size + captionH + spacing;
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
            if (ImGui.MenuItem(IsFavourite(m.Name) ? "Unstar" : "Star")) ToggleFavourite(m.Name);
            ImGui.Separator();
            if (ImGui.MenuItem("Replace in level with this…")) ReplaceMaterialDialog.For(ctx).Show(ctx, to: m.Name);
            if (ImGui.MenuItem("Copy name")) ImGui.SetClipboardText(m.Name);
            if (m.Pixels is { } pixels)
            {
                if (ImGui.MenuItem($"Tile by actual size ({pixels.Width} × {pixels.Height})")) UseActualSize(ctx, m);
                Tip($"The image was resized, so faces still tile it as {m.Width} × {m.Height}. This makes them use its real size, which changes how it sits on every face using it");
            }
            ImGui.EndPopup();
        }
    }
}
