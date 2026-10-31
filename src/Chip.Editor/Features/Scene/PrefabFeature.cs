using Chip.Core.IO;
using Chip.Core.Scene;
using Chip.Editor.Framework;
using NativeFileDialogSharp;

namespace Chip.Editor.Features.Scene;

/// <summary>
/// Prefab files (<see cref="Prefabs"/>): save a linked prop as one, open one to edit it on its own, place them from the
/// Place tab (<see cref="PrefabTool"/>), and follow them as they change: when a prefab file the level uses is written
/// (by another window, another level, or version control), its copies here are rebuilt from it.
/// </summary>
public sealed class PrefabFeature : IEditorFeature
{
    EditorContext _ctx = null!;
    int _seenResets = -1;
    double _nextPoll;
    /// <summary>Write times of the prefab files the level uses, as last seen.</summary>
    readonly Dictionary<string, DateTime> _written = new(StringComparer.OrdinalIgnoreCase);

    public void Register(EditorContext ctx)
    {
        _ctx = ctx;
        var tool = new PrefabTool();
        ctx.AddTool(tool);
        ctx.AddPanel(_ => Poll());
        var c = ctx.Commands;
        c.Add(new Command
        {
            Id = "prefab.save", Label = "Save as prefab…", Execute = SaveAsPrefab, Menu = "Edit", Group = "2-linked", Order = 3,
            CanExecute = () => ctx.Sel.Mode == SelectMode.Object && !ctx.Sel.IsEmpty,
            Description = "Saves the selected prop as a prefab file in the project, so every level can place linked copies of it",
        });
        c.Add(new Command
        {
            Id = "prefab.open", Label = "Open prefab", Execute = OpenPrefab, Menu = "Edit", Group = "2-linked", Order = 4,
            CanExecute = () => SelectedPrefab() != null,
            Description = "Opens the selected copy's prefab file to edit it on its own; levels using it update when it's saved",
        });
    }

    /// <summary>The prefab file the selection is a copy of (one only), or null.</summary>
    string? SelectedPrefab()
    {
        var defs = InstanceOps.SelectedCopies(_ctx.Doc, _ctx.Sel).Select(g => _ctx.Doc.FindDefinition(g.Instance)).Distinct().ToList();
        return defs is [{ Source: { } src }] && _ctx.Session.PrefabRoot is { } root ? Prefabs.FullPath(root, src) : null;
    }

    void SaveAsPrefab()
    {
        var doc = _ctx.Doc;
        if (_ctx.Session.PrefabRoot is not { } root) { _ctx.Status("Save the level first: prefabs are saved in its project", error: true); return; }
        var copies = InstanceOps.SelectedCopies(doc, _ctx.Sel);
        var defIds = copies.Select(g => g.Instance).Distinct().ToList();
        if (defIds.Count > 1) { _ctx.Status("Select copies of one prop to save it as a prefab", error: true); return; }
        if (defIds.Count == 1 && doc.FindDefinition(defIds[0])?.Source is { } existing)
        {
            _ctx.Status($"This prop is already the prefab {existing.Path}", error: true);
            return;
        }
        string? suggested = defIds.Count == 1 ? doc.FindDefinition(defIds[0])?.Name : null;
        var folder = Prefabs.DefaultFolder(root);
        Directory.CreateDirectory(folder);
        var result = Dialog.FileSave("chip", Path.Combine(folder, Safe(suggested ?? "prop") + Prefabs.Extension));
        if (!result.IsOk) return;
        var path = Path.GetFullPath(result.Path);
        if (!Prefabs.IsPrefabPath(path))
            path = (path.EndsWith(".chip", StringComparison.OrdinalIgnoreCase) ? path[..^5] : path) + Prefabs.Extension;
        if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            _ctx.Status($"Save prefabs inside the project folder ({root}) so levels can find them", error: true);
            return;
        }

        string? error = null;
        _ctx.Session.Begin("Save as Prefab", _ctx.Sel.MeshIds());
        try
        {
            var copy = copies.Count > 0 ? copies[0] : InstanceOps.Make(doc, _ctx.Sel.MeshIds());
            if (copy == null) error = "Only plain meshes can be a prefab: leave out entities and mesh entities";
            else
            {
                var (def, text) = Prefabs.Promote(doc.FindDefinition(copy.Instance)!, root, path);
                doc.Definitions = doc.Definitions.Select(d => d.Id == def.Id ? def : d).ToList();
                foreach (var g in InstanceOps.Copies(doc, def.Id)) g.Name = def.Name;
                _ctx.Session.WriteFile(path, text);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { error = $"Couldn't write {Path.GetFileName(path)}: {e.Message}"; }
        if (error != null)
        {
            _ctx.Session.Cancel();
            _ctx.Status(error, error: true);
            return;
        }
        _ctx.Session.End();
        PrefabIndex.Of(_ctx).Invalidate();
        _ctx.Status($"Saved prefab {Prefabs.Relative(root, path)}. Place it from the Place tab in any level of this project");
    }

    static string Safe(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Replace(' ', '_');
    }

    void OpenPrefab()
    {
        if (SelectedPrefab() is not { } path) return;
        _ctx.ConfirmDiscard(() =>
        {
            try
            {
                _ctx.CancelInteraction();
                _ctx.Session.Reset(DocumentSerializer.Load(path), path);
                _ctx.FrameAll();
                _ctx.Status($"Editing prefab {Path.GetFileName(path)}. Save it and levels using it update");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                _ctx.Status($"Couldn't open {Path.GetFileName(path)}: {e.Message}", error: true);
            }
        });
    }

    /// <summary>
    /// Once a second: reports problems with a level just opened, and rebuilds copies of prefab files written since
    /// (one undo step each). Never mid-drag.
    /// </summary>
    void Poll()
    {
        var session = _ctx.Session;
        if (_seenResets != session.Resets)
        {
            _seenResets = session.Resets;
            {
                _written.Clear();
                RecordTimes();
                if (session.PrefabProblems.Count > 0)
                    _ctx.Status(session.PrefabProblems.Count == 1 ? session.PrefabProblems[0]
                        : $"{session.PrefabProblems.Count} prefabs are missing or unreadable: {session.PrefabProblems[0]}", error: true);
            }
        }
        if (_ctx.Now < _nextPoll || session.InGesture || _ctx.Interacting || session.PrefabRoot is not { } root) return;
        _nextPoll = _ctx.Now + 1;
        var changed = new List<string>();
        foreach (var path in SourcePaths(root))
        {
            var t = WriteTime(path);
            if (_written.TryGetValue(path, out var was) && was != t) changed.Add(path);
            _written[path] = t;
        }
        if (changed.Count == 0) return;
        List<(int Definition, Prefabs.Status Status, string? Message)>? result = null;
        var set = changed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Only a file that differs from the level changes anything (this level's own writes come back as the same).
        var before = session.Doc.Definitions;
        session.Begin("Reload Prefab");
        result = Prefabs.Refresh(session.Doc, root, set.Contains);
        if (ReferenceEquals(before, session.Doc.Definitions)) { session.Cancel(); return; }
        session.End();
        var names = result.Where(r => r.Status == Prefabs.Status.Updated).Select(r => session.Doc.FindDefinition(r.Definition)?.Name).OfType<string>().ToList();
        if (names.Count > 0) _ctx.Status($"Prefab {string.Join(", ", names)} changed on disk: its copies here were updated");
    }

    void RecordTimes()
    {
        if (_ctx.Session.PrefabRoot is not { } root) return;
        foreach (var path in SourcePaths(root)) _written[path] = WriteTime(path);
    }

    IEnumerable<string> SourcePaths(string root) =>
        _ctx.Doc.Definitions.Where(d => d.Source != null).Select(d => Prefabs.FullPath(root, d.Source!)).Distinct(StringComparer.OrdinalIgnoreCase);

    static DateTime WriteTime(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }
}

/// <summary>The project's prefab files for the Place tab, scanned again every few seconds while shown.</summary>
public sealed class PrefabIndex
{
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<EditorContext, PrefabIndex> Table = new();
    public static PrefabIndex Of(EditorContext ctx) => Table.GetValue(ctx, _ => new PrefabIndex());

    string? _root;
    double _scanned = double.MinValue;
    List<PrefabInfo> _items = [];

    public void Invalidate() => _scanned = double.MinValue;

    public IReadOnlyList<PrefabInfo> Get(EditorContext ctx)
    {
        var root = ctx.Session.PrefabRoot;
        if (root == null) return [];
        if (root != _root || ctx.Now - _scanned > 5)
        {
            _root = root;
            _scanned = ctx.Now;
            _items = Prefabs.Scan(root);
        }
        return _items;
    }
}
