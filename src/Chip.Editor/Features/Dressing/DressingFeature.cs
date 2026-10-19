using System.Diagnostics;
using Chip.Core.Dressing;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using ImGuiNET;

namespace Chip.Editor.Features.Dressing;

/// <summary>
/// Dressing in the editor (docs/dressing.md): keeps the level dressed by its rules file, re-dressing when the document
/// changes and checking the rules files for edits four times a second, so an agent (or you) editing the JSON sees the
/// result live. ` toggles between the dressed level and the bare blockout; editing always works on the blockout.
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

    /// <summary>Seconds between checks of the rules files on disk.</summary>
    const double CheckInterval = 0.25;

    public string? RulesPath => _rulesPath;

    public void Register(EditorContext ctx)
    {
        _ctx = ctx;
        ctx.Dressed = Current;
        var c = ctx.Commands;
        c.Add("view.dressed", "Dressed view", ToggleView, "View", "`", () => Current() != null, "6-dressing", 0);
        c.Add("dressing.new", "New dressing rules file", Create, "Tools", null,
            () => ctx.Session.Path != null && Current() == null, "8-dressing", 0);
        c.Add("dressing.open", "Open dressing rules", Open, "Tools", null, () => Current() != null, "8-dressing", 1);
        c.Add("dressing.sample", "Open dressing sample", OpenSample, "Tools", null, group: "8-dressing", order: 2);
        c.Add("dressing.bake", "Bake dressing", Bake, "Tools", null,
            () => Current() is { } level && DressingBake.CanBake(level, ctx.Sel.MeshIds()), "8-dressing", 3);
        ctx.AddSection(new DressingSection(this));
        ctx.AddPanel(ProxyHoverTip);
    }

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
        Announce();
        return _level;
    }

    /// <summary>Says so in the status bar when rules (re)load, so edits to the file get feedback in the editor.</summary>
    void Announce()
    {
        if (_level == null || ReferenceEquals(_level.Rules, _announced)) return;
        bool first = _announced == null;
        _announced = _level.Rules;
        int errors = _level.Rules.Diagnostics.Count(d => d.IsError);
        string file = Path.GetFileName(_rulesPath!);
        if (errors > 0) _ctx.Status($"{file}: {errors} error{(errors == 1 ? "" : "s")} in the rules. See Dressing in the inspector.", error: true);
        else if (!first) _ctx.Status($"Reloaded {file}: {_level.Rules.Rules.Count(r => r.Enabled)} rules");
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
        _nextCheck = 0;
        _ctx.Status($"Created {Path.GetFileName(path)}. Tag walls with Ctrl+T; Chip reloads the file whenever it changes.");
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
            _nextCheck = 0;
            _ctx.Status($"Opened the dressing sample. Edit {Path.GetFileName(DressingRules.DefaultPathFor(level))} (Tools > Open dressing rules) and watch it update.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _ctx.Status($"Couldn't write the sample: {e.Message}", error: true);
        }
    });

    void Open()
    {
        if (_rulesPath == null) return;
        WriteSchema(_rulesPath);
        try { Process.Start(new ProcessStartInfo(_rulesPath) { UseShellExecute = true }); }
        catch (Exception e) { _ctx.Status($"Couldn't open {Path.GetFileName(_rulesPath)}: {e.Message}. It is at {_rulesPath}", error: true); }
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
