using System.Diagnostics;
using System.Numerics;
using Chip.Core.Dressing;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using Chip.Editor.Rendering;
using Chip.Editor.UI;
using ImGuiNET;

namespace Chip.Editor.Features.Dressing;

/// <summary>
/// Dressing in the editor (docs/dressing.md): keeps the level dressed by its rules file, re-dressing when the document
/// changes and checking the rules files for edits four times a second, so an agent (or you) editing the JSON sees the
/// result live. The Dressing rules window (Ctrl+Shift+D) edits the rules; Dress with (Ctrl+Shift+T) applies a rule to the
/// selection. Rule edits are written to the JSON file as undo steps (<see cref="EditRules"/>). ` toggles between the
/// dressed level and the bare blockout; editing always works on the blockout.
/// </summary>
public sealed class DressingFeature : IEditorFeature
{
    EditorContext _ctx = null!;
    Dresser _dresser = new(DiskFiles.Instance);
    string? _levelPath, _rulesPath;
    Document? _doc;
    int _version = -1;
    double _nextCheck;
    DressedLevel? _level;
    RuleSet? _announced;
    RulesWorkspace? _workspace;
    (RuleSet? Rules, string? Path) _workspaceKey;
    DressingRulesWindow _window = null!;
    readonly DressWithPopup _dressWith = new();

    /// <summary>Seconds between checks of the rules files on disk.</summary>
    const double CheckInterval = 0.25;

    public string? RulesPath => _rulesPath;

    /// <summary>The rule whose targets the viewport highlights (the row under the mouse in the window or Dress with).</summary>
    public string? HighlightRule { get; set; }

    /// <summary>View > Tag colours: every tagged face tinted by its tag, so untagged surfaces stand out.</summary>
    public bool TagColours { get; set; }

    public DressingRulesWindow Window => _window;

    public void Register(EditorContext ctx)
    {
        _ctx = ctx;
        _window = new DressingRulesWindow(this);
        ctx.Dressed = Current;
        ctx.ViewportTints = Tints;
        ctx.Session.FilesChanged += _ => Recheck();
        ctx.Session.FileError += (path, why) => ctx.Status($"Couldn't restore {Path.GetFileName(path)}: {why}", error: true);
        var c = ctx.Commands;
        c.Add("view.dressed", "Dressed view", ToggleView, "View", "`", () => Current() != null, "6-dressing", 0);
        c.Add(new Command
        {
            Id = "view.tagColours", Label = "Tag colours", Menu = "View", Group = "6-dressing", Order = 1,
            Execute = () => TagColours = !TagColours, IsChecked = () => TagColours,
            Description = "Tint every tagged face by its tag, so untagged surfaces stand out",
        });
        c.Add("dressing.rules", "Dressing rules...", () => _window.Open = !_window.Open, "Tools", "Ctrl+Shift+D", group: "8-dressing", order: 0);
        c.Add("dressing.dressWith", "Dress with...", () => _dressWith.Open(), "Edit", "Ctrl+Shift+T",
            () => TagOps.CanTag(ctx.Sel.Mode) && !ctx.Sel.IsEmpty, "5-tags", 1);
        c.Add("dressing.new", "New dressing rules file", Create, "Tools", null,
            () => ctx.Session.Path != null && Current() == null, "8-dressing", 1);
        c.Add("dressing.open", "Open rules file (JSON)", Open, "Tools", null, () => Current() != null, "8-dressing", 2);
        c.Add("dressing.sample", "Open dressing sample", OpenSample, "Tools", null, group: "8-dressing", order: 3);
        c.Add("dressing.bake", "Bake dressing", Bake, "Tools", null,
            () => Current() is { } level && DressingBake.CanBake(level, ctx.Sel.MeshIds()), "8-dressing", 4);
        ctx.AddSection(new DressingSection(this));
        ctx.AddPanel(ProxyHoverTip);
        ctx.AddPanel(_window.Draw);
        ctx.AddPanel(x => _dressWith.Draw(x, this));
    }

    // ---- Editing rules ----

    /// <summary>
    /// The rules file edits go to: the level's rules file, or where a new one goes (beside the level). Null for an unsaved
    /// level, which has nowhere to keep one.
    /// </summary>
    public string? EditPath => Current() != null ? _rulesPath
        : _ctx.Session.Path is { } level ? DressingRules.PathFor(_ctx.Doc, level, DiskFiles.Instance) ?? DressingRules.DefaultPathFor(level) : null;

    /// <summary>The level's rules as editable files, for display; rebuilt when the rules reload. Null for an unsaved level.</summary>
    public RulesWorkspace? Workspace()
    {
        var path = EditPath;
        if (path == null) return null;
        var key = (Current()?.Rules, path);
        if (_workspace == null || _workspaceKey != key)
        {
            _workspace = new RulesWorkspace(DiskFiles.Instance, path, LevelName);
            _workspaceKey = key;
        }
        return _workspace;
    }

    string? LevelName => _ctx.Session.Path is { } p ? Path.GetFileNameWithoutExtension(p) : null;

    /// <summary>
    /// One undo step changing the rules: <paramref name="change"/> edits a fresh copy of the rules files (return false to
    /// cancel) and every file it changed is written; <paramref name="docEdit"/> changes the level in the same step (scoped
    /// to <paramref name="meshIds"/>). Reports problems in the status bar. True if something changed.
    /// </summary>
    public bool EditRules(string label, Func<RulesWorkspace, bool> change, IReadOnlyCollection<int>? meshIds = null, Action? docEdit = null)
    {
        var path = EditPath;
        if (path == null) { _ctx.Status("Save the level first: its dressing rules live in a file beside it", error: true); return false; }
        var ws = new RulesWorkspace(DiskFiles.Instance, path, LevelName);
        if (!ws.CanEdit) { _ctx.Status(ws.MainError!, error: true); return false; }
        if (!change(ws))
        {
            if (ws.Problem != null) _ctx.Status(ws.Problem, error: true);
            return false;
        }
        var changes = ws.Changes();
        if (changes.Count == 0 && docEdit == null) return false;
        try
        {
            _ctx.Session.Do(label, meshIds ?? [], () =>
            {
                foreach (var (file, text) in changes) _ctx.Session.WriteFile(file, text);
                docEdit?.Invoke();
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _ctx.Status($"Couldn't save the rules: {e.Message}", error: true);
            return false;
        }
        if (!ws.Exists) WriteSchema(path);
        Recheck();
        LastSaved = (_ctx.Now, string.Join(", ", changes.Select(c => Path.GetFileName(c.Path))));
        return true;
    }

    /// <summary>When the last rule edit was written and to which files, for the rules window's "Saved" note.</summary>
    public (double At, string Files)? LastSaved { get; private set; }

    public double Now => _ctx.Now;

    /// <summary>Checks the rules files on the next frame instead of within a quarter second (after Chip wrote them).</summary>
    void Recheck()
    {
        _nextCheck = 0;
        _workspace = null;
    }

    // ---- Viewport tints ----

    IReadOnlyList<FaceTint>? _tints;
    (DressedLevel? Level, bool Dressed, string? Rule, bool Tags, int Version, Document? Doc) _tintKey;

    /// <summary>Stripe tint for a proxy kind in the blockout view (unknown kinds get a neutral one).</summary>
    public static Vector4 ProxyTint(string kind) => kind switch
    {
        "railing" => Theme.Rgb(0x3EC6C0, 0.45f),
        "stairs" => Theme.Rgb(0xA77BFF, 0.45f),
        _ => Theme.Rgb(0xC8C8C8, 0.4f),
    };

    /// <summary>Tag tints. No orange or yellow: those mean selection and hover, and vanish on the orange dev textures.</summary>
    static readonly Vector4[] TagPalette =
    [
        Theme.Rgb(0x4EA8FF, 0.55f), Theme.Rgb(0x59D98E, 0.55f), Theme.Rgb(0xFF7AB6, 0.55f), Theme.Rgb(0xA77BFF, 0.55f),
        Theme.Rgb(0x3EC6C0, 0.55f), Theme.Rgb(0xB8E04A, 0.55f), Theme.Rgb(0x8FE6FF, 0.55f), Theme.Rgb(0xC8D0E0, 0.55f),
    ];

    static readonly Dictionary<string, int> TagOrder = new(StringComparer.Ordinal);

    /// <summary>
    /// A colour per tag: the level's tags in name order take the palette in turn (so a level's first eight tags never share
    /// a colour), anything else falls back to a hash. The viewport and the rules window's chips agree.
    /// </summary>
    public static Vector4 TagColour(string tag)
    {
        if (TagOrder.TryGetValue(tag, out int i)) return TagPalette[i % TagPalette.Length];
        uint h = 2166136261;
        foreach (var ch in tag) { h ^= ch; h *= 16777619; }
        return TagPalette[h % (uint)TagPalette.Length];
    }

    /// <summary>Assigns <see cref="TagColour"/>s from the level's tags (call when they may have changed).</summary>
    public static void SetTagOrder(IEnumerable<string> tags)
    {
        TagOrder.Clear();
        foreach (var t in tags.Where(t => !t.StartsWith(Core.Geometry.TagSet.SkipPrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            TagOrder.TryAdd(t, TagOrder.Count);
    }

    IReadOnlyList<FaceTint>? Tints()
    {
        var level = Current();
        var key = (level, _ctx.ShowDressed, HighlightRule, TagColours, _ctx.Session.Version, _ctx.Doc);
        if (key == _tintKey) return _tints;
        _tintKey = key;
        var list = new List<FaceTint>();
        if (TagColours)
        {
            SetTagOrder(TagOps.Usage(_ctx.Doc).Select(u => u.Tag));
            foreach (var m in _ctx.Doc.Meshes)
            {
                if (m.Tags.FirstOrDefault() is { } meshTag) list.Add(new FaceTint(m.Id, -1, TagColour(meshTag), Striped: true));
                for (int f = 0; f < m.Faces.Count; f++)
                    if (m.Faces[f].Tags.FirstOrDefault(t => !t.StartsWith(Core.Geometry.TagSet.SkipPrefix, StringComparison.Ordinal)) is { } tag)
                        list.Add(new FaceTint(m.Id, f, TagColour(tag)));
            }
        }
        else if (level != null && !_ctx.ShowDressed)
            foreach (var (id, _, kind) in level.Proxies) list.Add(new FaceTint(id, -1, ProxyTint(kind), Striped: true));
        if (level != null && HighlightRule != null)
            foreach (var (mesh, face) in level.Targets(HighlightRule)) list.Add(new FaceTint(mesh, face, Theme.Hover with { W = 0.38f }));
        return _tints = list.Count == 0 ? null : list;
    }

    // ---- Dressing ----

    /// <summary>
    /// Turns the selected meshes' dressing into ordinary editable meshes (one undo step); the meshes stop following the
    /// rules. For hand work on one spot without leaving the rules behind everywhere else.
    /// </summary>
    void Bake()
    {
        if (Current() is not { } level) return;
        var ids = _ctx.Sel.MeshIds();
        DressingBake.Result? result = null;
        _ctx.Session.Do("Bake dressing", () => result = DressingBake.Bake(_ctx.Doc, level, ids));
        if (result == null || result.Baked.Count == 0) return;
        _ctx.SetMode(SelectMode.Object);
        _ctx.Sel.Clear();
        foreach (var id in result.Added.Concat(result.Baked.Except(result.Removed))) _ctx.Sel.Add(ElementRef.Object(id));
        _ctx.Status($"Baked {result.Baked.Count} mesh{(result.Baked.Count == 1 ? "" : "es")} into {result.Added.Count} new: they no longer follow the rules (Ctrl+Z to undo)");
    }

    /// <summary>In the blockout view, hovering a proxy's box names the rule that replaces it.</summary>
    void ProxyHoverTip(EditorContext ctx)
    {
        if (ctx.ShowDressed || !ctx.ViewportHovered || ctx.Hover is not { } hover || Current() is not { } level) return;
        foreach (var (id, rule, kind) in level.Proxies)
            if (id == hover.Mesh)
            {
                ImGui.SetTooltip($"Becomes a {kind} (rule \"{rule}\"). Press ` to see it dressed.");
                return;
            }
    }

    /// <summary>The dressed level, or null when the level has no rules file. Cheap when nothing changed.</summary>
    public DressedLevel? Current()
    {
        var session = _ctx.Session;
        bool docChanged = !ReferenceEquals(session.Doc, _doc) || session.Version != _version || session.Path != _levelPath;
        bool check = _ctx.Now >= _nextCheck;
        if (!docChanged && !check) return _level;
        if (check) _nextCheck = _ctx.Now + CheckInterval;

        if (session.Path != _levelPath) _dresser = new Dresser(DiskFiles.Instance); // another level: fresh caches
        _levelPath = session.Path;
        string? path = docChanged || check ? DressingRules.PathFor(session.Doc, session.Path, DiskFiles.Instance) : _rulesPath;
        bool rulesChanged = check && _dresser.RulesChanged(path);
        if (!docChanged && !rulesChanged && path == _rulesPath) return _level;

        _doc = session.Doc;
        _version = session.Version;
        _rulesPath = path;
        _level = path == null ? null : _dresser.Dress(session.Doc, path, session.MeshRevision, checkFiles: rulesChanged);
        if (_level != null) RegisterPropTextures(_level);
        Announce();
        return _level;
    }

    readonly Dictionary<string, Core.IO.ImportedImage> _registered = new(StringComparer.Ordinal);

    /// <summary>
    /// Makes textures that came inside prop files into materials (written to a temp folder, since the renderer and the
    /// exporter read images from disk), so props draw and export with their own textures.
    /// </summary>
    void RegisterPropTextures(DressedLevel level)
    {
        foreach (var (name, image) in level.PropTextures)
        {
            if (_registered.TryGetValue(name, out var had) && ReferenceEquals(had, image) && _ctx.Materials.Contains(name)) continue;
            try
            {
                var dir = Path.Combine(Path.GetTempPath(), "chip-prop-textures");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + image.Extension);
                if (!File.Exists(file) || !File.ReadAllBytes(file).AsSpan().SequenceEqual(image.Data)) File.WriteAllBytes(file, image.Data);
                using var stream = new MemoryStream(image.Data);
                if (!MaterialLibrary.TryReadImageSize(stream, image.Extension, out int w, out int h)) continue;
                _ctx.Materials.Add(new MaterialInfo(name, file, w, h) { Folder = "props", Source = "dressing props" });
                _registered[name] = image;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _ctx.Status($"Couldn't use the texture inside {name.Split('/')[0]}: {e.Message}", error: true);
            }
        }
    }

    /// <summary>Says so in the status bar when rules (re)load, so edits to the file get feedback in the editor.</summary>
    void Announce()
    {
        if (_level == null || ReferenceEquals(_level.Rules, _announced)) return;
        bool first = _announced == null;
        _announced = _level.Rules;
        int errors = _level.Rules.Diagnostics.Count(d => d.IsError);
        string file = Path.GetFileName(_rulesPath!);
        if (errors > 0) _ctx.Status($"{file}: {errors} error{(errors == 1 ? "" : "s")} in the rules. See Dressing rules (Ctrl+Shift+D).", error: true);
        else if (!first && !_window.Open) _ctx.Status($"Reloaded {file}: {_level.Rules.Rules.Count(r => r.Enabled)} rules");
    }

    void ToggleView()
    {
        _ctx.ShowDressed = !_ctx.ShowDressed;
        _ctx.Status(_ctx.ShowDressed ? "Showing the dressed level" : "Showing the blockout. Export still includes the dressing.");
    }

    void Create()
    {
        var level = _ctx.Session.Path;
        if (level == null) { _ctx.Status("Save the level first: the rules file lives beside it", error: true); return; }
        var path = DressingRules.DefaultPathFor(level);
        try
        {
            if (!File.Exists(path)) File.WriteAllText(path, DressingSchema.Starter(Path.GetFileNameWithoutExtension(level)));
            WriteSchema(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _ctx.Status($"Couldn't create {Path.GetFileName(path)}: {e.Message}", error: true);
            return;
        }
        Recheck();
        _ctx.Status($"Created {Path.GetFileName(path)}. Select walls and Dress with (Ctrl+Shift+T), or edit the rules (Ctrl+Shift+D).");
    }

    /// <summary>Writes the sample room and its rules to a temp folder and opens it (after the unsaved-changes check).</summary>
    void OpenSample() => _ctx.ConfirmDiscard(() =>
    {
        try
        {
            var level = DressingSample.Write(Path.Combine(Path.GetTempPath(), "chip-dressing-sample"));
            _ctx.CancelInteraction();
            _ctx.Session.Reset(Core.IO.DocumentSerializer.Load(level), level);
            _ctx.FrameAll();
            Recheck();
            _window.Open = true;
            _ctx.Status("Opened the dressing sample. Change a rule in Dressing rules and watch the room update.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _ctx.Status($"Couldn't write the sample: {e.Message}", error: true);
        }
    });

    /// <summary>Opens the rules file in the default app (for hand edits; Chip reloads it on save).</summary>
    public void Open()
    {
        if (EditPath is not { } path || !File.Exists(path)) return;
        WriteSchema(path);
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception e) { _ctx.Status($"Couldn't open {Path.GetFileName(path)}: {e.Message}. It is at {path}", error: true); }
    }

    /// <summary>Keeps the JSON Schema beside the rules file current, so editors validate and complete it offline.</summary>
    static void WriteSchema(string rulesPath)
    {
        try
        {
            var schema = Path.Combine(Path.GetDirectoryName(rulesPath) ?? "", DressingSchema.FileName);
            var text = DressingSchema.Generate();
            if (!File.Exists(schema) || File.ReadAllText(schema) != text) File.WriteAllText(schema, text);
        }
        catch (IOException) { } // a read-only folder: the rules still work without the schema
        catch (UnauthorizedAccessException) { }
    }
}
